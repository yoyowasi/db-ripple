// 조립 루트(§3.1 백엔드). 연결 문자열은 환경 변수 DBRIPPLE_SQL_CONNECTION에서만 읽는다(scripts/env.ps1, 출력 금지).
// 포트는 localhost에만 연다(R13). 구성 값(DB 이름·경로)은 appsettings.json의 DbRipple 절.
// 상대 경로(ManifestPath, PreviewStoreDir)는 콘텐츠 루트 기준이다. 기본값은 .data/ 아래(저장소에 커밋된 logs/week1 매니페스트를 재구축이 덮어쓰지 않게).
using DbRipple.Analysis;
using DbRipple.Api;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Diff;
using DbRipple.Execution;
using DbRipple.Preview;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("DbRipple");
builder.WebHost.UseUrls(cfg["Urls"] ?? "http://127.0.0.1:5080");

string FullPath(string key, string? fallback = null) =>
    Path.GetFullPath(cfg[key] ?? fallback ?? throw new InvalidOperationException($"DbRipple:{key} 없음"), builder.Environment.ContentRootPath);

string ConnectionString() => Environment.GetEnvironmentVariable("DBRIPPLE_SQL_CONNECTION") is { Length: > 0 } cs
    ? cs
    : throw new InvalidOperationException("DBRIPPLE_SQL_CONNECTION 없음(scripts/env.ps1 점 소싱)");

builder.Services.AddDbRippleApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITriggerAnalyzer, TriggerAnalyzer>();
builder.Services.AddSingleton<IBatchAnalyzer, BatchAnalyzer>();
builder.Services.AddSingleton<INetDiffer, NetDiffer>();
builder.Services.AddSingleton<IChangeCollector, ChangeCollector>();
builder.Services.AddSingleton<ICausalResolver, CausalResolver>();
// Q12: 실행기가 멱등으로 만드는 DML 전용 사용자(null이면 가장하지 않음: 테스트 전용)
builder.Services.AddSingleton(new ExecutionSettings { ExecuteAsUser = cfg["ExecuteAsUser"] ?? DbRipple.Core.Baseline.PreviewUserSql.UserName });
builder.Services.AddSingleton<IPreviewExecutor>(sp =>
    new PreviewExecutor(ConnectionString(), sp.GetRequiredService<ExecutionSettings>(), sp.GetRequiredService<INetDiffer>()));
builder.Services.AddSingleton<ISandboxManager>(sp => new SandboxManager(
    new SandboxManagerOptions
    {
        ConnectionString = ConnectionString(),
        OriginDb = cfg["OriginDb"] ?? "AW_Origin",
        SandboxDb = cfg["SandboxDb"] ?? "AW_Sandbox",
        SnapshotDb = cfg["SnapshotDb"] ?? "AW_Sandbox_base",
        BackupPath = cfg["BackupPath"] ?? throw new InvalidOperationException("DbRipple:BackupPath 없음"),
        ManifestPath = FullPath("ManifestPath"),
    },
    sp.GetRequiredService<ITriggerAnalyzer>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IPreviewStore>(sp =>
    new PreviewStore(FullPath("PreviewStoreDir", ".data/previews"), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IPreviewPipeline, PreviewPipeline>();

var app = builder.Build();
app.Logger.LogInformation("DbRipple 매니페스트 {Manifest}, 미리보기 저장소 {Store}", FullPath("ManifestPath"), FullPath("PreviewStoreDir", ".data/previews"));
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapDbRippleApi();
app.Run();
