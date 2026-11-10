// CascadeLab 손 정답 검증 도구 (설계 §10.3: 1차 정답 = 손 정답, 2차 정답 = 수집 장치 없는 DB의 실행 결과).
// 제품 코드(DbRipple.Core)를 참조하지 않는다. 비교 방식도 §10.3대로 행별 SHA2_256 해시 + 셀 비교다.
//
// 사용(PowerShell, 저장소 루트에서, 한 명령 안에서 점 소싱):
//   . .\scripts\env.ps1; dotnet run --project cascadelab\verification\CascadeLab.Verify -- oracle
//   . .\scripts\env.ps1; dotnet run --project cascadelab\verification\CascadeLab.Verify -- audit --sandbox CascadeLab_Sandbox [--rt-sandbox DB] [--repeat 5]
// 옵션: --cases <dir>(기본 cascadelab/cases)  --out <dir>(기본 cascadelab/verification/logs)  --case <ID>(반복 가능)  --tag <출력 파일 꼬리표>
//
// 사례마다: 새 연결 → BEGIN TRAN → 실행 전 상태 → 문장(오류 번호 수집) → [감사 헤더 읽기] → 실행 후 상태 → ROLLBACK → 롤백 후 상태.
// 시작 상태가 시드 상태(실행 전 디지스트)와 같은지, 롤백 뒤 시드로 돌아왔는지도 함께 확인한다.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace CascadeLab.Verify;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Options opt;
        try { opt = Options.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        var cases = CaseDef.LoadAll(opt.CasesDir)
            .Where(c => opt.Only.Count == 0 || opt.Only.Contains(c.Id))
            .ToList();
        if (cases.Count == 0) { Console.Error.WriteLine("실행할 사례가 없습니다."); return 2; }

        var suite = opt.Mode == "oracle" ? Runner.Oracle(cases) : Runner.Audit(opt, cases);

        Directory.CreateDirectory(opt.OutDir);
        var stamp = opt.Tag ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(opt.OutDir, $"{opt.Mode}-{stamp}");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(baseName + ".json", JsonSerializer.Serialize(suite, JsonOut.Options), utf8);
        var text = TextReport.Render(suite);
        File.WriteAllText(baseName + ".txt", text, utf8);
        Console.Write(text);
        Console.WriteLine($"결과: {baseName}.json, {baseName}.txt");
        return suite.Cases.Any(c => c.Verdict is "MISMATCH" or "INVALID" or "ERROR") ? 1 : 0;
    }
}

internal sealed class Options
{
    public const string Usage =
        "사용: CascadeLab.Verify oracle|audit [--cases dir] [--out dir] [--case ID]... [--sandbox DB] [--rt-sandbox DB] [--repeat N] [--tag T]";

    public string Mode { get; private set; } = "";
    public string CasesDir { get; private set; } = Path.Combine("cascadelab", "cases");
    public string OutDir { get; private set; } = Path.Combine("cascadelab", "verification", "logs");
    public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Sandbox { get; private set; }
    public string? RtSandbox { get; private set; }
    public int Repeat { get; private set; } = 1;
    public string? Tag { get; private set; }

    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("oracle" or "audit"))
            throw new ArgumentException("첫 인자로 모드(oracle|audit)가 필요합니다.");
        var o = new Options { Mode = args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} 값이 없습니다.");
            switch (name)
            {
                case "--cases": o.CasesDir = Next(); break;
                case "--out": o.OutDir = Next(); break;
                case "--case": o.Only.Add(Next()); break;
                case "--sandbox": o.Sandbox = Next(); break;
                case "--rt-sandbox": o.RtSandbox = Next(); break;
                case "--repeat": o.Repeat = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--tag": o.Tag = Next(); break;
                default: throw new ArgumentException($"알 수 없는 인자: {name}");
            }
        }
        if (o.Mode == "audit" && o.Sandbox is null && o.RtSandbox is null)
            throw new ArgumentException("audit 모드는 --sandbox 또는 --rt-sandbox가 필요합니다.");
        foreach (var db in new[] { o.Sandbox, o.RtSandbox })
            if (db is not null && !Regex.IsMatch(db, "^[A-Za-z0-9_]+$")) throw new ArgumentException($"DB 이름 형식 오류: {db}");
        return o;
    }
}

// ---------------------------------------------------------------- 사례 정의

internal sealed class CaseDef
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required List<(int Index, string Sql)> Statements { get; init; }
    public required JsonObject Expected { get; init; }

    public static List<CaseDef> LoadAll(string dir) =>
        Directory.GetFiles(dir, "*.json").Select(Load).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    private static CaseDef Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        var script = (string)root["schemaScript"]!;
        return new CaseDef
        {
            Id = (string)root["id"]!,
            FileName = Path.GetFileName(path),
            Database = (string)root["database"]!,
            Schema = Path.GetFileNameWithoutExtension(script),
            Statements = root["statements"]!.AsArray().Select(s => ((int)s!["index"]!, (string)s["sql"]!)).ToList(),
            Expected = root["expected"]!.AsObject(),
        };
    }
}

// ---------------------------------------------------------------- DB 접근

internal static class Db
{
    public static SqlConnection Open(string database)
    {
        var cs = Environment.GetEnvironmentVariable("DBRIPPLE_SQL_CONNECTION")
                 ?? throw new InvalidOperationException("DBRIPPLE_SQL_CONNECTION이 없습니다(같은 명령에서 . .\\scripts\\env.ps1 점 소싱).");
        var b = new SqlConnectionStringBuilder(cs)
        {
            InitialCatalog = database,
            Pooling = false,
            ApplicationName = "cascadelab-verify",
        };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        Exec(c, "SET LOCK_TIMEOUT 15000;");
        return c;
    }

    public static void Exec(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqlConnection c, string sql, params (string Name, object Value)[] ps)
    {
        var rows = Rows(c, sql, ps);
        return rows.Count == 0 ? null : rows[0][0];
    }

    public static List<object?[]> Rows(SqlConnection c, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
}

internal sealed record TableMeta(string Schema, string Name, List<string> Columns, List<string> Key)
{
    public string Display => Schema + "." + Name;
    public string Quoted => Db.Q(Schema) + "." + Db.Q(Name);
}

internal sealed record RowImage(string Key, Dictionary<string, object?> Values, string Hash);

internal static class Snapshot
{
    /// <summary>사용자 테이블 전부(감사 스키마 audit 제외)와 PK 열.</summary>
    public static List<TableMeta> LoadTables(SqlConnection c)
    {
        var rows = Db.Rows(c, """
            SELECT s.name, t.name, col.name, col.column_id,
                   (SELECT ic.key_ordinal
                    FROM sys.indexes AS i
                    JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    WHERE i.object_id = t.object_id AND i.is_primary_key = 1 AND ic.column_id = col.column_id) AS key_ordinal
            FROM sys.tables AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            JOIN sys.columns AS col ON col.object_id = t.object_id
            WHERE t.is_ms_shipped = 0 AND s.name <> N'audit'
            ORDER BY s.name, t.name, col.column_id;
            """);
        var metas = rows.GroupBy(r => ((string)r[0]!, (string)r[1]!))
            .Select(g => new TableMeta(g.Key.Item1, g.Key.Item2,
                g.Select(r => (string)r[2]!).ToList(),
                g.Where(r => r[4] is not null).OrderBy(r => Convert.ToInt32(r[4], CultureInfo.InvariantCulture))
                 .Select(r => (string)r[2]!).ToList()))
            .OrderBy(m => m.Display, StringComparer.Ordinal)
            .ToList();
        foreach (var m in metas.Where(m => m.Key.Count == 0))
            throw new NotSupportedException($"{m.Display}: PK 없는 테이블은 이 도구 범위 밖입니다(CL14).");
        return metas;
    }

    /// <summary>§10.3 방식: 행별 SHA2_256(FOR JSON, NULL 포함) + 전체 열 값.</summary>
    public static Dictionary<string, Dictionary<string, RowImage>> Take(SqlConnection c, List<TableMeta> metas)
    {
        var all = new Dictionary<string, Dictionary<string, RowImage>>(StringComparer.Ordinal);
        foreach (var m in metas)
        {
            var cols = string.Join(", ", m.Columns.Select(x => "x." + Db.Q(x)));
            var sql = $"SELECT {cols}, HASHBYTES('SHA2_256', (SELECT {cols} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) FROM {m.Quoted} AS x;";
            var rows = new Dictionary<string, RowImage>(StringComparer.Ordinal);
            foreach (var r in Db.Rows(c, sql))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < m.Columns.Count; i++) values[m.Columns[i]] = r[i];
                var key = Values.KeyText(m.Key, k => Values.Text(values[k]));
                var hash = Convert.ToHexString((byte[])r[m.Columns.Count]!);
                if (!rows.TryAdd(key, new RowImage(key, values, hash)))
                    throw new InvalidOperationException($"{m.Display}: 키 중복 {key}");
            }
            all[m.Display] = rows;
        }
        return all;
    }

    public static string Digest(Dictionary<string, Dictionary<string, RowImage>> snap)
    {
        var sb = new StringBuilder();
        foreach (var t in snap.Keys.OrderBy(x => x, StringComparer.Ordinal))
            foreach (var k in snap[t].Keys.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(t).Append('|').Append(k).Append('|').Append(snap[t][k].Hash).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>PK 조인 순 변경. 해시 판정과 셀 판정이 어긋나면 ComparatorIssues에 남긴다.</summary>
    public static List<TableDiff> Diff(List<TableMeta> metas,
        Dictionary<string, Dictionary<string, RowImage>> before,
        Dictionary<string, Dictionary<string, RowImage>> after)
    {
        var list = new List<TableDiff>();
        foreach (var m in metas)
        {
            var b = before[m.Display];
            var a = after[m.Display];
            var d = new TableDiff { Table = m.Display, KeyColumns = m.Key };
            foreach (var (k, row) in a.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (!b.ContainsKey(k)) { d.Inserted.Add(k); d.InsertedRows[k] = row.Values; }
            foreach (var k in b.Keys.OrderBy(x => x, StringComparer.Ordinal))
                if (!a.ContainsKey(k)) d.Deleted.Add(k);
            foreach (var (k, rb) in b.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!a.TryGetValue(k, out var ra)) continue;
                var changed = m.Columns.Where(col => !Values.Same(rb.Values[col], ra.Values[col])).ToList();
                var hashChanged = rb.Hash != ra.Hash;
                if (hashChanged != changed.Count > 0)
                    d.ComparatorIssues.Add($"{k}: hashChanged={hashChanged}, cellChanged={changed.Count > 0}");
                if (changed.Count == 0) continue;
                d.Updated.Add(new UpdatedRow
                {
                    Key = k,
                    ChangedColumns = changed,
                    Before = changed.ToDictionary(x => x, x => rb.Values[x]),
                    After = changed.ToDictionary(x => x, x => ra.Values[x]),
                    AfterRow = ra.Values,
                });
            }
            list.Add(d);
        }
        return list;
    }
}

internal static class Values
{
    public static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => a!.Equals(b),
    };

    public static string Text(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => JsonSerializer.Serialize(dt.ToString("o", CultureInfo.InvariantCulture)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(v.ToString()),
    };

    public static string Json(JsonNode? n) => n is null ? "null" : n.ToJsonString();

    public static string KeyText(IEnumerable<string> keyCols, Func<string, string> valueOf) =>
        "{" + string.Join(",", keyCols.Select(k => "\"" + k + "\":" + valueOf(k))) + "}";
}

// ---------------------------------------------------------------- 결과 모델

internal sealed class TableDiff
{
    public string Table { get; init; } = "";
    public List<string> KeyColumns { get; init; } = [];
    public List<string> Inserted { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<UpdatedRow> Updated { get; } = [];
    public List<string> ComparatorIssues { get; } = [];
    [JsonIgnore] public Dictionary<string, Dictionary<string, object?>> InsertedRows { get; } = new(StringComparer.Ordinal);
    [JsonIgnore] public bool HasChanges => Inserted.Count + Deleted.Count + Updated.Count > 0;

    public string Summary() =>
        $"I{Inserted.Count}[{string.Join(" ", Inserted)}] U{Updated.Count}[{string.Join(" ", Updated.Select(u => u.Key + "(" + string.Join(",", u.ChangedColumns) + ")"))}] D{Deleted.Count}[{string.Join(" ", Deleted)}]";
}

internal sealed class UpdatedRow
{
    public string Key { get; init; } = "";
    public List<string> ChangedColumns { get; init; } = [];
    public Dictionary<string, object?> Before { get; init; } = new();
    public Dictionary<string, object?> After { get; init; } = new();
    [JsonIgnore] public Dictionary<string, object?> AfterRow { get; init; } = new();
}

internal sealed record ErrorInfo(int Number, byte Class, byte State, int Line, string Message);

internal sealed class StatementRun
{
    public int Index { get; init; }
    public string Sql { get; init; } = "";
    public string Status { get; set; } = "";
    public int? RowsAffected { get; set; }
    public int? ErrorNumber { get; set; }
    public List<ErrorInfo> Errors { get; set; } = [];
    public List<string> InfoMessages { get; set; } = [];
    public int TranCountAfter { get; set; }
    public int XactStateAfter { get; set; }
}

internal sealed class AuditFiring
{
    public long Seq { get; init; }
    public string Table { get; init; } = "";
    public string Operation { get; init; } = "";
    public long InsertedCount { get; init; }
    public long DeletedCount { get; init; }
    public long RowCount { get; init; }
    public int NestLevel { get; init; }
    public int TranCount { get; init; }
    public string ImageMode { get; init; } = "";
    public List<string> Images { get; init; } = [];
    public string Label => $"{Seq}:{Table}:{Operation[0]}:d{NestLevel}:r{RowCount}";
}

internal sealed class Check
{
    public string Item { get; init; } = "";
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public string Verdict { get; init; } = "";   // MATCH, MISMATCH, OBSERVED
    public string? Note { get; init; }

    public static Check Eq(string item, string exp, string act, string? note = null) =>
        new() { Item = item, Expected = exp, Actual = act, Verdict = exp == act ? "MATCH" : "MISMATCH", Note = note };

    public static Check Set(string item, IEnumerable<string> exp, IEnumerable<string> act, string? note = null) =>
        Eq(item, "[" + string.Join(", ", exp.OrderBy(x => x, StringComparer.Ordinal)) + "]",
                 "[" + string.Join(", ", act.OrderBy(x => x, StringComparer.Ordinal)) + "]", note);

    public static Check Observed(string item, string exp, string act, string? note = null) =>
        new() { Item = item, Expected = exp, Actual = act, Verdict = "OBSERVED", Note = note };
}

internal sealed class RunResult
{
    public List<StatementRun> Statements { get; } = [];
    public List<TableDiff> Diffs { get; set; } = [];
    public string BeforeDigest { get; set; } = "";
    public string AfterRollbackDigest { get; set; } = "";
    public List<AuditFiring>? Audit { get; set; }
    public long AuditBoundary { get; set; }
    public long AuditMaxAfterRollback { get; set; }
    public string Status => Statements.FirstOrDefault(s => s.Status is "failed" or "trigger_blocked")?.Status ?? "ok";
    public int? ErrorNumber => Statements.FirstOrDefault(s => s.ErrorNumber is not null)?.ErrorNumber;
}

internal sealed class CaseResult
{
    public string Id { get; init; } = "";
    public string CaseFile { get; init; } = "";
    public string Database { get; init; } = "";
    public string Verdict { get; set; } = "";   // MATCH, MISMATCH, INVALID(환경), SKIPPED, ERROR
    public string? Reason { get; set; }
    public string Status { get; set; } = "";
    public int? ErrorNumber { get; set; }
    public List<StatementRun> Statements { get; set; } = [];
    public List<TableDiff> NetChanges { get; set; } = [];
    public List<Check> Checks { get; set; } = [];
    public List<AuditFiring>? Firings { get; set; }
    public string? CaptureOrder { get; set; }
    public string? MatchedAlternative { get; set; }
    public List<string>? RepeatRuns { get; set; }
    public List<string>? AuditTriggers { get; set; }
}

internal sealed class SuiteResult
{
    public string Mode { get; init; } = "";
    public string StartedAt { get; init; } = "";
    public string Tool { get; init; } = "cascadelab/verification/CascadeLab.Verify";
    public Dictionary<string, object?> Env { get; set; } = new();
    public Dictionary<string, string> SeedDigest { get; } = new();
    public List<Check> EnvChecks { get; } = [];
    public List<CaseResult> Cases { get; } = [];
}

internal static class JsonOut
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---------------------------------------------------------------- 실행

internal static class Runner
{
    public static SuiteResult Oracle(List<CaseDef> cases)
    {
        var suite = new SuiteResult { Mode = "oracle", StartedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) };
        suite.Env = Env.Read(cases.Select(c => c.Database).Distinct().ToList());
        foreach (var g in cases.GroupBy(c => c.Database))
        {
            List<TableMeta> metas;
            string seed;
            using (var c = Db.Open(g.Key))
            {
                metas = Snapshot.LoadTables(c);
                seed = Snapshot.Digest(Snapshot.Take(c, metas));
            }
            suite.SeedDigest[g.Key] = seed;
            foreach (var cd in g)
            {
                var cr = new CaseResult { Id = cd.Id, CaseFile = cd.FileName, Database = g.Key };
                try
                {
                    var run = RunOnce(g.Key, metas, cd, readAudit: false);
                    Fill(cr, cd, metas, run, seed, "");
                }
                catch (Exception ex) { cr.Verdict = "ERROR"; cr.Reason = ex.GetType().Name + ": " + ex.Message; }
                suite.Cases.Add(cr);
            }
        }
        suite.Cases.Sort((x, y) => StringComparer.Ordinal.Compare(x.Id, y.Id));
        return suite;
    }

    public static SuiteResult Audit(Options opt, List<CaseDef> cases)
    {
        var suite = new SuiteResult { Mode = "audit", StartedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) };
        string? SandboxOf(string origin) => origin switch
        {
            "CascadeLab" => opt.Sandbox,
            "CascadeLab_RT" => opt.RtSandbox,
            _ => null,
        };
        var dbs = cases.Select(c => c.Database).Distinct().ToList();
        dbs.AddRange(dbs.Select(SandboxOf).Where(x => x is not null).Select(x => x!).ToList());
        suite.Env = Env.Read(dbs.Where(Env.Exists).Distinct().ToList());

        foreach (var g in cases.GroupBy(c => c.Database))
        {
            var sandbox = SandboxOf(g.Key);
            string? skip = sandbox is null ? $"{g.Key}의 샌드박스 인자 없음" : !Env.Exists(sandbox) ? $"{sandbox} 없음" : null;
            if (skip is null)
            {
                using var c = Db.Open(sandbox!);
                if (Db.Scalar(c, "SELECT OBJECT_ID(N'audit.AuditHeader');") is null) skip = $"{sandbox}에 audit.AuditHeader 없음";
            }
            if (skip is not null)
            {
                foreach (var cd in g) suite.Cases.Add(new CaseResult { Id = cd.Id, CaseFile = cd.FileName, Database = sandbox ?? "", Verdict = "SKIPPED", Reason = skip });
                continue;
            }

            List<TableMeta> metas;
            string seed;
            using (var c = Db.Open(sandbox!))
            {
                metas = Snapshot.LoadTables(c);
                seed = Snapshot.Digest(Snapshot.Take(c, metas));
            }
            suite.SeedDigest[sandbox!] = seed;
            using (var o = Db.Open(g.Key))
            {
                var originDigest = Snapshot.Digest(Snapshot.Take(o, Snapshot.LoadTables(o)));
                suite.SeedDigest[g.Key] = originDigest;
                suite.EnvChecks.Add(Check.Eq($"env: {sandbox} 데이터 = {g.Key} 데이터(감사 스키마 제외 디지스트)", originDigest, seed));
            }
            suite.EnvChecks.Add(Check.Eq($"env: RECURSIVE_TRIGGERS {g.Key} = {sandbox}",
                Values.Text(Env.DbOption(g.Key, "is_recursive_triggers_on")), Values.Text(Env.DbOption(sandbox!, "is_recursive_triggers_on"))));

            foreach (var cd in g)
            {
                var cr = new CaseResult { Id = cd.Id, CaseFile = cd.FileName, Database = sandbox! };
                try
                {
                    var runs = new List<RunResult>();
                    for (var r = 0; r < opt.Repeat; r++) runs.Add(RunOnce(sandbox!, metas, cd, readAudit: true));
                    var first = runs[0];
                    Fill(cr, cd, metas, first, seed, "sandbox ");
                    cr.Firings = first.Audit;
                    cr.CaptureOrder = string.Join(" < ", first.Audit!.Select(f => f.Label));
                    cr.Checks.Add(Check.Eq("env: 롤백 뒤 감사 헤더가 경계로 돌아감", first.AuditBoundary.ToString(CultureInfo.InvariantCulture),
                        first.AuditMaxAfterRollback.ToString(CultureInfo.InvariantCulture)));
                    var (firingChecks, alt) = Firings.Judge(cd, first.Audit!);
                    cr.Checks.AddRange(firingChecks);
                    cr.MatchedAlternative = alt;
                    cr.AuditTriggers = AuditTriggers(sandbox!, cd.Schema);
                    cr.RepeatRuns = runs.Select((x, i) =>
                        $"run{i + 1}: status={x.Status} err={x.ErrorNumber?.ToString(CultureInfo.InvariantCulture) ?? "null"} order=" +
                        string.Join(" < ", x.Audit!.Select(f => $"{f.Table}:{f.Operation[0]}:d{f.NestLevel}:r{f.RowCount}")) +
                        $" net={string.Join("; ", x.Diffs.Where(d => d.HasChanges).Select(d => d.Table + " " + d.Summary()))}").ToList();
                    var distinct = cr.RepeatRuns.Select(s => s[(s.IndexOf(':') + 1)..]).Distinct().Count();
                    cr.Checks.Add(Check.Observed($"반복 {runs.Count}회: 순서·순 변경 문자열 종류 수", "1", distinct.ToString(CultureInfo.InvariantCulture),
                        "M4 맛보기. captureSeq 값 자체는 빼고 비교"));
                    Finish(cr);
                }
                catch (Exception ex) { cr.Verdict = "ERROR"; cr.Reason = ex.GetType().Name + ": " + ex.Message; }
                suite.Cases.Add(cr);
            }
        }
        suite.Cases.Sort((x, y) => StringComparer.Ordinal.Compare(x.Id, y.Id));
        return suite;
    }

    private static RunResult RunOnce(string database, List<TableMeta> metas, CaseDef cd, bool readAudit)
    {
        var run = new RunResult();
        using var c = Db.Open(database);
        Db.Exec(c, "BEGIN TRANSACTION;");
        if (readAudit) run.AuditBoundary = Convert.ToInt64(Db.Scalar(c, "SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;"), CultureInfo.InvariantCulture);
        var before = Snapshot.Take(c, metas);
        var stop = false;
        foreach (var (index, sql) in cd.Statements)
        {
            var sr = new StatementRun { Index = index, Sql = sql };
            run.Statements.Add(sr);
            if (stop) { sr.Status = "not_run"; continue; }
            var infos = new List<string>();
            void OnInfo(object? _, SqlInfoMessageEventArgs e)
            {
                foreach (SqlError er in e.Errors) infos.Add($"{er.Number}/{er.Class}: {er.Message}");
            }
            c.InfoMessage += OnInfo;
            try
            {
                using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
                sr.RowsAffected = cmd.ExecuteNonQuery();
                sr.Status = "ok";
            }
            catch (SqlException ex)
            {
                sr.Errors = ex.Errors.Cast<SqlError>().Select(e => new ErrorInfo(e.Number, e.Class, e.State, e.LineNumber, e.Message)).ToList();
                sr.ErrorNumber = ex.Number;
                sr.Status = sr.Errors.Any(e => e.Number == 3609) ? "trigger_blocked" : "failed";
                stop = true;
            }
            finally { c.InfoMessage -= OnInfo; }
            sr.InfoMessages = infos;
            var st = Db.Rows(c, "SELECT @@TRANCOUNT, XACT_STATE();")[0];
            sr.TranCountAfter = Convert.ToInt32(st[0], CultureInfo.InvariantCulture);
            sr.XactStateAfter = Convert.ToInt32(st[1], CultureInfo.InvariantCulture);
        }
        if (readAudit) run.Audit = ReadAudit(c, run.AuditBoundary);
        var after = Snapshot.Take(c, metas);
        Db.Exec(c, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        var restored = Snapshot.Take(c, metas);
        run.Diffs = Snapshot.Diff(metas, before, after);
        run.BeforeDigest = Snapshot.Digest(before);
        run.AfterRollbackDigest = Snapshot.Digest(restored);
        if (readAudit) run.AuditMaxAfterRollback = Convert.ToInt64(Db.Scalar(c, "SELECT ISNULL(MAX(captureSeq), 0) FROM audit.AuditHeader;"), CultureInfo.InvariantCulture);
        return run;
    }

    private static List<AuditFiring> ReadAudit(SqlConnection c, long boundary)
    {
        var images = Db.Rows(c, """
            SELECT captureSeq, imageKind, ordinal, keyJson, rowJson
            FROM audit.AuditRowImage WHERE captureSeq > @b ORDER BY captureSeq, imageKind, ordinal;
            """, ("@b", boundary))
            .GroupBy(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Select(r => $"{r[1]} key={r[3] ?? "null"} row={r[4] ?? "null"}").ToList());
        return Db.Rows(c, """
            SELECT captureSeq, tableName, operation, insertedCount, deletedCount, nestLevel, tranCount, imageMode
            FROM audit.AuditHeader WHERE captureSeq > @b ORDER BY captureSeq;
            """, ("@b", boundary))
            .Select(r =>
            {
                var seq = Convert.ToInt64(r[0], CultureInfo.InvariantCulture);
                var op = ((string)r[2]!).Trim() switch { "I" => "INSERT", "U" => "UPDATE", "D" => "DELETE", var x => x };
                var ins = Convert.ToInt64(r[3], CultureInfo.InvariantCulture);
                var del = Convert.ToInt64(r[4], CultureInfo.InvariantCulture);
                return new AuditFiring
                {
                    Seq = seq,
                    Table = ((string)r[1]!).Replace("[", "").Replace("]", ""),
                    Operation = op,
                    InsertedCount = ins,
                    DeletedCount = del,
                    RowCount = op == "DELETE" ? del : ins,
                    NestLevel = Convert.ToInt32(r[5], CultureInfo.InvariantCulture),
                    TranCount = Convert.ToInt32(r[6], CultureInfo.InvariantCulture),
                    ImageMode = (string)r[7]!,
                    Images = images.TryGetValue(seq, out var im) ? im : [],
                };
            }).ToList();
    }

    private static List<string> AuditTriggers(string sandbox, string schema)
    {
        using var c = Db.Open(sandbox);
        // 카탈로그 열끼리 데이터 정렬이 달라(type_desc는 Latin1_General_CI_AS_KS_WS) SQL에서 이어 붙이지 않고 C#에서 만든다.
        return Db.Rows(c, """
            SELECT OBJECT_NAME(tr.parent_id), te.type_desc, tr.name, te.is_first, te.is_last
            FROM sys.triggers AS tr
            JOIN sys.trigger_events AS te ON te.object_id = tr.object_id
            WHERE tr.parent_class = 1 AND OBJECT_SCHEMA_NAME(tr.parent_id) = @s
            ORDER BY 1, 2, 3;
            """, ("@s", schema))
            .Select(r => $"{r[0]} {r[1]} {r[2]} " + ((bool)r[4]! ? "LAST" : (bool)r[3]! ? "FIRST" : "NONE"))
            .ToList();
    }

    private static void Fill(CaseResult cr, CaseDef cd, List<TableMeta> metas, RunResult run, string seed, string prefix)
    {
        cr.Status = run.Status;
        cr.ErrorNumber = run.ErrorNumber;
        cr.Statements = run.Statements;
        cr.NetChanges = run.Diffs.Where(d => d.HasChanges || d.ComparatorIssues.Count > 0).ToList();
        cr.Checks.Add(Check.Eq("env: 시작 상태 = 시드 상태", seed, run.BeforeDigest));
        cr.Checks.Add(Check.Eq("env: ROLLBACK 뒤 시드 상태로 복귀", seed, run.AfterRollbackDigest));
        foreach (var d in run.Diffs.Where(d => d.ComparatorIssues.Count > 0))
            cr.Checks.Add(Check.Eq($"env: 비교기 해시-셀 일치 {d.Table}", "", string.Join("; ", d.ComparatorIssues)));
        cr.Checks.AddRange(Judge.Status(cd, run, prefix));
        cr.Checks.AddRange(Judge.Net(cd, metas, run.Diffs, prefix));
        if (prefix.Length == 0) Finish(cr);
    }

    private static void Finish(CaseResult cr)
    {
        var bad = cr.Checks.Where(x => x.Verdict == "MISMATCH").ToList();
        cr.Verdict = bad.Any(x => x.Item.StartsWith("env:", StringComparison.Ordinal)) ? "INVALID"
                   : bad.Count > 0 ? "MISMATCH" : "MATCH";
    }
}

// ---------------------------------------------------------------- 판정

internal static class Judge
{
    public static List<Check> Status(CaseDef cd, RunResult run, string prefix)
    {
        var exp = cd.Expected;
        var checks = new List<Check>
        {
            Check.Eq(prefix + "status", (string)exp["status"]!, run.Status),
            Check.Eq(prefix + "errorNumber", Values.Json(exp["errorNumber"]),
                run.ErrorNumber?.ToString(CultureInfo.InvariantCulture) ?? "null",
                run.Statements.SelectMany(s => s.Errors).Any()
                    ? "수집한 오류: " + string.Join(", ", run.Statements.SelectMany(s => s.Errors).Select(e => $"{e.Number}(class {e.Class})"))
                    : null),
        };
        foreach (var s in exp["statementStatus"]?.AsArray() ?? new JsonArray())
        {
            var idx = (int)s!["index"]!;
            var act = run.Statements.FirstOrDefault(x => x.Index == idx);
            checks.Add(Check.Eq($"{prefix}statement {idx} status", (string)s["status"]!, act?.Status ?? "(없음)"));
        }
        return checks;
    }

    public static List<Check> Net(CaseDef cd, List<TableMeta> metas, List<TableDiff> diffs, string prefix)
    {
        var checks = new List<Check>();
        var metaBy = metas.ToDictionary(m => m.Display, StringComparer.OrdinalIgnoreCase);
        var diffBy = diffs.ToDictionary(d => d.Table, StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in cd.Expected["changes"]!.AsArray())
        {
            var ch = node!.AsObject();
            var table = (string)ch["table"]!;
            listed.Add(table);
            if (!metaBy.TryGetValue(table, out var m)) { checks.Add(Check.Eq($"{prefix}{table} 테이블 존재", "있음", "없음")); continue; }
            var d = diffBy[table];
            var keyCols = ch["keyColumns"]!.AsArray().Select(x => (string)x!).ToList();
            checks.Add(Check.Eq($"{prefix}{table} keyColumns", string.Join(",", keyCols), string.Join(",", m.Key)));
            string ExpKey(JsonNode row) => Values.KeyText(m.Key, k => Values.Json(row["key"]?[k]));
            List<JsonObject> Rows(string kind) => (ch[kind]?.AsArray() ?? new JsonArray()).Select(r => r!.AsObject()).ToList();
            var ins = Rows("inserted");
            var upd = Rows("updated");
            var del = Rows("deleted");
            checks.Add(Check.Set($"{prefix}{table} inserted", ins.Select(ExpKey), d.Inserted));
            checks.Add(Check.Set($"{prefix}{table} updated", upd.Select(ExpKey), d.Updated.Select(u => u.Key)));
            checks.Add(Check.Set($"{prefix}{table} deleted", del.Select(ExpKey), d.Deleted));
            foreach (var row in upd)
            {
                var key = ExpKey(row);
                var act = d.Updated.FirstOrDefault(u => u.Key == key);
                if (act is null) continue;
                var cols = (row["changedColumns"]?.AsArray() ?? new JsonArray()).Select(x => (string)x!).ToList();
                checks.Add(Check.Set($"{prefix}{table} {key} changedColumns", cols, act.ChangedColumns));
                if (row["after"] is JsonObject after)
                    foreach (var (col, val) in after)
                        checks.Add(Check.Eq($"{prefix}{table} {key} after.{col}", Values.Json(val),
                            act.AfterRow.TryGetValue(col, out var v) ? Values.Text(v) : "(열 없음)"));
            }
            foreach (var row in ins)
            {
                var key = ExpKey(row);
                if (row["after"] is not JsonObject after || !d.InsertedRows.TryGetValue(key, out var actRow)) continue;
                foreach (var (col, val) in after)
                    checks.Add(Check.Eq($"{prefix}{table} {key} after.{col}", Values.Json(val),
                        actRow.TryGetValue(col, out var v) ? Values.Text(v) : "(열 없음)"));
            }
        }
        foreach (var d in diffs.Where(x => !listed.Contains(x.Table)))
        {
            var sameSchema = d.Table.StartsWith(cd.Schema + ".", StringComparison.OrdinalIgnoreCase);
            if (!sameSchema && !d.HasChanges) continue;   // 다른 스키마는 바뀐 경우만 적는다
            checks.Add(Check.Eq($"{prefix}{d.Table} 변경 0({(sameSchema ? "정답에 없는 같은 패턴 스키마 테이블" : "다른 스키마")})",
                "I0 U0 D0", $"I{d.Inserted.Count} U{d.Updated.Count} D{d.Deleted.Count}"));
        }
        return checks;
    }
}

internal sealed record ExpFiring(string Node, string Table, string Operation, long RowCount, string CauseKind, int? Depth, bool DepthJudged);

internal static class Firings
{
    /// <summary>감사 헤더를 정답 firings와 대조. 대안(observedOnly.alternatives)이 있으면 완전히 맞는 대안을 고른다.</summary>
    public static (List<Check> Checks, string? Alternative) Judge(CaseDef cd, List<AuditFiring> actual)
    {
        var edges = (cd.Expected["edges"]?.AsArray() ?? new JsonArray()).Select(e => e!.AsObject()).ToList();
        var baseFirings = cd.Expected["firings"]!.AsArray().Select(f => ToFiring(f!.AsObject(), edges)).ToList();
        var candidates = new List<(string Label, List<ExpFiring> Firings)>();
        foreach (var oo in cd.Expected["observedOnly"]?.AsArray() ?? new JsonArray())
            foreach (var alt in oo!["alternatives"]?.AsArray() ?? new JsonArray())
            {
                if (alt is not JsonObject ao) continue;   // 문자열 대안(형제 순서 등)은 묶음 집합과 무관
                var altEdges = edges.Concat((ao["edges"]?.AsArray() ?? new JsonArray()).Select(e => e!.AsObject())).ToList();
                var extra = (ao["firings"]?.AsArray() ?? new JsonArray()).Select(f => ToFiring(f!.AsObject(), altEdges)).ToList();
                candidates.Add(((string)ao["label"]!, baseFirings.Concat(extra).ToList()));
            }
        if (candidates.Count == 0) return (Match(baseFirings, actual), null);

        var results = candidates.Select(c => (c.Label, Checks: Match(c.Firings, actual))).ToList();
        var ok = results.Where(r => r.Checks.All(x => x.Verdict != "MISMATCH")).ToList();
        if (ok.Count == 1)
        {
            var chosen = ok[0];
            chosen.Checks.Add(Check.Observed("observedOnly 대안 판정", string.Join(" / ", candidates.Select(c => c.Label)), chosen.Label,
                "다른 대안: " + string.Join("; ", results.Where(r => r.Label != chosen.Label)
                    .Select(r => r.Label + " → " + string.Join(", ", r.Checks.Where(x => x.Verdict == "MISMATCH").Select(x => x.Item))))));
            return (chosen.Checks, chosen.Label);
        }
        var first = results[0].Checks;
        first.Add(Check.Eq("observedOnly 대안 판정", "대안 하나만 맞음", ok.Count == 0 ? "맞는 대안 없음" : "여러 대안이 맞음"));
        return (first, null);
    }

    private static ExpFiring ToFiring(JsonObject f, List<JsonObject> edges)
    {
        var node = (string)f["node"]!;
        var parts = node.Split(':');   // F:<schema.table>:<OP>[#k]
        var table = (string?)f["table"] ?? parts[1];
        var op = (string?)f["operation"] ?? parts[2].Split('#')[0];
        return new ExpFiring(node, table, op, (long)f["rowCount"]!, (string?)f["causeKind"] ?? "", (int?)f["depth"], DepthJudged(node, edges));
    }

    /// <summary>문장까지 거슬러 오르는 경로에 FK 간선이 있으면 깊이는 H2 전제값이라 판정하지 않는다(answer-format 채점 메모).</summary>
    private static bool DepthJudged(string node, List<JsonObject> edges)
    {
        var parentOf = edges.GroupBy(e => (string)e["child"]!).ToDictionary(g => g.Key, g => g.First());
        var cur = node;
        for (var guard = 0; guard < 64 && parentOf.TryGetValue(cur, out var e); guard++)
        {
            if ((string)e["kind"]! == "FK") return false;
            cur = (string)e["parent"]!;
        }
        return true;
    }

    private static List<Check> Match(List<ExpFiring> expected, List<AuditFiring> actual)
    {
        var checks = new List<Check>();
        var used = new HashSet<long>();
        var pairs = new List<(ExpFiring E, AuditFiring A)>();
        var pending = new List<ExpFiring>();
        static bool Same(ExpFiring e, AuditFiring a) =>
            string.Equals(e.Table, a.Table, StringComparison.OrdinalIgnoreCase) && e.Operation == a.Operation && e.RowCount == a.RowCount;
        foreach (var e in expected)
        {
            var a = actual.FirstOrDefault(x => !used.Contains(x.Seq) && Same(e, x) && e.Depth == x.NestLevel);
            if (a is null) { pending.Add(e); continue; }
            used.Add(a.Seq);
            pairs.Add((e, a));
        }
        foreach (var e in pending)
        {
            var a = actual.FirstOrDefault(x => !used.Contains(x.Seq) && Same(e, x));
            if (a is null)
            {
                checks.Add(Check.Eq($"firing {e.Node}", $"{e.Table} {e.Operation} rows={e.RowCount} depth={e.Depth}", "(감사 헤더 없음)"));
                continue;
            }
            used.Add(a.Seq);
            pairs.Add((e, a));
        }
        foreach (var (e, a) in pairs.OrderBy(p => p.A.Seq))
        {
            checks.Add(Check.Eq($"firing {e.Node} (table, op, rowCount)", $"{e.Table} {e.Operation} {e.RowCount}", $"{a.Table} {a.Operation} {a.RowCount}",
                $"captureSeq {a.Seq}, inserted {a.InsertedCount}, deleted {a.DeletedCount}"));
            var dExp = e.Depth?.ToString(CultureInfo.InvariantCulture) ?? "null";
            var dAct = a.NestLevel.ToString(CultureInfo.InvariantCulture);
            checks.Add(e.DepthJudged
                ? Check.Eq($"firing {e.Node} depth", dExp, dAct)
                : Check.Observed($"firing {e.Node} depth (FK 연쇄 경로, H2 전제값)", dExp, dAct));
        }
        foreach (var a in actual.Where(x => !used.Contains(x.Seq)))
            checks.Add(Check.Eq($"firing (정답에 없음) captureSeq {a.Seq}", "(없음)", $"{a.Table} {a.Operation} rows={a.RowCount} depth={a.NestLevel}"));
        return checks;
    }
}

// ---------------------------------------------------------------- 환경

internal static class Env
{
    private static readonly Dictionary<string, Dictionary<string, object?>> DbOptions = new(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string db)
    {
        using var c = Db.Open("master");
        return Db.Scalar(c, "SELECT DB_ID(@d);", ("@d", db)) is not null;
    }

    public static object? DbOption(string db, string name) =>
        DbOptions.TryGetValue(db, out var o) && o.TryGetValue(name, out var v) ? v : null;

    public static Dictionary<string, object?> Read(List<string> dbs)
    {
        using var c = Db.Open("master");
        var env = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["productVersion"] = Db.Scalar(c, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64));"),
            ["productUpdateLevel"] = Db.Scalar(c, "SELECT CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(64));"),
            ["edition"] = Db.Scalar(c, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128));"),
            ["nestedTriggers"] = Db.Scalar(c, "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'nested triggers';"),
            ["disallowResultsFromTriggers"] = Db.Scalar(c, "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'disallow results from triggers';"),
            ["sessionOptions"] = Db.Scalar(c, "SELECT @@OPTIONS;"),
            ["xactAbort"] = Db.Scalar(c, "SELECT CASE WHEN (@@OPTIONS & 16384) <> 0 THEN 1 ELSE 0 END;"),
        };
        foreach (var db in dbs)
        {
            var row = Db.Rows(c, """
                SELECT is_recursive_triggers_on, compatibility_level, is_read_committed_snapshot_on, snapshot_isolation_state_desc, collation_name
                FROM sys.databases WHERE name = @d;
                """, ("@d", db)).FirstOrDefault();
            if (row is null) continue;
            var o = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["is_recursive_triggers_on"] = row[0],
                ["compatibility_level"] = row[1],
                ["is_read_committed_snapshot_on"] = row[2],
                ["snapshot_isolation_state_desc"] = row[3],
                ["collation_name"] = row[4],
            };
            DbOptions[db] = o;
            env["db:" + db] = o;
        }
        return env;
    }
}

// ---------------------------------------------------------------- 텍스트 보고

internal static class TextReport
{
    public static string Render(SuiteResult s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# CascadeLab.Verify {s.Mode} {s.StartedAt}");
        sb.AppendLine("env: " + string.Join("; ", s.Env.Select(kv => kv.Key + "=" + (kv.Value is Dictionary<string, object?> d
            ? "{" + string.Join(", ", d.Select(x => x.Key + "=" + Values.Text(x.Value))) + "}"
            : Values.Text(kv.Value)))));
        sb.AppendLine("seed digest: " + string.Join(", ", s.SeedDigest.Select(kv => kv.Key + "=" + kv.Value)));
        foreach (var c in s.EnvChecks) sb.AppendLine($"[{c.Verdict}] {c.Item}: exp={c.Expected} act={c.Actual}");
        sb.AppendLine();
        foreach (var c in s.Cases)
        {
            var judged = c.Checks.Where(x => x.Verdict != "OBSERVED").ToList();
            sb.AppendLine($"== {c.Id} ({c.CaseFile}) db={c.Database} verdict={c.Verdict}"
                + (c.Reason is null ? "" : $" reason={c.Reason}")
                + $" judged={judged.Count} match={judged.Count(x => x.Verdict == "MATCH")} mismatch={judged.Count(x => x.Verdict == "MISMATCH")}"
                + $" observed={c.Checks.Count(x => x.Verdict == "OBSERVED")}");
            if (c.Verdict is "SKIPPED" or "ERROR") { sb.AppendLine(); continue; }
            foreach (var st in c.Statements)
            {
                sb.AppendLine($"   stmt {st.Index}: {st.Sql}");
                sb.AppendLine($"     status={st.Status} rowsAffected={st.RowsAffected?.ToString(CultureInfo.InvariantCulture) ?? "-"} tranCountAfter={st.TranCountAfter} xactStateAfter={st.XactStateAfter}");
                foreach (var e in st.Errors) sb.AppendLine($"     error {e.Number} class={e.Class} state={e.State} line={e.Line}: {e.Message}");
                foreach (var m in st.InfoMessages) sb.AppendLine($"     info {m}");
            }
            foreach (var d in c.NetChanges) sb.AppendLine($"   net {d.Table}: {d.Summary()}");
            foreach (var d in c.NetChanges)
                foreach (var u in d.Updated)
                    sb.AppendLine($"     {d.Table} {u.Key}: " + string.Join(", ", u.ChangedColumns.Select(col => $"{col} {Values.Text(u.Before[col])}->{Values.Text(u.After[col])}")));
            if (c.Firings is not null)
            {
                sb.AppendLine($"   audit order (captureSeq:table:op:depth:rows): {c.CaptureOrder}");
                foreach (var f in c.Firings)
                    foreach (var im in f.Images) sb.AppendLine($"     {f.Seq} {im}");
            }
            if (c.AuditTriggers is not null) sb.AppendLine("   audit triggers: " + string.Join(" | ", c.AuditTriggers));
            if (c.MatchedAlternative is not null) sb.AppendLine($"   matched alternative: {c.MatchedAlternative}");
            foreach (var r in c.RepeatRuns ?? []) sb.AppendLine($"   {r}");
            foreach (var x in c.Checks)
                sb.AppendLine($"   [{x.Verdict}] {x.Item}: exp={x.Expected} act={x.Actual}" + (x.Note is null ? "" : $"  ({x.Note})"));
            sb.AppendLine();
        }
        var groups = s.Cases.GroupBy(c => c.Verdict).Select(g => $"{g.Key} {g.Count()}");
        sb.AppendLine("summary: " + string.Join(", ", groups) + $" (사례 {s.Cases.Count}개)");
        return sb.ToString();
    }
}
