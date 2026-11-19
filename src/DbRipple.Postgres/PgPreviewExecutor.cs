using System.Diagnostics;
using System.Globalization;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using Npgsql;

namespace DbRipple.Postgres;

/// <summary>
/// PostgreSQL 미리보기 실행기(설계 결정 3·4). 전용 연결(풀링 끔)에서 BEGIN → 문장마다 SET LOCAL ROLE(미리보기 역할) → 실행 → RESET ROLE → 경계 읽기,
/// 마지막에 감사 헤더·행 이미지를 읽고 최종 차이를 이미지로 계산한 뒤 ROLLBACK한다.
/// 이미지 상한(2026-11-19): 시작 때 dbr.image_cap GUC(남은 FULL 예산)를 두면 감사 함수가 발동마다 예산을 줄이고, 모자라면 KEYS로 바꾼다(한 문장 안에서도).
/// 실행기는 문장 뒤 그 범위에 KEYS 헤더가 있으면 truncated로 적는다(SwitchesToKeys).
/// PostgreSQL 트리거 함수는 COMMIT할 수 없고 오류가 나면 트랜잭션 전체가 중단되므로, 실패하면 ROLLBACK만 하고 남은 변경 검사는 하지 않는다(시퀀스 값 소비는 남는다).
/// 평가 훅(SQL Server 전용 세션)은 부르지 않는다.
/// </summary>
public sealed class PgPreviewExecutor(PgOptions options) : IPreviewExecutor
{
    public async Task<RawCapture> ExecuteAsync(AnalysisResult analysis, BaselineContext baseline, PreviewOptions previewOptions, IEvaluationHook? hook, CancellationToken ct)
    {
        if (!analysis.IsAccepted) throw new ArgumentException("거부된 분석 결과는 실행할 수 없습니다.", nameof(analysis));
        var cap = previewOptions.ImageCap ?? PreviewOptions.DefaultImageCap;
        var tables = baseline.Catalog.ToCaptureInput().ToDictionary(t => t.ObjectId);

        await using var conn = new NpgsqlConnection(SandboxConnectionString(options, options.SandboxDb));
        try { await conn.OpenAsync(ct); }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new SandboxUnavailableException("샌드박스에 연결하지 못했습니다.", ex);
        }

        var executions = new List<StatementExecution>();
        var timings = new ExecutionTimings();
        long imageCount = 0;
        var mode = ImageMode.Full;
        var truncated = false;
        StatementStatus? failure = null;
        await using var tx = await conn.BeginTransactionAsync(ct);
        var sw = Stopwatch.StartNew();
        await using (var init = new NpgsqlCommand(BeginSql, conn, tx)
                     { Parameters = { new() { Value = cap.ToString(CultureInfo.InvariantCulture) } } })
            await init.ExecuteNonQueryAsync(ct);
        var boundary0 = await ScalarAsync(conn, tx, "SELECT coalesce(max(capture_seq), 0) FROM dbr_audit.header", ct);
        var prev = boundary0;

        foreach (var st in analysis.Statements.OrderBy(s => s.Index))
        {
            if (failure is not null)
            {
                executions.Add(new StatementExecution { Result = Result(st, StatementStatus.NotRun), ModeAfter = mode, ImageCountAfter = imageCount });
                continue;
            }
            long? rows = null;
            NpgsqlException? error = null;
            try
            {
                if (options.PreviewRole is { } role) await ExecAsync(conn, tx, "SET LOCAL ROLE " + PgNames.Quote(role), ct);
                await using var cmd = new NpgsqlCommand(st.Sql, conn, tx) { CommandTimeout = options.CommandTimeoutSeconds };
                rows = await cmd.ExecuteNonQueryAsync(ct);
                if (options.PreviewRole is not null) await ExecAsync(conn, tx, "RESET ROLE", ct);
            }
            catch (NpgsqlException ex) when (!ct.IsCancellationRequested)
            {
                error = ex;
            }
            if (conn.State != System.Data.ConnectionState.Open) throw new SandboxUnavailableException("문장 실행 중 연결이 끊김(기준선 재구축 필요)", error);

            if (error is not null)
            {
                var (status, err) = error is PostgresException pg
                    ? Classify(pg)
                    : (StatementStatus.Failed, new StatementError
                    {
                        ErrorNumber = 0,
                        Message = error.InnerException is TimeoutException ? $"명령 시간 제한({options.CommandTimeoutSeconds}초)을 넘김" : error.Message,
                        IsToolFault = false,
                    });
                failure = status;
                executions.Add(new StatementExecution { Result = Result(st, status) with { Error = err }, ModeAfter = mode, ImageCountAfter = imageCount });
                continue;
            }

            var (min, max, images, keysBatches) = await BoundaryAsync(conn, tx, prev, ct);
            imageCount += images;
            var boundary = max ?? prev;
            if (SwitchesToKeys(mode, imageCount, cap, keysBatches))
            {
                // 새 감사 함수는 이미 KEYS로 바꿨다(해가 없음). 옛 함수는 여기서 바꿔 다음 문장부터 KEYS.
                await ExecAsync(conn, tx, "SELECT set_config('dbr.image_mode', 'KEYS', true)", ct);
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
        timings = timings with { Execute = sw.ElapsedMilliseconds };

        if (failure is not null)
        {
            // 중단된 트랜잭션은 ROLLBACK만 받는다. 트리거가 확정할 방법이 없으므로 남은 변경은 없다(외부 부작용은 분석기가 막는다).
            await tx.RollbackAsync(CancellationToken.None);
            return new RawCapture
            {
                Status = failure == StatementStatus.TriggerBlocked ? PreviewStatus.TriggerBlocked : PreviewStatus.Failed,
                Executions = executions.Select(e => e with { Result = e.Result with { CaptureSeqRange = null } }).ToList(),
                Boundary0 = boundary0,
                SandboxDirty = false,
                Capture = new CaptureInfo { ImageCount = 0, Cap = cap, Truncated = false, ManifestVersion = baseline.Manifest.Version },
                Timings = timings,
            };
        }

        sw.Restart();
        // SP22: FK 연쇄 헤더는 연쇄를 시작한 문장과 같은 깊이로 기록된다.
        var headers = await ReadHeadersAsync(conn, tx, boundary0, ct);
        var imageRows = await ReadImagesAsync(conn, tx, boundary0, ct);
        timings = timings with { ReadLog = sw.ElapsedMilliseconds };

        sw.Restart();
        var diff = ImageNetDiff.Compute(headers, imageRows, tables);
        var net = diff.Changes.ToList();
        if (diff.Unresolved.Count > 0)
            net.AddRange(await RereadNetAsync(conn, tx, diff.Unresolved.Select(id => tables[id]).ToList(), imageRows, headers, ct));
        net = [.. net.OrderBy(n => n.Table, StringComparer.Ordinal)];
        timings = timings with { NetDiff = sw.ElapsedMilliseconds };

        await tx.RollbackAsync(CancellationToken.None);
        return new RawCapture
        {
            Status = truncated ? PreviewStatus.Truncated : PreviewStatus.Ok,
            Executions = executions,
            Boundary0 = boundary0,
            Headers = headers,
            Images = imageRows,
            NetChanges = net,
            SandboxDirty = false,
            Capture = new CaptureInfo { ImageCount = imageCount, Cap = cap, Truncated = truncated, ManifestVersion = baseline.Manifest.Version },
            Timings = timings,
        };
    }

    /// <summary>
    /// 트랜잭션 시작 설정(도구가 실행, set_config(…, true) = SET LOCAL).
    /// standard_conforming_strings = on: 문장 나누기(PgSqlLexer)는 일반 '…' 안의 백슬래시를 이스케이프로 보지 않으므로 서버도 같게 강제한다(2026-11-19).
    /// dbr.image_mode·dbr.image_cap: 감사 함수가 발동마다 보는 모드와 "남은 FULL 예산"(감사 함수가 줄여 간다).
    /// </summary>
    internal const string BeginSql =
        "SELECT set_config('standard_conforming_strings', 'on', true), set_config('dbr.image_mode', 'FULL', true), set_config('dbr.image_cap', $1, true)";

    internal static string SandboxConnectionString(PgOptions o, string database) =>
        new NpgsqlConnectionStringBuilder(o.ServerConnectionString) { Database = database, Pooling = false }.ConnectionString;

    static StatementResult Result(AnalyzedStatement st, StatementStatus status) => new()
    {
        Index = st.Index, Batch = st.Batch, Position = st.Position, Kind = st.Kind, Target = st.Target, Span = st.Span, Status = status,
    };

    /// <summary>
    /// 오류 분류(순수). 오류 문맥(Where)에 PL/pgSQL 함수가 있으면 트리거 안에서 난 오류다. 감사 함수(dbr_audit.*)면 수집 장치 오류(failed, 도구 오류),
    /// 사용자 트리거 함수면 trigger_blocked. ErrorNumber는 SQL Server 번호 자리라 0으로 두고 SQLSTATE는 메시지 앞에 붙인다.
    /// </summary>
    internal static (StatementStatus Status, StatementError Error) Classify(PostgresException ex)
    {
        var function = TriggerFunction(ex.Where);
        var tool = function is not null && function.StartsWith(PgNames.AuditSchema + ".", StringComparison.Ordinal);
        var status = function is not null && !tool ? StatementStatus.TriggerBlocked : StatementStatus.Failed;
        var message = $"[{ex.SqlState}] {ex.MessageText}";
        if (ex.Detail is { Length: > 0 } detail) message += " — " + detail;
        return (status, new StatementError { ErrorNumber = 0, Message = message.Length <= 1000 ? message : message[..1000], Procedure = function, IsToolFault = tool });
    }

    /// <summary>오류 문맥의 첫 "PL/pgSQL function 이름(…)" → 이름(스키마가 붙어 있으면 그대로). 없으면 null.</summary>
    internal static string? TriggerFunction(string? where)
    {
        if (string.IsNullOrEmpty(where)) return null;
        const string marker = "PL/pgSQL function ";
        var at = where.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + marker.Length;
        var end = where.IndexOf('(', start);
        return end > start ? where[start..end].Trim() : null;
    }

    static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// 문장 뒤 모드 판정(순수, 2026-11-19). 그 문장 범위에 KEYS 묶음이 있으면(감사 함수가 발동 단위 상한으로 바꿈) KEYS·truncated.
    /// 옛 감사 함수(새로 고침 전 샌드박스)는 발동 단위 판정이 없어 지금처럼 누적 FULL 이미지 수가 상한을 넘을 때 바꾼다.
    /// </summary>
    internal static bool SwitchesToKeys(ImageMode mode, long imageCount, long cap, long keysBatches) =>
        mode == ImageMode.Full && (keysBatches > 0 || imageCount > cap);

    /// <summary>경계 뒤 이 문장의 (최소 순번, 최대 순번, 행 JSON 있는 이미지 수, KEYS 헤더 수).</summary>
    static async Task<(long? Min, long? Max, long Images, long KeysBatches)> BoundaryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long prev, CancellationToken ct)
    {
        const string sql = """
            SELECT (SELECT min(capture_seq) FROM dbr_audit.header WHERE capture_seq > $1),
                   (SELECT max(capture_seq) FROM dbr_audit.header WHERE capture_seq > $1),
                   (SELECT count(*) FROM dbr_audit.row_image WHERE capture_seq > $1 AND row_json IS NOT NULL),
                   (SELECT count(*) FROM dbr_audit.header WHERE capture_seq > $1 AND image_mode = 'KEYS')
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = prev } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    static async Task<IReadOnlyList<AuditHeaderRow>> ReadHeadersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long boundary0, CancellationToken ct)
    {
        const string sql = """
            SELECT capture_seq, table_oid, table_schema, table_name, op, inserted_count, deleted_count, nest_level, image_mode
            FROM dbr_audit.header WHERE capture_seq > $1 ORDER BY capture_seq
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = boundary0 } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditHeaderRow>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditHeaderRow
            {
                CaptureSeq = r.GetInt64(0),
                TableObjectId = PgNames.ObjectId(r.GetFieldValue<uint>(1), "감사 테이블"),
                TableName = PgNames.Bracket(r.GetString(2), r.GetString(3)),
                Operation = r.GetString(4) switch { "I" => ChangeOperation.Insert, "U" => ChangeOperation.Update, _ => ChangeOperation.Delete },
                InsertedCount = r.GetInt64(5),
                DeletedCount = r.GetInt64(6),
                NestLevel = r.GetInt32(7),
                // SQL Server 계약의 "BEGIN TRAN 안 문장이면 2"를 그대로 둔다(PostgreSQL에는 중첩 트랜잭션 수가 없다).
                TranCount = 2,
                ImageMode = r.GetString(8) == "KEYS" ? ImageMode.Keys : ImageMode.Full,
            });
        return list;
    }

    static async Task<IReadOnlyList<AuditRowImageRow>> ReadImagesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long boundary0, CancellationToken ct)
    {
        const string sql = """
            SELECT capture_seq, image_kind, ordinal, key_json, row_json
            FROM dbr_audit.row_image WHERE capture_seq > $1 ORDER BY capture_seq, image_kind, ordinal
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = boundary0 } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRowImageRow>();
        while (await r.ReadAsync(ct))
            list.Add(new AuditRowImageRow
            {
                CaptureSeq = r.GetInt64(0),
                ImageKind = r.GetString(1) == "INSERTED" ? ImageKind.Inserted : ImageKind.Deleted,
                Ordinal = r.GetInt32(2),
                KeyJson = r.IsDBNull(3) ? null : r.GetString(3),
                RowJson = r.IsDBNull(4) ? null : r.GetString(4),
            });
        return list;
    }

    /// <summary>
    /// 이미지 상한을 넘어 KEYS로 바뀐 테이블의 최종 차이: before는 트랜잭션 밖 새 연결(확정된 기준선 상태), after는 이 트랜잭션에서 읽는다.
    /// 키 있는 테이블은 감사에 나온 키만, 키 없는 테이블은 전체 행 다중집합을 비교한다(상한을 넘은 경우에만 가는 느린 길).
    /// </summary>
    async Task<IReadOnlyList<NetChange>> RereadNetAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TableInfo> tables, IReadOnlyList<AuditRowImageRow> images,
        IReadOnlyList<AuditHeaderRow> headers, CancellationToken ct)
    {
        await using var before = new NpgsqlConnection(SandboxConnectionString(options, options.SandboxDb));
        await before.OpenAsync(ct);
        var result = new List<NetChange>();
        foreach (var t in tables)
        {
            var key = TableKey.Choose(t);
            var q = PgNames.Quote(t.Schema, t.Name);
            if (key.Kind == KeyKind.ROW)
            {
                var sql = $"SELECT dbr_audit.row_of(to_jsonb(x)), count(*) FROM {q} x GROUP BY 1";
                var b = await CountsAsync(before, null, sql, ct);
                var a = await CountsAsync(conn, tx, sql, ct);
                foreach (var json in b.Keys.Union(a.Keys).Order(StringComparer.Ordinal))
                {
                    var n = a.GetValueOrDefault(json) - b.GetValueOrDefault(json);
                    if (n == 0) continue;
                    result.Add(n < 0
                        ? new NetChange { Table = t.DisplayName, Key = new RowKey { Kind = KeyKind.ROW }, Change = NetChangeKind.MultisetRemoved, Multiplicity = -n, Before = AuditJson.ParseObject(json) }
                        : new NetChange { Table = t.DisplayName, Key = new RowKey { Kind = KeyKind.ROW }, Change = NetChangeKind.MultisetAdded, Multiplicity = n, After = AuditJson.ParseObject(json) });
                }
                continue;
            }
            var keys = RereadKeys(t.ObjectId, headers, images);
            if (keys.Count == 0) continue;
            var cols = string.Join(", ", key.Columns.Select(c => "x." + PgNames.Quote(c)));
            var rcols = string.Join(", ", key.Columns.Select(c => "r." + PgNames.Quote(c)));
            var keySql = $"""
                SELECT dbr_audit.key_of(to_jsonb(x), $2), dbr_audit.row_of(to_jsonb(x)) FROM {q} x
                WHERE ({cols}) IN (SELECT {rcols} FROM jsonb_populate_recordset(NULL::{q}, $1::jsonb) r)
                """;
            var keyArray = "[" + string.Join(",", keys) + "]";
            var colArray = key.Columns.ToArray();
            var rowsBefore = await RowsByKeyAsync(before, null, keySql, keyArray, colArray, ct);
            var rowsAfter = await RowsByKeyAsync(conn, tx, keySql, keyArray, colArray, ct);
            foreach (var k in keys.Order(StringComparer.Ordinal))
            {
                var b = rowsBefore.GetValueOrDefault(k);
                var a = rowsAfter.GetValueOrDefault(k);
                if (b == a) continue;
                var rowKey = ImageNetDiff.KeyOf(key, k);
                if (b is null)
                    result.Add(new NetChange { Table = t.DisplayName, Key = rowKey, Change = NetChangeKind.Inserted, Multiplicity = 1, After = AuditJson.ParseObject(a!) });
                else if (a is null)
                    result.Add(new NetChange { Table = t.DisplayName, Key = rowKey, Change = NetChangeKind.Deleted, Multiplicity = 1, Before = AuditJson.ParseObject(b) });
                else
                {
                    var bv = AuditJson.ParseObject(b);
                    var av = AuditJson.ParseObject(a);
                    result.Add(new NetChange { Table = t.DisplayName, Key = rowKey, Change = NetChangeKind.Updated, Multiplicity = 1, Before = bv, After = av, ChangedColumns = ImageNetDiff.Changed(t, bv, av) });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 재조회할 키(순수): 그 테이블의 모든 묶음(FULL·KEYS) 이미지에 나온 키. 감사 함수는 FULL에서도 key_json을 남기므로
    /// 한 문장 안에서 FULL·KEYS 묶음이 섞인 테이블도 빠지는 키가 없다(2026-11-19).
    /// </summary>
    internal static List<string> RereadKeys(int tableId, IReadOnlyList<AuditHeaderRow> headers, IReadOnlyList<AuditRowImageRow> images)
    {
        var seqs = headers.Where(h => h.TableObjectId == tableId).Select(h => h.CaptureSeq).ToHashSet();
        return images.Where(i => seqs.Contains(i.CaptureSeq) && i.KeyJson is not null).Select(i => i.KeyJson!).Distinct(StringComparer.Ordinal).ToList();
    }

    static async Task<Dictionary<string, long>> CountsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    static async Task<Dictionary<string, string>> RowsByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, string keys, string[] cols, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0, Parameters = { new() { Value = keys }, new() { Value = cols } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
        return d;
    }
}
