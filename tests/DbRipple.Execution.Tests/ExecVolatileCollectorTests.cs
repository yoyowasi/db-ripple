using DbRipple.Contracts;

namespace DbRipple.Execution.Tests;

public partial class CollectorTests
{
    static AnalyzedStatement ExecStatement(params TableColumn[] writes) => new()
    {
        Index = 1, Batch = 1, Position = 1, Kind = ChangeOperation.Exec, Target = null, Procedure = "dbo.q", Sql = "EXEC dbo.q;",
        Span = new SourceSpan { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 },
        Plan = new ProcedurePlan { Procedure = "dbo.q", ExecSql = "x", WriteTargets = ["dbo.T", "dbo.I"], NondeterministicWrites = writes },
    };

    [Fact]
    public void Exec_procedure_nondeterministic_writes_are_statement_function_volatile_at_depth_1()
    {
        // 프로시저 본문의 UPDATE dbo.T SET v = GETDATE()…, INSERT dbo.I (note) VALUES (NEWID()…)는 깊이 1 묶음이다(2026-11-17).
        // 깊이 2(트리거) 묶음에는 프로시저 비결정 쓰기를 붙이지 않는다(트리거 쪽은 TRIGGER_GETDATE 규칙).
        var analysis = Analysis(ExecStatement(new TableColumn { Table = "dbo.T", Column = "v" }, new TableColumn { Table = "DBO.I", Column = "note" }));
        var r = Collect(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":7,\"amt\":12.50,\"c\":70}"])
            .Firing(102, 3, ChangeOperation.Insert, 1, inserted: ["{\"id\":7,\"at\":\"2026-11-10T10:00:00.123\",\"note\":\"x\"}"])
            .Firing(103, 3, ChangeOperation.Insert, 2, inserted: ["{\"id\":8,\"at\":\"2026-11-10T10:00:00.123\",\"note\":\"y\"}"])
            .EndStatement(103).Build(), analysis: analysis);

        var upd = r.RowChanges.Single(x => x.FiringId == "f-101");
        Assert.Equal([("v", VolatileReason.StatementFunction)], upd.Volatile.Select(v => (v.Column, v.Reason)));
        var ins = r.RowChanges.Single(x => x.FiringId == "f-102");
        Assert.Contains(("note", VolatileReason.StatementFunction), ins.Volatile.Select(v => (v.Column, v.Reason)));
        var nested = r.RowChanges.Single(x => x.FiringId == "f-103");
        Assert.DoesNotContain(nested.Volatile, v => v.Column == "note");
    }

    [Fact]
    public void Exec_without_nondeterministic_writes_marks_nothing_extra()
    {
        var r = Collect(new RawBuilder()
            .Firing(101, 1, ChangeOperation.Update, 1, deleted: [T1], inserted: ["{\"id\":1,\"code\":\"a\",\"v\":7,\"amt\":12.50,\"c\":70}"])
            .EndStatement(101).Build(), analysis: Analysis(ExecStatement()));

        Assert.Empty(Assert.Single(r.RowChanges).Volatile);
    }
}
