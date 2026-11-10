using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Execution.Tests.Support;

/// <summary>
/// 자기 전용 기준선(dbrt_exe_). CascadeLab은 TestBaseline을 쓰고, 오류 규칙 실측용 작은 스키마는 Core BaselineBuilder로 직접 만든다(TestBaseline과 같은 순서).
/// </summary>
public sealed class ExeBaseline : IAsyncDisposable
{
    readonly IAsyncDisposable cleanup;

    ExeBaseline(string origin, string sandbox, string snapshot, BaselineResult result, IAsyncDisposable cleanup)
    {
        Origin = origin; Sandbox = sandbox; Snapshot = snapshot; Result = result; this.cleanup = cleanup;
    }

    public string Origin { get; }
    public string Sandbox { get; }
    public string Snapshot { get; }
    public BaselineResult Result { get; }

    public static async Task<ExeBaseline> CascadeLabAsync(CascadeLabVariant variant = CascadeLabVariant.Default)
    {
        var tb = await TestBaseline.BuildCascadeLabAsync("exe", variant);
        return new ExeBaseline(tb.Origin, tb.Sandbox, tb.Snapshot, tb.Result, tb);
    }

    /// <summary>batches로 원본 역할 DB를 만들고 기준선을 쌓는다.</summary>
    public static async Task<ExeBaseline> CustomAsync(string tag, params string[] batches)
    {
        var origin = TestDb.NewName("exe", tag);
        string sandbox = origin + "_sbx", snapshot = origin + "_base", backup = TestDb.BackupPath(origin);
        var cleanup = new TestDatabases(snapshot, sandbox, origin);
        cleanup.ContainerFiles.Add(backup);
        try
        {
            await TestDb.ExecAsync("master", $"CREATE DATABASE [{origin}];");
            await TestDb.ExecAsync(origin, batches);
            await using var steps = new SqlBaselineSteps(TestDb.ConnectionString());
            var result = await BaselineBuilder.BuildAsync(new BaselineOptions(origin, sandbox, snapshot, backup), steps, () => DateTimeOffset.Now);
            return new ExeBaseline(origin, sandbox, snapshot, result, cleanup);
        }
        catch
        {
            await cleanup.DisposeAsync();
            throw;
        }
    }

    /// <summary>미리보기 문맥(트리거 정적 분석은 C6 몫이라 비워 둔다).</summary>
    public async Task<BaselineContext> ContextAsync()
    {
        await using var conn = await TestDb.OpenAsync(Sandbox);
        var catalog = await CatalogReader.ReadAsync(conn);
        var fp = await Fingerprint.ReadAsync(conn, Sandbox);
        return new BaselineContext
        {
            OriginDb = Origin,
            SandboxDb = Sandbox,
            SnapshotDb = Snapshot,
            BackupFinishedAt = Result.BackupCompletedAt,
            Fingerprint = fp,
            FingerprintMatch = Result.FingerprintMatch,
            Catalog = catalog,
            Triggers = [],
            Manifest = Result.Install.Manifest,
        };
    }

    public ValueTask DisposeAsync() => cleanup.DisposeAsync();
}

/// <summary>
/// 시험용 최종 차이(C12가 준비되기 전 대역, AD1 때문에 Diff 프로젝트를 참조하지 않는다). 테이블마다 관측 가능 열의 EXCEPT 양방향으로 키별 INSERTED/DELETED/UPDATED를 낸다.
/// 값은 채우지 않는다(건수·키만 본다). 호출 기록을 남긴다.
/// </summary>
public sealed class ExceptNetDiffer(INetDiffer? inner = null) : INetDiffer
{
    /// <summary>
    /// 픽스처를 쓸 때(DBR_WRITE_FIXTURES=1)는 diff-1이 이미 빌드한 진짜 C12(src/DbRipple.Diff/bin/Debug/net10.0/DbRipple.Diff.dll)를 바이트로 읽어(파일 잠금 없이) 감싼다.
    /// 프로젝트 참조가 아니라 실행 시 적재라 AD1(조립은 Host·Eval.Cli에서만)을 컴파일 의존으로 깨지 않는다. 평소에는 EXCEPT 대역만 쓴다.
    /// </summary>
    public static ExceptNetDiffer Create()
    {
        if (!FixtureFiles.Enabled) return new ExceptNetDiffer();
        var dll = Path.Combine(DbRipple.TestSupport.RepoPaths.Root, "src", "DbRipple.Diff", "bin", "Debug", "net10.0", "DbRipple.Diff.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("픽스처 생성에는 빌드된 DbRipple.Diff.dll이 필요함: " + dll);
        var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(dll));
        return new ExceptNetDiffer((INetDiffer)Activator.CreateInstance(asm.GetType("DbRipple.Diff.NetDiffer", throwOnError: true)!)!);
    }

    public bool IsReal => inner is not null;

    public List<(string Live, string Snapshot, IReadOnlyList<string> Tables, int TranCount)> Calls { get; } = [];

    public async Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        int tranCount;
        await using (var c = session.CreateCommand("SELECT @@TRANCOUNT;")) tranCount = (int)(await c.ExecuteScalarAsync(ct))!;
        Calls.Add((liveDb, snapshotDb, tables.Select(t => t.DisplayName).ToList(), tranCount));
        if (inner is not null) return await inner.DiffAsync(session, liveDb, snapshotDb, tables, ct);
        var result = new List<NetChange>();
        foreach (var t in tables.OrderBy(t => t.DisplayName, StringComparer.Ordinal))
        {
            var key = TableKey.Choose(t);
            var cols = t.Columns.Where(c => !c.IsComputed && !AuditValueExpressions.IsUnobservable(c) && c.TypeName is not ("xml" or "geography" or "geometry" or "hierarchyid"))
                .Select(c => "[" + c.Name + "]").ToList();
            var colList = string.Join(", ", cols);
            string live = $"[{liveDb}].{t.QuotedName}", snap = $"[{snapshotDb}].{t.QuotedName}";
            var added = await KeysAsync(session, $"SELECT {colList} FROM {live} EXCEPT SELECT {colList} FROM {snap}", key, ct);
            var removed = await KeysAsync(session, $"SELECT {colList} FROM {snap} EXCEPT SELECT {colList} FROM {live}", key, ct);
            if (key.Kind == KeyKind.ROW)
            {
                result.AddRange(removed.Select(_ => Net(t, key, [], NetChangeKind.MultisetRemoved)));
                result.AddRange(added.Select(_ => Net(t, key, [], NetChangeKind.MultisetAdded)));
                continue;
            }
            var addSet = added.ToHashSet(StringComparer.Ordinal);
            var remSet = removed.ToHashSet(StringComparer.Ordinal);
            foreach (var k in remSet.Union(addSet).Order(StringComparer.Ordinal))
            {
                var kind = addSet.Contains(k) && remSet.Contains(k) ? NetChangeKind.Updated : addSet.Contains(k) ? NetChangeKind.Inserted : NetChangeKind.Deleted;
                result.Add(Net(t, key, k.Split('\u001f'), kind));
            }
        }
        return result;
    }

    static NetChange Net(TableInfo t, TableKey key, IReadOnlyList<string?> values, NetChangeKind kind) => new()
    {
        Table = t.DisplayName,
        Key = new RowKey { Kind = key.Kind, Columns = key.Columns, Values = values },
        Change = kind,
        Multiplicity = 1,
    };

    static async Task<List<string>> KeysAsync(SqlSession s, string sql, TableKey key, CancellationToken ct)
    {
        var keyCols = key.Kind == KeyKind.ROW ? "1 AS k" : string.Join(", ", key.Columns.Select(c => $"CONVERT(nvarchar(4000), x.[{c}])"));
        await using var cmd = s.CreateCommand($"SELECT {keyCols} FROM ({sql}) AS x;");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
            list.Add(string.Join('\u001f', Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return list;
    }
}

/// <summary>분석 결과 대역(C6 없이 문장을 직접 만든다).</summary>
public static class Stmts
{
    public static AnalysisResult Of(params (string Sql, ChangeOperation Kind, string Target)[] statements) => new()
    {
        Statements = statements.Select((s, i) => new AnalyzedStatement
        {
            Index = i + 1,
            Batch = 1,
            Position = i + 1,
            Kind = s.Kind,
            Target = s.Target,
            Span = new SourceSpan { StartLine = i + 1, StartColumn = 1, EndLine = i + 1, EndColumn = s.Sql.Length + 1 },
            Sql = s.Sql,
        }).ToList(),
    };

    /// <summary>"UPDATE x.Y ..."에서 연산과 대상을 뽑는다(테스트 문장 전용).</summary>
    public static (string, ChangeOperation, string) Parse(string sql)
    {
        var words = sql.Replace(";", " ").Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var kind = words[0].ToUpperInvariant() switch { "INSERT" => ChangeOperation.Insert, "UPDATE" => ChangeOperation.Update, _ => ChangeOperation.Delete };
        var skip = words[1].Equals("FROM", StringComparison.OrdinalIgnoreCase) || words[1].Equals("INTO", StringComparison.OrdinalIgnoreCase);
        var target = kind == ChangeOperation.Update || !skip ? words[1] : words[2];
        return (sql, kind, target.Split('(')[0]);
    }

    public static AnalysisResult Sql(params string[] sql) => Of(sql.Select(Parse).ToArray());

    /// <summary>샌드박스 대 스냅샷 차이 행 수(테이블 전체 EXCEPT 양방향, 감사 저장소 제외).</summary>
    public static async Task<long> SandboxDiffRowsAsync(ExeBaseline b)
    {
        var ctx = await b.ContextAsync();
        await using var conn = await TestDb.OpenAsync(b.Sandbox);
        var differ = new ExceptNetDiffer();
        var tables = ctx.Catalog.ToCaptureInput().Where(t => !AuditTriggerGenerator.IsCaptureStore(t)).ToList();
        return (await differ.DiffAsync(new SqlSession { Connection = conn }, b.Sandbox, b.Snapshot, tables, default)).Count;
    }
}
