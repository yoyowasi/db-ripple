using System.Text.Json;
using System.Text.Json.Nodes;
using DbRipple.Contracts;
using DbRipple.Core.Capture;

namespace DbRipple.TestSupport.Tests;

/// <summary>공용 계약의 JSON 모양이 설계 §6.2·§6.3·§6.4 예시와 같은지(속성 이름, 열거형 문자열, null, 판별자).</summary>
public class ContractJsonTests
{
    static JsonNode Json<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, ContractJson.Options))!;

    [Fact]
    public void Firing_matches_design_example()
    {
        var f = new Firing
        {
            Id = ContractIds.Firing(1005), CaptureSeq = 1005, StatementIndex = 1, Depth = 2, Table = "Sales.SalesOrderHeader",
            Operation = ChangeOperation.Update, RowCount = 1, ImageMode = ImageMode.Full, AuditOrder = AuditOrder.Last,
            Cause = new Cause
            {
                Kind = CauseKind.Trigger, Confidence = Confidence.Inferred, ParentFiringId = "f-1006",
                TriggerCandidates = ["Sales.iduSalesOrderDetail"], Evidence = [Evidence.Depth, Evidence.PostorderBracket, Evidence.StaticWriteSet],
            },
            OrderGuarantee = OrderGuarantee.Guaranteed,
        };
        var expected = JsonNode.Parse("""
            {
              "id": "f-1005", "captureSeq": 1005, "statementIndex": 1, "depth": 2, "table": "Sales.SalesOrderHeader",
              "operation": "UPDATE", "rowCount": 1, "imageMode": "FULL", "auditOrder": "LAST",
              "cause": { "kind": "TRIGGER", "confidence": "inferred", "parentFiringId": "f-1006", "foreignKey": null,
                         "triggerCandidates": ["Sales.iduSalesOrderDetail"], "evidence": ["depth", "postorderBracket", "staticWriteSet"] },
              "orderGuarantee": "guaranteed"
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, Json(f)), Json(f).ToJsonString());
    }

    [Fact]
    public void RowChange_and_NetChange_use_string_values_and_key_kind()
    {
        var rc = new RowChange
        {
            Id = ContractIds.RowChange(1001, 1), FiringId = "f-1001", Operation = ChangeOperation.Insert,
            Key = new RowKey { Kind = KeyKind.PK, Columns = ["TransactionID"], Values = ["213443"] },
            After = new Dictionary<string, string?> { ["TransactionID"] = "213443", ["Quantity"] = "3" },
            Volatile = [new VolatileCell { Column = "TransactionID", Reason = VolatileReason.Identity }],
            Pairing = Pairing.SingleRow, FinalAfterSource = FinalAfterSource.Audit,
        };
        var j = Json(rc);
        Assert.Equal("rc-1001-1", (string?)j["id"]);
        Assert.Equal("PK", (string?)j["key"]!["kind"]);
        Assert.Null(j["before"]);
        Assert.True(j.AsObject().ContainsKey("before"));
        Assert.Equal("IDENTITY", (string?)j["volatile"]![0]!["reason"]);
        Assert.Equal("SINGLE_ROW", (string?)j["pairing"]);
        Assert.Equal("AUDIT", (string?)j["finalAfterSource"]);

        var nc = new NetChange
        {
            Table = "dbo.Heap", Key = new RowKey { Kind = KeyKind.ROW }, Change = NetChangeKind.MultisetRemoved, Multiplicity = 2,
            Before = new Dictionary<string, string?> { ["a"] = null },
        };
        var n = Json(nc);
        Assert.Equal("MULTISET_REMOVED", (string?)n["change"]);
        Assert.Equal("ROW", (string?)n["key"]!["kind"]);
        Assert.Null(n["explainedBy"]);
        var back = JsonSerializer.Deserialize<NetChange>(n.ToJsonString(), ContractJson.Options)!;
        Assert.Equal(NetChangeKind.MultisetRemoved, back.Change);
        Assert.Null(back.Before!["a"]);
    }

    [Fact]
    public void Graph_nodes_use_type_discriminator_and_round_trip()
    {
        var g = new GraphDto
        {
            Nodes =
            [
                new StatementNode { Id = "st-1", StatementIndex = 1, Label = "UPDATE …", RowsAffected = 1, Status = StatementStatus.Ok },
                new FiringNode { Id = "f-1006", FiringId = "f-1006", Table = "Sales.SalesOrderDetail", Operation = ChangeOperation.Update, RowCount = 1, Depth = 1, CaptureSeq = 1006 },
                new TriggerNode { Id = "t-1", Trigger = "Sales.iduSalesOrderDetail", ParentFiringId = "f-1006", StatementIndex = 1, Observed = true },
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", Source = "st-1", Target = "f-1006", Kind = EdgeKind.Direct, Confidence = Confidence.Certain, OrderGuarantee = OrderGuarantee.Guaranteed },
            ],
        };
        var j = Json(g);
        Assert.Equal("statement", (string?)j["nodes"]![0]!["type"]);
        Assert.Equal("firing", (string?)j["nodes"]![1]!["type"]);
        Assert.Equal("trigger", (string?)j["nodes"]![2]!["type"]);
        Assert.Equal("DIRECT", (string?)j["edges"]![0]!["kind"]);
        Assert.Equal("observed_only", JsonSerializer.Serialize(OrderGuarantee.ObservedOnly, ContractJson.Options).Trim('"'));
        var back = JsonSerializer.Deserialize<GraphDto>(j.ToJsonString(), ContractJson.Options)!;
        Assert.IsType<TriggerNode>(back.Nodes[2]);
        Assert.Equal("Sales.iduSalesOrderDetail", ((TriggerNode)back.Nodes[2]).Trigger);
    }

    [Fact]
    public void Status_values_match_design()
    {
        Assert.Equal("\"trigger_blocked\"", JsonSerializer.Serialize(PreviewStatus.TriggerBlocked, ContractJson.Options));
        Assert.Equal("\"not_run\"", JsonSerializer.Serialize(StatementStatus.NotRun, ContractJson.Options));
        Assert.Equal("\"FK_SET_NULL\"", JsonSerializer.Serialize(CauseKind.FkSetNull, ContractJson.Options));
        Assert.Equal("\"SET_DEFAULT\"", JsonSerializer.Serialize(FkAction.SetDefault, ContractJson.Options));
        Assert.Equal("\"net\"", JsonSerializer.Serialize(RowView.Net, ContractJson.Options));
    }

    [Theory]
    [InlineData("dbr_audit_T_U", true)]
    [InlineData("Sales.dbr_audit_SalesOrderHeader_U", true)]
    [InlineData("[Sales].[dbr_audit_SalesOrderHeader_U]", true)]
    [InlineData("uSalesOrderHeader", false)]
    [InlineData("my_dbr_audit_x", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Tool_fault_is_judged_by_procedure_prefix(string? procedure, bool expected) =>
        Assert.Equal(expected, ErrorClassification.IsToolFault(procedure));
}
