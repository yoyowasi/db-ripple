using System.Diagnostics;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Sql;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution;

/// <summary>
/// C10 미리보기 실행기(§4 C10, §5.2). 최종 차이는 주입받은 INetDiffer로 같은 연결에서 계산한다.
/// connectionString은 서버 연결 문자열(InitialCatalog는 실행기가 baseline.SandboxDb로 바꾼다, 풀링 끔). 값을 로그·예외 메시지에 넣지 않는다.
/// <para>
/// 실행 봉투: 전용 연결(풀링 끔) → SET 옵션 고정(XACT_ABORT ON + SqlClient 기본값) → @@OPTIONS 점검 → 실행 사용자 멱등 준비(트랜잭션 밖, Q12)
/// → T-SQL BEGIN TRAN → 제어 테이블 FULL·상한 → 경계 0 → 문장마다 [EXECUTE AS → 문장(별도 명령) → XACT_STATE·@@TRANCOUNT → (−1이면 ROLLBACK) → REVERT → 경계·이미지 수 → 상한이면 KEYS]
/// → 헤더·이미지 읽기 → 재조회 → 최종 차이 → 평가 훅 → ROLLBACK → 확정된 감사 기록 확인(남았으면 SandboxDirty). 실패면 멈추고 롤백 뒤 전체 사용자 테이블을 스냅샷과 비교한다(ResidualCommitted).
/// 문장마다 CURRENT_TRANSACTION_ID()를 BEGIN TRAN 직후 값과 비교해, 트리거의 "COMMIT; BEGIN TRAN;"으로 트랜잭션이 바뀌었으면 실패 경로로 보낸다(SandboxDirty).
/// </para>
/// </summary>
public sealed class PreviewExecutor(string connectionString, ExecutionSettings settings, INetDiffer netDiffer) : IPreviewExecutor
{
    readonly string connectionString = connectionString;

    /// <summary>실행 설정.</summary>
    public ExecutionSettings Settings { get; } = settings;

    /// <summary>최종 차이 계산기(C12).</summary>
    public INetDiffer NetDiffer { get; } = netDiffer;

    /// <summary>
    /// 실행 봉투 SET 옵션. SqlClient 기본값(@@OPTIONS 5432, 지문 sessionOptions와 같음)에 XACT_ABORT ON(E20)을 더한다.
    /// NOCOUNT는 OFF여야 드라이버 영향 행 수가 나온다(A9·SP2(c)).
    /// </summary>
    public const string EnvelopeSql = """
        SET XACT_ABORT ON;
        SET ANSI_NULLS ON;
        SET QUOTED_IDENTIFIER ON;
        SET ANSI_WARNINGS ON;
        SET ANSI_PADDING ON;
        SET CONCAT_NULL_YIELDS_NULL ON;
        SET NUMERIC_ROUNDABORT OFF;
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT OFF;
        SELECT @@OPTIONS;
        """;

    /// <summary>@@OPTIONS에 반드시 켜져 있어야 하는 비트: XACT_ABORT 16384, CONCAT_NULL_YIELDS_NULL 4096, QUOTED_IDENTIFIER 256, ANSI_NULLS 32, ANSI_PADDING 16, ANSI_WARNINGS 8.</summary>
    public const int RequiredOptions = 16384 | 4096 | 256 | 32 | 16 | 8;

    /// <summary>@@OPTIONS에 꺼져 있어야 하는 비트: NUMERIC_ROUNDABORT 8192, NOCOUNT 512, IMPLICIT_TRANSACTIONS 2.</summary>
    public const int ForbiddenOptions = 8192 | 512 | 2;

    /// <summary>경계 0: 감사 헤더 최댓값과 SEQUENCE 마지막 사용값(없으면 시작값 − 1) 중 큰 값. 기준선 설치가 감사 기록을 비우므로(ResetBatch) SEQUENCE도 본다.</summary>
    public const string Boundary0Sql = """
        SELECT CONVERT(bigint, (SELECT MAX(v) FROM (VALUES
            ((SELECT MAX(captureSeq) FROM audit.AuditHeader)),
            ((SELECT COALESCE(CONVERT(bigint, last_used_value), CONVERT(bigint, start_value) - 1)
              FROM sys.sequences WHERE object_id = OBJECT_ID(N'audit.CaptureSeq')))) AS x(v)));
        """;

    /// <inheritdoc />
    public async Task<RawCapture> ExecuteAsync(AnalysisResult analysis, BaselineContext baseline, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(baseline);
        if (!analysis.IsAccepted) throw new ArgumentException("거부된 분석 결과는 실행하지 않는다(§5.2 1)", nameof(analysis));
        var cap = options?.ImageCap ?? Settings.DefaultImageCap;
        if (cap < 0) throw new ArgumentOutOfRangeException(nameof(options), "이미지 상한은 0 이상");

        var tables = baseline.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).ToList();
        var csb = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = baseline.SandboxDb, Pooling = false };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        var session = new SqlSession { Connection = conn };

        var options0 = await ScalarAsync<int>(conn, EnvelopeSql, ct);
        if ((options0 & RequiredOptions) != RequiredOptions || (options0 & ForbiddenOptions) != 0)
            throw new InvalidOperationException($"실행 봉투 SET 옵션 점검 실패(@@OPTIONS = {options0})");

        var dirty = false;
        var user = Settings.ExecuteAsUser;
        if (user is not null) dirty = await EnsureUserAsync(conn, user, tables, baseline, ct);

        var timings = new Timings();
        var executions = new List<StatementExecution>();
        var tranOpen = false;
        var impersonating = false;
        long boundary0 = 0;
        long? tranId = null;
        long imageCount = 0;
        var mode = ImageMode.Full;
        var truncated = false;
        StatementStatus? failure = null;
        try
        {
            var sw = Stopwatch.StartNew();
            await ExecAsync(conn, "BEGIN TRAN;", ct);
            tranOpen = true;
            tranId = await ScalarAsync<long>(conn, "SELECT CURRENT_TRANSACTION_ID();", ct);
            await ExecAsync(conn, "UPDATE audit.AuditControl SET mode = 'FULL', cap = @cap WHERE id = 1;", ct, ("@cap", cap));
            boundary0 = await ScalarAsync<long>(conn, Boundary0Sql, ct);
            var prev = boundary0;

            foreach (var st in analysis.Statements.OrderBy(s => s.Index))
            {
                if (failure is not null)
                {
                    executions.Add(new StatementExecution { Result = Result(st, StatementStatus.NotRun), ModeAfter = mode, ImageCountAfter = imageCount });
                    continue;
                }
                if (user is not null)
                {
                    await ExecAsync(conn, $"EXECUTE AS USER = {SqlText.NLiteral(user)};", ct);
                    impersonating = true;
                }
                long? rows = null;
                SqlException? error = null;
                try
                {
                    // Q33(오케스트레이터 승인 2026-11-11, A안): StatementRunner와 같은 식으로 센다. D7 1(문장별 행 수)은 미리보기 값과 [적용] runner 값을 비교하므로
                    // 같은 방식이어야 한다(드라이버 영향 행 수는 NOCOUNT OFF 트리거 행까지 합산).
                    await using var cmd = new SqlCommand(st.Sql + StatementRunner.RowCountSuffix, conn) { CommandTimeout = Settings.CommandTimeoutSeconds };
                    rows = await StatementRunner.ReadRowCountAsync(cmd, ct);
                }
                catch (SqlException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException("미리보기 취소", ex, ct);
                    error = ex;
                }
                if (conn.State != System.Data.ConnectionState.Open) throw new SandboxUnavailableException("문장 실행 중 연결이 끊김(E20: 기준선 재구축 필요)", error);

                // 위험 6(ProbeTests·ErrorRuleTests 실측): XACT_ABORT ON에서 오류는 트랜잭션을 이미 롤백해 @@TRANCOUNT = 0으로 남기고, 그때 XACT_STATE()는 1로 읽혔다.
                // 그래서 @@TRANCOUNT를 먼저 본다. 트랜잭션이 남아 있고 doomed(−1)면 REVERT가 3930으로 실패하므로(SP10) ROLLBACK을 먼저 한다.
                var (xact, tranCount, currentTranId) = await StateAsync(conn, ct);
                if (tranCount == 0) tranOpen = false;
                else if (xact == -1) { await ExecAsync(conn, "ROLLBACK;", ct); tranOpen = false; }
                if (impersonating) { await ExecAsync(conn, "REVERT;", ct); impersonating = false; }

                // 트리거의 "COMMIT TRAN; BEGIN TRAN;"은 @@TRANCOUNT = 1을 남겨 오류 없이 지나간다. 트랜잭션 ID가 바뀌었으면 그 앞 변경은 이미 확정됐다(검토 재현 2026-11-10).
                // 인계 5: 트리거가 "COMMIT; BEGIN TRAN;" 뒤 RAISERROR를 내면 오류가 있어도 트랜잭션이 바뀌어 있다 → 오류가 있어도 확정으로 본다(failed).
                var tranReplaced = tranOpen && tranCount == 1 && currentTranId != tranId;
                if (tranReplaced) dirty = true;
                if (error is not null || !tranOpen || tranCount != 1 || tranReplaced)
                {
                    var (status, err) = error is not null
                        ? Replaced(ErrorRules.Classify(error, baseline), tranReplaced)
                        : (StatementStatus.Failed, new StatementError
                        {
                            ErrorNumber = 0,
                            Message = tranReplaced
                                ? $"문장 중 미리보기 트랜잭션이 확정되고 새 트랜잭션으로 바뀜(트리거의 COMMIT 뒤 BEGIN TRAN으로 보임, 트랜잭션 ID {tranId} → {currentTranId}). 확정된 변경은 롤백할 수 없음"
                                : $"문장 뒤 트랜잭션 상태가 예상과 다름(@@TRANCOUNT = {tranCount}, XACT_STATE = {xact})",
                            IsToolFault = !tranReplaced,
                        });
                    failure = status;
                    executions.Add(new StatementExecution { Result = Result(st, status) with { Error = err }, ModeAfter = mode, ImageCountAfter = imageCount });
                    continue;
                }

                var (min, max, images) = await BoundaryAsync(conn, prev, ct);
                imageCount += images;
                var boundary = max ?? prev;
                if (mode == ImageMode.Full && imageCount > cap)
                {
                    await ExecAsync(conn, "UPDATE audit.AuditControl SET mode = 'KEYS' WHERE id = 1;", ct);
                    mode = ImageMode.Keys;
                    truncated = true;
                }
                executions.Add(new StatementExecution
                {
                    Result = Result(st, StatementStatus.Ok) with { RowsAffected = rows, CaptureSeqRange = min is long a && max is long b ? [a, b] : null },
                    Boundary = boundary,
                    ImageCountAfter = imageCount,
                    ModeAfter = mode,
                });
                prev = boundary;
            }
            timings.Execute = sw.ElapsedMilliseconds;

            if (failure is not null)
            {
                // §5.2 4: 롤백 뒤 같은 연결에서 전체 사용자 테이블을 기준 스냅샷과 비교한다. 감사 기록은 함께 롤백되어 범위를 좁힐 수 없다.
                if (tranOpen) { await ExecAsync(conn, "ROLLBACK;", ct); tranOpen = false; }
                if (await CommittedAuditAsync(conn, boundary0, ct)) dirty = true;
                sw.Restart();
                // 인계 7(Q31): 이 비교가 예외(예 8115)로 끝나도 미리보기 실패 결과는 돌려준다. 남은 변경을 알 수 없으므로 샌드박스는 오염으로 둔다.
                IReadOnlyList<NetChange> residual = [];
                string? residualError = null;
                try
                {
                    residual = await NetDiffer.DiffAsync(session, baseline.SandboxDb, baseline.SnapshotDb, tables, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    if (conn.State != System.Data.ConnectionState.Open) throw new SandboxUnavailableException("롤백 뒤 남은 변경 검사 중 연결이 끊김(E20: 기준선 재구축 필요)", ex);
                    residualError = ResidualCheckMessage(ex);
                }
                timings.NetDiff = sw.ElapsedMilliseconds;
                // Q27 보완(인계 5): 트리거 거부(trigger_blocked)는 ROLLBACK이면 맞지만, 트리거가 COMMIT했으면 문장 변경이 확정된 것이다.
                // 롤백 뒤 사용자 테이블에 남은 변경이 있고 그 거부가 ROLLBACK 트리거에서 났다고 확인되지 않으면 failed로 바꾼다.
                // 대표 오류가 3609가 아니어도(COMMIT 뒤 RAISERROR: 50000 + 3609, 또는 COMMIT; BEGIN TRAN; RAISERROR) 같은 규칙이다. 검사 실패면 남은 변경을 모르므로 그대로 둔다.
                if (failure == StatementStatus.TriggerBlocked && residual.Count > 0
                    && executions[^1].Result is { Error: { } eb } last
                    && !ErrorRules.EndedByRollback(eb.Procedure, last.Target, last.Kind, baseline))
                {
                    failure = StatementStatus.Failed;
                    executions[^1] = executions[^1] with { Result = executions[^1].Result with { Status = StatementStatus.Failed } };
                }
                return new RawCapture
                {
                    Status = failure == StatementStatus.TriggerBlocked ? PreviewStatus.TriggerBlocked : PreviewStatus.Failed,
                    Executions = executions.Select(e => e with { Result = e.Result with { CaptureSeqRange = null } }).ToList(),
                    Boundary0 = boundary0,
                    ResidualCommitted = residual,
                    ResidualCheckFailed = residualError is not null,
                    ResidualCheckMessage = residualError,
                    SandboxDirty = dirty || residual.Count > 0 || residualError is not null,
                    Capture = new CaptureInfo { ImageCount = 0, Cap = cap, Truncated = false, ManifestVersion = baseline.Manifest.Version },
                    Timings = timings.ToContract(),
                };
            }

            sw.Restart();
            var headers = await ReadHeadersAsync(conn, boundary0, ct);
            var imageRows = await ReadImagesAsync(conn, boundary0, ct);
            timings.ReadLog = sw.ElapsedMilliseconds;

            sw.Restart();
            var rereads = await RereadAsync(conn, headers, imageRows, tables, ct);
            timings.Reread = sw.ElapsedMilliseconds;

            sw.Restart();
            var scope = Settings.EvaluationMode ? tables : NetDiffScope(headers, tables, baseline.Manifest);
            var net = await NetDiffer.DiffAsync(session, baseline.SandboxDb, baseline.SnapshotDb, scope, ct);
            timings.NetDiff = sw.ElapsedMilliseconds;

            var statements = executions.Select(e => e.Result).ToList();
            if (Settings.EvaluationMode && hook is not null)
            {
                await hook.OnBeforeRollbackAsync(new EvaluationHookContext
                {
                    Session = session,
                    SandboxDb = baseline.SandboxDb,
                    SnapshotDb = baseline.SnapshotDb,
                    ManifestNotes = baseline.Manifest.Notes,
                    Statements = statements,
                }, ct);
            }

            // 최종 ROLLBACK을 여기서 하고, 그 뒤에도 경계 0보다 큰 감사 기록이 남았으면 실행 중 트랜잭션이 확정된 것이다(오염).
            await ExecAsync(conn, "ROLLBACK;", ct);
            tranOpen = false;
            if (await CommittedAuditAsync(conn, boundary0, ct)) dirty = true;

            return new RawCapture
            {
                Status = truncated ? PreviewStatus.Truncated : PreviewStatus.Ok,
                Executions = executions,
                Boundary0 = boundary0,
                Headers = headers,
                Images = imageRows,
                Rereads = rereads,
                NetChanges = net,
                SandboxDirty = dirty,
                Capture = new CaptureInfo { ImageCount = imageCount, Cap = cap, Truncated = truncated, ManifestVersion = baseline.Manifest.Version },
                Timings = timings.ToContract(),
            };
        }
        finally
        {
            await CleanupAsync(conn, impersonating, tranOpen);
        }
    }

    /// <summary>트랜잭션이 바뀌었으면(트리거의 COMMIT 뒤 BEGIN TRAN) 오류 판정과 상관없이 failed(앞 변경이 확정됨, 인계 5). 오류 내용은 그대로 둔다.</summary>
    public static (StatementStatus Status, StatementError Error) Replaced((StatementStatus Status, StatementError Error) classified, bool tranReplaced) =>
        tranReplaced ? (StatementStatus.Failed, classified.Error) : classified;

    /// <summary>잔여 확인 실패 요약(Q31): "형식 [오류 번호] 메시지", 500자 이내. SqlException 메시지에는 연결 문자열이 들어가지 않는다.</summary>
    public static string ResidualCheckMessage(Exception ex)
    {
        var text = ex is SqlException sql ? $"{nameof(SqlException)} {sql.Number}: {sql.Message}" : $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 500 ? text : text[..500];
    }

    /// <summary>롤백 뒤 경계 0보다 큰 감사 헤더가 남았는지(감사 기록은 미리보기 트랜잭션 안에서만 쓰이므로, 남았다면 확정된 것이다).</summary>
    static async Task<bool> CommittedAuditAsync(SqlConnection conn, long boundary0, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM audit.AuditHeader WHERE captureSeq > @b0) THEN 1 ELSE 0 END;", conn);
        cmd.Parameters.Add(new SqlParameter("@b0", System.Data.SqlDbType.BigInt) { Value = boundary0 });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    /// <summary>finally 정리: 가장이 남았으면 REVERT(doomed면 3930 → ROLLBACK 뒤 다시), 트랜잭션이 남았으면 ROLLBACK. 실패는 삼킨다(연결을 닫으면 서버가 롤백한다).</summary>
    static async Task CleanupAsync(SqlConnection conn, bool impersonating, bool tranOpen)
    {
        if (conn.State != System.Data.ConnectionState.Open) return;
        try
        {
            if (impersonating)
            {
                try { await ExecAsync(conn, "REVERT;", CancellationToken.None); }
                catch (SqlException) { await ExecAsync(conn, "IF @@TRANCOUNT > 0 ROLLBACK;", CancellationToken.None); await ExecAsync(conn, "REVERT;", CancellationToken.None); }
            }
            await ExecAsync(conn, "IF @@TRANCOUNT > 0 ROLLBACK;", CancellationToken.None);
        }
        catch (SqlException) { /* 연결을 닫아 서버 쪽 롤백에 맡긴다 */ }
        catch (InvalidOperationException) { }
        _ = tranOpen;
    }

    /// <summary>실행 사용자 준비(Q12). 만들거나 권한을 줬고 그 DDL이 사용자 테이블을 바꿨을 수 있으면(DDL 트리거) true = 샌드박스 오염.</summary>
    async Task<bool> EnsureUserAsync(SqlConnection conn, string user, IReadOnlyList<TableInfo> tables, BaselineContext baseline, CancellationToken ct)
    {
        if (!string.Equals(user, PreviewUserSql.UserName, StringComparison.Ordinal))
        {
            // 다른 이름은 만들거나 권한을 주지 않는다(운영 계정과 같은 이름일 수 있음). 존재만 확인한다.
            var exists = await ScalarAsync<int>(conn, $"SELECT CASE WHEN USER_ID({SqlText.NLiteral(user)}) IS NULL THEN 0 ELSE 1 END;", ct);
            if (exists == 0) throw new InvalidOperationException($"실행 사용자 {user}가 샌드박스에 없음");
            return false;
        }
        var sql = "DECLARE @h0 bigint = (SELECT COUNT_BIG(*) FROM audit.AuditHeader);\n"
            + PreviewUserSql.EnsureBatch(tables.Select(t => t.Schema), user)
            + "SELECT COUNT_BIG(*) - @h0 FROM audit.AuditHeader;\n";
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = Settings.CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var ddl = r.GetBoolean(0);
        await r.NextResultAsync(ct);
        await r.ReadAsync(ct);
        var auditDelta = r.GetInt64(0);
        var ddlTriggers = baseline.Catalog.Triggers.Any(t => t.ParentClass == "DATABASE" && !t.IsDisabled);
        return auditDelta > 0 || (ddl && ddlTriggers);
    }

    /// <summary>정상 경로 최종 차이 범위(D3 대조 2): 감사 기록에 나온 테이블 ∪ 제외 테이블.</summary>
    static IReadOnlyList<TableInfo> NetDiffScope(IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<TableInfo> tables, CaptureManifest manifest)
    {
        var ids = headers.Select(h => h.TableObjectId).ToHashSet();
        var excluded = manifest.ExcludedTables.Select(e => e.Table).ToHashSet(StringComparer.Ordinal);
        return tables.Where(t => ids.Contains(t.ObjectId) || excluded.Contains(t.DisplayName)).ToList();
    }

    static StatementResult Result(AnalyzedStatement st, StatementStatus status) => new()
    {
        Index = st.Index,
        Batch = st.Batch,
        Position = st.Position,
        Kind = st.Kind,
        Target = st.Target,
        Span = st.Span,
        Status = status,
    };

    static async Task<(int Xact, int TranCount, long? TranId)> StateAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT XACT_STATE(), @@TRANCOUNT, CURRENT_TRANSACTION_ID();", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (Convert.ToInt32(r.GetValue(0)), r.GetInt32(1), r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2)));
    }

    static async Task<(long? Min, long? Max, long Images)> BoundaryAsync(SqlConnection conn, long prev, CancellationToken ct)
    {
        const string sql = """
            SELECT MIN(captureSeq), MAX(captureSeq),
                   (SELECT COUNT_BIG(*) FROM audit.AuditRowImage WHERE captureSeq > @prev AND rowJson IS NOT NULL)
            FROM audit.AuditHeader WHERE captureSeq > @prev;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@prev", System.Data.SqlDbType.BigInt) { Value = prev });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2));
    }

    static async Task<IReadOnlyList<AuditHeaderRow>> ReadHeadersAsync(SqlConnection conn, long boundary0, CancellationToken ct)
    {
        const string sql = """
            SELECT captureSeq, tableObjectId, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode
            FROM audit.AuditHeader WHERE captureSeq > @b0 ORDER BY captureSeq;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@b0", System.Data.SqlDbType.BigInt) { Value = boundary0 });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditHeaderRow>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditHeaderRow
            {
                CaptureSeq = r.GetInt64(0),
                TableObjectId = r.GetInt32(1),
                TableName = r.GetString(2),
                Operation = r.GetString(3) switch { "I" => ChangeOperation.Insert, "U" => ChangeOperation.Update, _ => ChangeOperation.Delete },
                InsertedCount = r.GetInt64(4),
                DeletedCount = r.GetInt64(5),
                NestLevel = r.GetInt32(6),
                TranCount = r.GetInt32(7),
                ImageMode = r.GetString(8) == "KEYS" ? ImageMode.Keys : ImageMode.Full,
            });
        }
        return list;
    }

    static async Task<IReadOnlyList<AuditRowImageRow>> ReadImagesAsync(SqlConnection conn, long boundary0, CancellationToken ct)
    {
        const string sql = """
            SELECT captureSeq, imageKind, ordinal, keyJson, rowJson
            FROM audit.AuditRowImage WHERE captureSeq > @b0 ORDER BY captureSeq, imageKind, ordinal;
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@b0", System.Data.SqlDbType.BigInt) { Value = boundary0 });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRowImageRow>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditRowImageRow
            {
                CaptureSeq = r.GetInt64(0),
                ImageKind = r.GetString(1) == "INSERTED" ? ImageKind.Inserted : ImageKind.Deleted,
                Ordinal = r.GetInt32(2),
                KeyJson = r.IsDBNull(3) ? null : r.GetString(3),
                RowJson = r.IsDBNull(4) ? null : r.GetString(4),
            });
        }
        return list;
    }

    /// <summary>
    /// 재조회(D3 대조 1): FULL 묶음에 나온 키(키 있는 테이블)의 현재 행을 감사 트리거와 같은 식(Core AuditValueExpressions)으로 FOR JSON한다.
    /// KEYS 묶음의 키는 비교할 감사 after가 없으므로 읽지 않는다(상한 이내로 비용을 묶는다).
    /// </summary>
    static async Task<IReadOnlyList<RereadRow>> RereadAsync(SqlConnection conn, IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<AuditRowImageRow> images,
        IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        var byId = tables.ToDictionary(t => t.ObjectId);
        var full = headers.Where(h => h.ImageMode == ImageMode.Full).ToDictionary(h => h.CaptureSeq, h => h.TableObjectId);
        var keysByTable = images.Where(i => i.KeyJson is not null && full.ContainsKey(i.CaptureSeq))
            .GroupBy(i => full[i.CaptureSeq])
            .Where(g => byId.ContainsKey(g.Key))
            .OrderBy(g => byId[g.Key].DisplayName, StringComparer.Ordinal);
        var result = new List<RereadRow>();
        foreach (var g in keysByTable)
        {
            var table = byId[g.Key];
            var keys = g.Select(i => i.KeyJson!).Distinct(StringComparer.Ordinal).ToList();
            var sql = RereadSql.For(table);
            if (sql is null) continue;
            foreach (var chunk in keys.Chunk(1000))
            {
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add(new SqlParameter("@keys", System.Data.SqlDbType.NVarChar, -1) { Value = "[" + string.Join(",", chunk) + "]" });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                var found = new Dictionary<int, string?>();
                while (await r.ReadAsync(ct)) found[Convert.ToInt32(r.GetString(0))] = r.IsDBNull(1) ? null : r.GetString(1);
                for (var i = 0; i < chunk.Length; i++)
                    result.Add(new RereadRow { Table = table.DisplayName, KeyJson = chunk[i], RowJson = found.GetValueOrDefault(i) });
            }
        }
        return result;
    }

    static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        return (T)Convert.ChangeType(v!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    sealed class Timings
    {
        public long Execute, ReadLog, Reread, NetDiff;
        public ExecutionTimings ToContract() => new() { Execute = Execute, ReadLog = ReadLog, Reread = Reread, NetDiff = NetDiff };
    }
}
