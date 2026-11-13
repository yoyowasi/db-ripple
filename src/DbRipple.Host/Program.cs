// 조립 루트(§3.1 백엔드). 두 가지로 돈다(2026-11-13 N4):
// - 개발 모드(Debug 빌드 + --dev 인자 + 실행 파일 폴더에 wwwroot 없음, Properties/launchSettings.json): Vite(5273)가 /api를 5080으로 프록시. 연결은 환경 변수(scripts/env.ps1, 출력 금지).
// - 실행 파일 모드(그 밖, 환경 변수와 무관): wwwroot의 web 빌드를 같은 출처로 서빙, 실행 토큰 쿠키 + 요청 키 필수, 시작 뒤 기본 브라우저로 실행 URL을 연다(--no-browser로 끔).
// 연결은 환경 변수(DBRIPPLE_*_CONNECTION)가 있으면 그것(설정 API 읽기 전용), 없으면 %LOCALAPPDATA%\db-ripple\profile.json(설정 API로 저장).
// 포트는 루프백에만 연다(R13). 데이터 폴더는 개발 모드 = 콘텐츠 루트 기준 .data/, 실행 파일 모드 = DataDir(기본 %LOCALAPPDATA%\db-ripple).
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DbRipple.Analysis;
using DbRipple.Api;
using DbRipple.Api.Middleware;
using DbRipple.Apply;
using DbRipple.Attribution;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Diff;
using DbRipple.Execution;
using DbRipple.Host;
using DbRipple.Preview;
using DbRipple.Preview.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using ContractBackup = DbRipple.Contracts.BackupSettings;

const string NoBrowserArg = "--no-browser";
var openBrowser = !args.Contains(NoBrowserArg, StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(a => !string.Equals(a, NoBrowserArg, StringComparison.OrdinalIgnoreCase)
    && !string.Equals(a, LaunchMode.DevArg, StringComparison.OrdinalIgnoreCase)).ToArray();

// 실행 모드 판정은 환경 변수에 묶지 않는다(N4 보강): wwwroot가 있으면 항상 실행 파일 모드, 개발 모드는 Debug + --dev + wwwroot 없음.
#if DEBUG
const bool debugBuild = true;
#else
const bool debugBuild = false;
#endif
var mode = LaunchMode.Resolve(AppContext.BaseDirectory, args, debugBuild);
var exeMode = mode.Executable;
if (mode.IgnoredDevReason is { } ignored)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"경고: {LaunchMode.DevArg}를 무시하고 실행 파일 모드(실행 토큰·요청 키 필수)로 엽니다. 이유: {ignored}.");
    Console.ResetColor();
}
// 실행 파일 모드는 콘텐츠 루트를 실행 파일 폴더로(바로 가기·다른 폴더에서 실행해도 wwwroot·appsettings를 찾게).
// 환경 이름도 모드로 정한다(실행 파일 모드에서 ASPNETCORE_ENVIRONMENT=Development여도 개발자 예외 페이지·5273 출처·OpenAPI가 켜지지 않게).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    ContentRootPath = exeMode ? AppContext.BaseDirectory : null,
    EnvironmentName = exeMode ? Environments.Production : Environments.Development,
});
var cfg = builder.Configuration.GetSection("DbRipple");

// 포트: 설정 주소(루프백만). 실행 파일 모드에서 이미 쓰이면 127.0.0.1의 빈 포트(0)로 열고 실제 주소를 로그·URL·허용 출처에 쓴다.
var configuredUrl = cfg["Urls"] ?? "http://127.0.0.1:5080";
var listenUri = new Uri(configuredUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]);
if (!DbRippleApi.LocalHosts.Contains(listenUri.Host, StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException($"DbRipple:Urls는 루프백(127.0.0.1, localhost, [::1])만 쓸 수 있음: {listenUri.Host}");
var portBusy = exeMode && PortInUse(listenUri);
builder.WebHost.UseUrls(portBusy ? "http://127.0.0.1:0" : listenUri.GetLeftPart(UriPartial.Authority));

string FullPath(string key, string? fallback = null) =>
    Path.GetFullPath(cfg[key] ?? fallback ?? throw new InvalidOperationException($"DbRipple:{key} 없음"), builder.Environment.ContentRootPath);

var dataDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cfg["DataDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "db-ripple")));
// 실행 파일 모드: 상대 경로·빈 값이면 DataDir 아래(절대 경로 설정은 그대로). 개발 모드: 지금처럼 콘텐츠 루트 기준.
string DataPath(string key, string devDefault, string exeRelative)
{
    var v = cfg[key];
    if (exeMode && (string.IsNullOrWhiteSpace(v) || !Path.IsPathRooted(v))) return Path.Combine(dataDir, exeRelative);
    return FullPath(key, devDefault);
}
var manifestPath = DataPath("ManifestPath", ".data/aw-baseline/manifest.json", Path.Combine("baseline", "manifest.json"));
var previewStoreDir = DataPath("PreviewStoreDir", ".data/previews", "previews");
var importDir = DataPath("Backup:ImportDir", "../../.data/import", "import");

// Q12: 실행기가 멱등으로 만드는 DML 전용 사용자. 빈 문자열은 가장 없이 실행하게 되므로 시작에서 막는다(N4).
var executeAsUser = cfg["ExecuteAsUser"] ?? PreviewUserSql.UserName;
if (string.IsNullOrWhiteSpace(executeAsUser))
    throw new InvalidOperationException("DbRipple:ExecuteAsUser가 비어 있음(기본 dbr_preview_user를 쓰려면 키를 지울 것)");

// 연결: 환경 변수가 하나라도 있으면 그것(기존 동작, 빠진 변수는 이름만 적은 오류). 없으면 프로필.
EnvSettings? env = null;
if (new[] { ServerConnections.OriginEnvVar, ServerConnections.SandboxEnvVar, ServerConnections.SingleEnvVar }
    .Any(n => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))))
{
    env = new EnvSettings(ServerConnections.Resolve(cfg["Mode"], Environment.GetEnvironmentVariable),
        cfg["OriginDb"] ?? "AW_Origin", cfg["SandboxDb"] ?? "AW_Sandbox", new ContractBackup
        {
            ServerPath = Empty(cfg["Backup:ServerPath"]),
            SharePath = Empty(cfg["Backup:SharePath"]),
            ImportFile = Empty(cfg["Backup:ImportFile"]) is not null ? FullPath("Backup:ImportFile") : null,
        });
}

builder.Services.AddDbRippleApi();
if (exeMode) builder.Services.AddLaunchSession();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITriggerAnalyzer, TriggerAnalyzer>();
builder.Services.AddSingleton<IBatchAnalyzer, BatchAnalyzer>();
builder.Services.AddSingleton<INetDiffer, NetDiffer>();
builder.Services.AddSingleton<IChangeCollector, ChangeCollector>();
builder.Services.AddSingleton<ICausalResolver, CausalResolver>();
builder.Services.AddSingleton(new ExecutionSettings { ExecuteAsUser = executeAsUser });
builder.Services.AddSingleton<IStatementRunner, StatementRunner>();
builder.Services.AddSingleton<IPreviewStore>(sp => new PreviewStore(previewStoreDir, sp.GetRequiredService<TimeProvider>()));

// 연결에 묶인 서비스 묶음(N4: 설정을 바꾸면 다시 만든다). 요청마다 현재 묶음에서 꺼낸다.
builder.Services.AddSingleton(sp => new EngineHolder(profile => CreateEngine(sp, profile), sp.GetRequiredService<ILogger<EngineHolder>>()));
builder.Services.AddSingleton<IConnectionSwitch>(sp => sp.GetRequiredService<EngineHolder>());
builder.Services.AddTransient(sp => sp.GetRequiredService<EngineHolder>().Require().Sandbox);
builder.Services.AddTransient(sp => sp.GetRequiredService<EngineHolder>().Require().Pipeline);
builder.Services.AddTransient(sp => sp.GetRequiredService<EngineHolder>().Require().Catalog);
builder.Services.AddTransient(sp => sp.GetRequiredService<EngineHolder>().Require().Apply);
builder.Services.AddSingleton(sp => new ProfileSettingsService(
    new ProfileSettingsOptions
    {
        DataDir = dataDir,
        Env = env,
        DefaultOriginDb = cfg["OriginDb"] ?? "AW_Origin",
        DefaultSandboxDb = cfg["SandboxDb"] ?? "AW_Sandbox",
    },
    new DataProtectionSecretProtector(Path.Combine(dataDir, "keys")),
    new SqlServerProbe(),
    sp.GetRequiredService<IConnectionSwitch>()));
builder.Services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<ProfileSettingsService>());

var app = builder.Build();

var engines = app.Services.GetRequiredService<EngineHolder>();
var settings = app.Services.GetRequiredService<ProfileSettingsService>();
var startProfile = env is not null
    ? new RuntimeProfile(env.Connections, env.OriginDb, env.SandboxDb, cfg["SnapshotDb"] ?? "AW_Sandbox_base", env.Backup)
    : settings.Initialize();
if (settings.LoadWarning is { } loadWarning) app.Logger.LogWarning("설정: {Warning}", loadWarning);
if (startProfile is not null) engines.Start(startProfile);
else app.Logger.LogWarning("연결 설정이 없습니다. 화면의 설정에서 원본·샌드박스 연결을 저장하세요(저장 위치 {DataDir}).", dataDir);
app.Logger.LogInformation("DbRipple {Mode} 모드, 데이터 {DataDir}, 매니페스트 {Manifest}, 미리보기 저장소 {Store}",
    exeMode ? "실행 파일" : "개발", dataDir, manifestPath, previewStoreDir);

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!exeMode) app.MapOpenApi();
// web 빌드(wwwroot) 같은 출처 서빙. 문서·API는 실행 토큰 쿠키가 있어야 여기까지 온다(LaunchSession, 실행 파일 모드).
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapDbRippleApi();
app.MapFallback("/api/{**path}", () => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "없는 API 경로입니다"));
app.MapFallbackToFile("index.html").WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
var hasWebRoot = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html"));
if (exeMode && !hasWebRoot)
    app.Logger.LogWarning("wwwroot/index.html이 없습니다. scripts/publish.ps1로 게시한 폴더에서 실행하세요.");
if (!exeMode)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(hasWebRoot
        ? $"경고: 개발 모드({LaunchMode.DevArg})인데 콘텐츠 루트에 wwwroot/index.html이 있습니다. 실행 토큰 없이 화면과 API가 열립니다. 배포용이면 게시본 폴더에서 실행하세요."
        : $"개발 모드({LaunchMode.DevArg}): 실행 토큰 없이 API가 열립니다(Vite 5273 프록시용).");
    Console.ResetColor();
}

await app.StartAsync();

// 실제 바인딩 주소 → 허용 출처(5273은 개발 모드에서만), 실행 URL, 브라우저.
var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Select(a => new Uri(a)).ToList() ?? [];
var guard = app.Services.GetRequiredService<IOptions<CrossSiteRequestGuardOptions>>().Value;
var boundOrigins = bound.SelectMany(u => new[] { $"http://127.0.0.1:{u.Port}", $"http://localhost:{u.Port}" }).Distinct().ToArray();
guard.AllowedOrigins = exeMode ? boundOrigins : [.. guard.AllowedOrigins.Concat(boundOrigins).Distinct(StringComparer.OrdinalIgnoreCase)];
var baseUrl = bound.Count > 0 ? $"http://127.0.0.1:{bound[0].Port}" : configuredUrl;
if (portBusy) app.Logger.LogWarning("{Configured} 포트가 이미 쓰이고 있어 {Actual}로 열었습니다.", listenUri.GetLeftPart(UriPartial.Authority), baseUrl);
app.Logger.LogInformation("DbRipple 주소 {Url}, 허용 출처 {Origins}", baseUrl, string.Join(", ", guard.AllowedOrigins));

if (exeMode)
{
    // 실행 URL(일회용 토큰)은 로그 공급자가 아니라 콘솔에만 찍는다.
    var session = app.Services.GetRequiredService<LaunchSession>();
    session.TokenRotated += token => Console.WriteLine($"새 브라우저 창 주소(한 번만 쓸 수 있음): {baseUrl}/?{LaunchSession.QueryName}={token}");
    var launchUrl = session.LaunchUrl(baseUrl);
    var opened = false;
    if (openBrowser)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });
            opened = true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            app.Logger.LogWarning("기본 브라우저를 열지 못했습니다({Error}).", ex.GetType().Name);
        }
    }
    Console.WriteLine(opened
        ? $"브라우저를 열었습니다. 다른 창에서 열려면 이 주소를 쓰세요(한 번만): {launchUrl}"
        : $"브라우저에서 이 주소를 여세요(한 번만 쓸 수 있음): {launchUrl}");
    Console.WriteLine("끝내려면 이 창에서 Ctrl+C를 누르세요.");
}

await app.WaitForShutdownAsync();

Engine CreateEngine(IServiceProvider sp, RuntimeProfile profile)
{
    var mode = profile.Connections.Mode;
    var clock = sp.GetRequiredService<TimeProvider>();
    var sandbox = new SandboxManager(
        new SandboxManagerOptions
        {
            Connections = profile.Connections,
            OriginDb = profile.OriginDb,
            SandboxDb = profile.SandboxDb,
            SnapshotDb = profile.SnapshotDb,
            BackupPath = cfg["BackupPath"] ?? throw new InvalidOperationException("DbRipple:BackupPath 없음"),
            ManifestPath = manifestPath,
            Backup = mode == ServerMode.Split ? SplitBackup(profile.Backup) : null,
            AllowRiskyDdlTriggers = cfg.GetValue("AllowRiskyDdlTriggers", false),
        },
        sp.GetRequiredService<ITriggerAnalyzer>(), clock);
    var executor = new PreviewExecutor(profile.Connections.Sandbox.ConnectionString, sp.GetRequiredService<ExecutionSettings>(), sp.GetRequiredService<INetDiffer>());
    var pipeline = ActivatorUtilities.CreateInstance<PreviewPipeline>(sp, sandbox, (IPreviewExecutor)executor);
    // 3주차: /catalog/schema(AD8), [적용](C15, AD6·AD7·AD10).
    var catalog = new CatalogSchemaProvider(sandbox, profile.Connections.Sandbox.ConnectionString);
    var d = new ApplySettings();
    var applySettings = new ApplySettings
    {
        // SingleServer [적용]은 같은 사용자로 원본에서 실행한다(E19). Split은 가장하지 않고 본인 로그인(실행 주체 불일치는 결과 limitations).
        ExecuteAsUser = mode == ServerMode.Split ? null : executeAsUser,
        Mode = mode,
        OriginLockTimeoutMs = cfg.GetValue("OriginLockTimeoutMs", d.OriginLockTimeoutMs),
        CommandTimeoutSeconds = cfg.GetValue("ApplyCommandTimeoutSeconds", d.CommandTimeoutSeconds),
        ToolCommandTimeoutSeconds = cfg.GetValue("ApplyToolTimeoutSeconds", d.ToolCommandTimeoutSeconds),
        OriginSnapshotSuffix = cfg["OriginSnapshotSuffix"] ?? d.OriginSnapshotSuffix,
        ForceKeyset = cfg.GetValue("ForceKeyset", d.ForceKeyset), // 테스트·데모 전용(§5.3 KEYSET 경로 확인)
    };
    var apply = new ApplyService(profile.Connections.Origin, applySettings, sp.GetRequiredService<IPreviewStore>(), sandbox,
        sp.GetRequiredService<IBatchAnalyzer>(), sp.GetRequiredService<IStatementRunner>(), sp.GetRequiredService<INetDiffer>(), clock);
    return new Engine(profile, sandbox, pipeline, catalog, apply);
}

SplitBackupSettings SplitBackup(ContractBackup backup)
{
    var b = cfg.GetSection("Backup");
    var d = new SplitBackupSettings { ImportDir = "." };
    return new SplitBackupSettings
    {
        ServerPath = backup.ServerPath,
        SharePath = backup.SharePath,
        ImportFile = backup.ImportFile,
        ImportDir = importDir,
        ImportContainerDir = Empty(b["ImportContainerDir"]) ?? d.ImportContainerDir,
        DeleteAfterRestore = b.GetValue("DeleteAfterRestore", d.DeleteAfterRestore),
    };
}

static bool PortInUse(Uri uri)
{
    var address = uri.Host == "[::1]" ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    try
    {
        var listener = new TcpListener(address, uri.Port);
        listener.Start();
        listener.Stop();
        return false;
    }
    catch (SocketException)
    {
        return true;
    }
}

static string? Empty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
