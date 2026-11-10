using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Core.Catalog;
using DbRipple.TestSupport;
using Microsoft.Data.SqlClient;

namespace DbRipple.Diff.Tests;

/// <summary>
/// CL01~CL08 순 변경이 1주차 오라클 결과(cascadelab/verification/logs/oracle-2026-11-10.json)와 같은지.
/// 자기 전용 CascadeLab 기준선(dbrt_dif_)의 샌드박스에서 사례 문장을 열린 트랜잭션으로 실행하고, 같은 연결로 샌드박스 대 기준 스냅샷을 비교한 뒤 ROLLBACK한다.
/// 오라클 파일은 기대값(데이터)으로만 읽는다(하네스 코드와 공유 없음).
/// </summary>
public class CascadeLabOracleTests
{
    static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(RepoPaths.CascadeLab, "verification", "logs", "oracle-2026-11-10.json"))).RootElement;

    [SqlFact]
    public Task Default_cases_match_oracle() => RunAsync(CascadeLabVariant.Default, "CascadeLab");

    [SqlFact]
    public Task Recursive_triggers_cases_match_oracle() => RunAsync(CascadeLabVariant.RecursiveTriggers, "CascadeLab_RT");

    static async Task RunAsync(CascadeLabVariant variant, string oracleDb)
    {
        await using var tb = await TestBaseline.BuildCascadeLabAsync("dif", variant);
        await using var conn = await TestDb.OpenAsync(tb.Sandbox);
        var tables = (await CatalogReader.ReadAsync(conn)).ToCaptureInput()
            .Where(t => !string.Equals(t.Schema, AuditTriggerGenerator.StoreSchema, StringComparison.OrdinalIgnoreCase)).ToList();
        var differ = new NetDiffer();
        var cases = Oracle.GetProperty("Cases").EnumerateArray().Where(c => c.GetProperty("Database").GetString() == oracleDb).ToList();
        Assert.NotEmpty(cases);

        var failures = new List<string>();
        foreach (var c in cases)
        {
            var id = c.GetProperty("Id").GetString()!;
            await Sql.ExecAsync(conn, null, "SET XACT_ABORT ON; BEGIN TRAN;");
            string? error = null;
            try
            {
                foreach (var st in c.GetProperty("Statements").EnumerateArray())
                    await Sql.ExecAsync(conn, null, st.GetProperty("Sql").GetString()!);
            }
            catch (SqlException ex) { error = $"{ex.Number}"; }

            var expectedStatus = c.GetProperty("Status").GetString();
            if ((error is null) != (expectedStatus == "ok")) failures.Add($"{id}: 상태 기대 {expectedStatus}, 실행 오류 {error ?? "없음"}");

            var actual = await differ.DiffAsync(new SqlSession { Connection = conn }, tb.Sandbox, tb.Snapshot, tables, default);
            var exp = Expected(c);
            var act = Actual(actual);
            if (!exp.SequenceEqual(act))
                failures.Add($"{id}:\n  기대:\n    {string.Join("\n    ", exp)}\n  실제:\n    {string.Join("\n    ", act)}");

            await Sql.ExecAsync(conn, null, "IF @@TRANCOUNT > 0 ROLLBACK;");
        }

        // 모든 사례를 되돌린 뒤 샌드박스 대 스냅샷 차이 0(트랜잭션 없음)
        var residual = await differ.DiffAsync(new SqlSession { Connection = conn }, tb.Sandbox, tb.Snapshot, tables, default);
        if (residual.Count > 0) failures.Add("롤백 뒤 차이:\n" + Sql.Dump(residual));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>오라클 NetChanges → 정렬된 사실 문자열(테이블, 종류, 키, 바뀐 열과 그 열의 before→after).</summary>
    static List<string> Expected(JsonElement c)
    {
        var list = new List<string>();
        foreach (var t in c.GetProperty("NetChanges").EnumerateArray())
        {
            var table = t.GetProperty("Table").GetString()!;
            foreach (var k in t.GetProperty("Inserted").EnumerateArray()) list.Add($"{table} INSERTED {KeyText(k.GetString()!)}");
            foreach (var k in t.GetProperty("Deleted").EnumerateArray()) list.Add($"{table} DELETED {KeyText(k.GetString()!)}");
            foreach (var u in t.GetProperty("Updated").EnumerateArray())
            {
                var cols = u.GetProperty("ChangedColumns").EnumerateArray().Select(x => x.GetString()!).ToList();
                var b = u.GetProperty("Before");
                var a = u.GetProperty("After");
                list.Add($"{table} UPDATED {KeyText(u.GetProperty("Key").GetString()!)} " +
                         string.Join(";", cols.Select(col => $"{col}:{NetDiffer.ToText(b.GetProperty(col))}->{NetDiffer.ToText(a.GetProperty(col))}")));
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static List<string> Actual(IReadOnlyList<NetChange> changes)
    {
        var list = changes.Select(n =>
        {
            var key = string.Join(",", n.Key.Columns.Zip(n.Key.Values, (k, v) => $"{k}={v}"));
            return n.Change == NetChangeKind.Updated
                ? $"{n.Table} UPDATED {key} " + string.Join(";", n.ChangedColumns.Select(col => $"{col}:{n.Before![col]}->{n.After![col]}"))
                : $"{n.Table} {n.Change.ToString().ToUpperInvariant()} {key}";
        }).ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static string KeyText(string keyJson)
    {
        using var d = JsonDocument.Parse(keyJson);
        return string.Join(",", d.RootElement.EnumerateObject().Select(p => $"{p.Name}={NetDiffer.ToText(p.Value)}"));
    }
}
