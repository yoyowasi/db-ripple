using System.Net;
using System.Text;
using System.Text.Json;
using DbRipple.Api;
using DbRipple.Contracts;
using DbRipple.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DbRipple.Api.Tests;

/// <summary>골격 스모크: 엔드포인트가 가짜 서비스로 뜨고 §6.4 형식(상태 코드, 422 rejects, 열거형 문자열)을 지킨다.</summary>
public class SmokeTests
{
    sealed class FakeSandbox : ISandboxManager
    {
        public Task<SandboxStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new SandboxStatus
        {
            State = SandboxState.Ready, BaselineAt = DateTimeOffset.Parse("2026-11-06T09:58:02+09:00"), FingerprintMatch = true, Locked = false,
            Manifest = new ManifestSummary { CoveredTables = 71, ExcludedTables = 0, AuditNotLast = 0 },
        });
        public Task<BaselineContext> GetContextAsync(CancellationToken ct) => throw new PreviewConflictException(ConflictReason.NoBaseline, "기준선 없음");
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public void StartRefresh() { }
        public Task<IAsyncDisposable> AcquirePreviewLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public void MarkDirty(string reason) { }
    }

    sealed class RejectingPipeline : IPreviewPipeline
    {
        public Task<PreviewOutcome> RunAsync(string sql, PreviewOptions options, IEvaluationHook? hook, CancellationToken ct) =>
            Task.FromResult(new PreviewOutcome
            {
                Analysis = new AnalysisResult
                {
                    Rejects =
                    [
                        new Diagnostic
                        {
                            StatementIndex = 2, Severity = Severity.Error, RuleId = "REJECT_TRUNCATE", Message = "TRUNCATE", DocRef = "A21",
                            Span = new SourceSpan { StartLine = 4, StartColumn = 1, EndLine = 4, EndColumn = 18 },
                        },
                    ],
                },
            });
    }

    static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbRippleApi();
        builder.Services.AddSingleton<ISandboxManager, FakeSandbox>();
        builder.Services.AddSingleton<IPreviewPipeline, RejectingPipeline>();
        builder.Services.AddSingleton<IPreviewStore>(new PreviewStore(Path.GetTempPath(), TimeProvider.System));
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapOpenApi();
        app.MapDbRippleApi();
        await app.StartAsync();
        return app;
    }

    /// <summary>OpenAPI 문서가 계약 타입(제네릭 RowPage, 다형 그래프 노드, Core Fingerprint)으로 만들어진다(프런트 타입 생성 입력, D6).</summary>
    [Fact]
    public async Task OpenApi_document_is_generated_from_contracts()
    {
        await using var app = await StartAsync();
        var res = await app.GetTestClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var p in new[] { "/api/sandbox", "/api/analyze", "/api/previews", "/api/previews/{id}", "/api/previews/{id}/rows", "/api/previews/{id}/cancel", "/api/previews/{id}/export" })
            Assert.True(paths.TryGetProperty(p, out _), p);
        Assert.Contains("trigger_blocked", text);
        Assert.Contains("\"statement\"", text);
    }

    [Fact]
    public async Task Sandbox_status_rejected_preview_and_bad_query_follow_contract()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var status = await client.GetAsync("/api/sandbox");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using (var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            Assert.Equal("ready", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal(71, doc.RootElement.GetProperty("manifest").GetProperty("coveredTables").GetInt32());
        }

        var rejected = await client.PostAsync("/api/previews",
            new StringContent("""{"sql":"TRUNCATE TABLE t;","options":{"imageCap":10}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using (var doc = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
        {
            Assert.Equal(ProblemTypes.Rejected, doc.RootElement.GetProperty("type").GetString());
            var reject = doc.RootElement.GetProperty("rejects")[0];
            Assert.Equal("REJECT_TRUNCATE", reject.GetProperty("ruleId").GetString());
            Assert.Equal(4, reject.GetProperty("span").GetProperty("startLine").GetInt32());
        }

        var badView = await client.GetAsync("/api/previews/x/rows?view=grid");
        Assert.Equal(HttpStatusCode.BadRequest, badView.StatusCode);

        // 준비되지 않은 샌드박스(PreviewConflictException) → 409 ProblemDetails
        var analyze = await client.PostAsync("/api/analyze", new StringContent("""{"sql":"DELETE FROM t;"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, analyze.StatusCode);
        Assert.Equal("application/problem+json", analyze.Content.Headers.ContentType?.MediaType);
    }
}
