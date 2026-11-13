using System.Net;
using System.Text;
using System.Text.Json;
using DbRipple.Contracts;
using DbRipple.Core.Baseline;
using DbRipple.Preview.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbRipple.Api.Tests;

/// <summary>
/// 설정(프로필) API(N4 계약 4절): GET에 비밀값 없음, PUT 검증 400(필드별 점 표기), Split 샌드박스 비루프백 400, 환경 변수 우선 409 ENV_OVERRIDE,
/// 저장 위치·스키마 버전·비밀번호 분리 저장(보호기 통과), 기억 안 함은 메모리만, 서버·사용자가 바뀌면 비밀번호 재사용 안 함,
/// 연결 문자열 조립(Encrypt, 원본 TrustServerCertificate 기본 false), 연결 교체, 바쁠 때 409, 시험 엔드포인트(저장 안 함, 경고).
/// </summary>
public sealed class SettingsApiTests : IAsyncLifetime
{
    const string Secret = "S3cret!pw;x=1";
    readonly string dir = Path.Combine(Path.GetTempPath(), "dbrt_api_settings_" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeSwitch target = new();
    readonly Probe probe = new();
    WebApplication app = null!;
    HttpClient client = null!;
    EnvSettings? env;

    sealed class Protector : ISecretProtector
    {
        // 시험용: 뒤집고 표지를 붙인다(평문이 파일에 그대로 남지 않는지만 본다).
        public byte[] Protect(byte[] data) => [0xD9, .. Enumerable.Reverse(data)];
        public byte[] Unprotect(byte[] data) => data[0] == 0xD9 ? [.. Enumerable.Reverse(data.Skip(1))] : throw new InvalidDataException();
    }

    sealed class FakeSwitch : IConnectionSwitch
    {
        public readonly List<RuntimeProfile> Switched = [];
        public bool Busy;
        public void EnsureIdle() { if (Busy) throw new PreviewConflictException(ConflictReason.Locked, "바쁨"); }
        public void Switch(RuntimeProfile profile) { EnsureIdle(); Switched.Add(profile); }
    }

    sealed class Probe : ISqlServerProbe
    {
        public readonly List<string> Seen = [];
        public bool Sysadmin;
        public Task<OriginTestResult> ProbeOriginAsync(string connectionString, string originDb, CancellationToken ct)
        {
            Seen.Add(connectionString);
            return Task.FromResult(new OriginTestResult
            {
                Ok = true, ServerName = "DEVSQL01", ProductVersion = "16.0.4175.1", Edition = "Developer",
                IsSysadmin = Sysadmin, CanBackup = false, CanCreateDatabase = false,
            });
        }
        public Task<SandboxTestResult> ProbeSandboxAsync(string connectionString, bool isLoopback, CancellationToken ct)
        {
            Seen.Add(connectionString);
            return Task.FromResult(new SandboxTestResult { Ok = true, ServerName = "dbripple-sql", ProductVersion = "17.0", IsLoopback = isLoopback });
        }
    }

    ProfileSettingsService NewService() => new(new ProfileSettingsOptions { DataDir = dir, Env = env }, new Protector(), probe, target);

    public async Task InitializeAsync() => await StartAsync();

    async Task StartAsync()
    {
        if (app is not null) await app.DisposeAsync();
        var service = NewService();
        service.Initialize();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbRippleApi();
        builder.Services.AddSingleton<ISettingsService>(service);
        app = builder.Build();
        app.UseExceptionHandler();
        app.MapDbRippleApi();
        await app.StartAsync();
        client = app.GetTestClient().WithGuardHeader();
    }

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static object Body(string mode = "split", string originServer = "devsql01", string originAuth = "sql", string? originUser = "dev1",
        string? originPassword = Secret, bool remember = true, string sandboxServer = "127.0.0.1,14333", string sandboxDb = "AW_Sandbox",
        string? sandboxPassword = "Sb!pass", bool trust = false, string originDb = "AW_Origin") => new
    {
        mode,
        origin = new { server = originServer, database = originDb, auth = originAuth, user = originUser, password = originPassword, rememberPassword = remember, trustServerCertificate = trust },
        sandbox = new { server = sandboxServer, database = sandboxDb, auth = "sql", user = "sa", password = sandboxPassword, rememberPassword = remember },
        backup = new { serverPath = (string?)null, sharePath = (string?)null, importFile = (string?)null },
    };

    static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    Task<HttpResponseMessage> PutAsync(object body) => client.PutAsync("/api/settings", Json(body));

    async Task<JsonElement> GetSettingsAsync()
    {
        var res = await client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("Sb!pass", text);
        Assert.DoesNotContain("\"password\"", text, StringComparison.OrdinalIgnoreCase); // 비밀번호 필드 자체가 없다(hasStoredPassword만)
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }

    [Fact]
    public async Task Get_without_profile_is_none_with_defaults_and_no_secrets()
    {
        var s = await GetSettingsAsync();
        Assert.Equal("none", s.GetProperty("source").GetString());
        Assert.False(s.GetProperty("configured").GetBoolean());
        Assert.Equal("split", s.GetProperty("mode").GetString());
        Assert.Equal("127.0.0.1,14333", s.GetProperty("sandbox").GetProperty("server").GetString());
        Assert.False(s.GetProperty("origin").GetProperty("hasStoredPassword").GetBoolean());
        Assert.False(s.GetProperty("origin").GetProperty("trustServerCertificate").GetBoolean());
    }

    [Fact]
    public async Task Put_saves_profile_without_secrets_switches_connections_and_get_shows_no_password()
    {
        var res = await PutAsync(Body());
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var profileText = await File.ReadAllTextAsync(Path.Combine(dir, ProfileSettingsService.ProfileFileName));
        Assert.Contains("\"schemaVersion\": 1", profileText);
        Assert.DoesNotContain(Secret, profileText);
        var secretBytes = await File.ReadAllBytesAsync(Path.Combine(dir, ProfileSettingsService.SecretFileName));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(secretBytes)); // 보호기를 거친다

        var runtime = Assert.Single(target.Switched);
        Assert.Equal(ServerMode.Split, runtime.Connections.Mode);
        Assert.Equal("AW_Sandbox_base", runtime.SnapshotDb);
        var origin = new SqlConnectionStringBuilder(runtime.Connections.Origin.ConnectionString);
        Assert.Equal("devsql01", origin.DataSource);
        Assert.Equal(Secret, origin.Password);                       // ; = 가 있어도 빌더가 인용한다
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, origin.Encrypt);
        Assert.False(origin.TrustServerCertificate);                  // 원본 기본 false
        Assert.True(new SqlConnectionStringBuilder(runtime.Connections.Sandbox.ConnectionString).TrustServerCertificate); // 루프백 샌드박스
        Assert.DoesNotContain(Secret, runtime.ToString());

        var s = await GetSettingsAsync();
        Assert.Equal("profile", s.GetProperty("source").GetString());
        Assert.True(s.GetProperty("configured").GetBoolean());
        Assert.True(s.GetProperty("origin").GetProperty("hasStoredPassword").GetBoolean());
        Assert.Equal("dev1", s.GetProperty("origin").GetProperty("user").GetString());

        // 다시 시작해도 기억한 비밀번호로 연결을 만든다
        var restarted = NewService();
        Assert.NotNull(restarted.Initialize());
    }

    [Fact]
    public async Task Not_remembered_password_stays_in_memory_only()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body(remember: false))).StatusCode);
        Assert.False(File.Exists(Path.Combine(dir, ProfileSettingsService.SecretFileName)));
        Assert.True((await GetSettingsAsync()).GetProperty("configured").GetBoolean());

        var restarted = NewService();
        Assert.Null(restarted.Initialize());                          // 다시 입력해야 한다
        Assert.False((await restarted.GetAsync(CancellationToken.None)).Configured);
    }

    [Fact]
    public async Task Password_is_reused_only_for_the_same_server_and_user()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body(originPassword: null, sandboxPassword: null))).StatusCode);
        Assert.Equal(Secret, new SqlConnectionStringBuilder(target.Switched[^1].Connections.Origin.ConnectionString).Password);

        var errors = await ErrorsAsync(await PutAsync(Body(originServer: "other-sql", originPassword: null)));
        Assert.Contains("origin.password", errors.Keys);              // 다른 서버로 기존 비밀번호를 보내지 않는다
        errors = await ErrorsAsync(await PutAsync(Body(originUser: "dev2", originPassword: null)));
        Assert.Contains("origin.password", errors.Keys);
    }

    [Fact]
    public async Task Put_validation_is_400_with_dotted_field_errors_and_nothing_is_saved()
    {
        var errors = await ErrorsAsync(await PutAsync(Body(originServer: "", originDb: "bad]name", originUser: null, originPassword: null)));
        Assert.Contains("origin.server", errors.Keys);
        Assert.Contains("origin.database", errors.Keys);
        Assert.Contains("origin.user", errors.Keys);
        Assert.Empty(target.Switched);
        Assert.False(File.Exists(Path.Combine(dir, ProfileSettingsService.ProfileFileName)));

        errors = await ErrorsAsync(await PutAsync(Body(originServer: "x;Password=y")));
        Assert.Contains("origin.server", errors.Keys);                // 세미콜론으로 연결 문자열을 조작하지 못한다
    }

    [Theory]
    [InlineData("devsql02,1433")]
    [InlineData("10.0.0.5")]
    [InlineData("tcp:devsql02")]
    public async Task Split_with_non_loopback_sandbox_is_400(string server)
    {
        var errors = await ErrorsAsync(await PutAsync(Body(sandboxServer: server)));
        Assert.Equal([ProfileSettingsService.SandboxNotLoopback], errors["sandbox.server"]);
        Assert.Empty(target.Switched);
    }

    [Theory]
    [InlineData("127.0.0.1,14333")]
    [InlineData("localhost")]
    [InlineData("(local)")]
    [InlineData(".")]
    [InlineData("tcp:127.0.0.1,14333")]
    public async Task Split_with_loopback_sandbox_is_accepted(string server) =>
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body(sandboxServer: server))).StatusCode);

    [Fact]
    public async Task Single_server_uses_sandbox_connection_and_rejects_same_database()
    {
        var errors = await ErrorsAsync(await PutAsync(Body(mode: "single_server", sandboxDb: "AW_Origin")));
        Assert.Contains("sandbox.database", errors.Keys);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body(mode: "single_server", originServer: "", originUser: null, originPassword: null))).StatusCode);
        var runtime = Assert.Single(target.Switched);
        Assert.Equal(ServerMode.SingleServer, runtime.Connections.Mode);
        Assert.Equal(runtime.Connections.Origin.ConnectionString, runtime.Connections.Sandbox.ConnectionString);
        Assert.Equal("AW_Origin", runtime.OriginDb);
    }

    [Fact]
    public async Task Trust_server_certificate_applies_to_origin_only_when_explicit()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(Body(trust: true))).StatusCode);
        Assert.True(new SqlConnectionStringBuilder(target.Switched[^1].Connections.Origin.ConnectionString).TrustServerCertificate);
        Assert.True((await GetSettingsAsync()).GetProperty("origin").GetProperty("trustServerCertificate").GetBoolean());
    }

    [Fact]
    public async Task Env_source_is_read_only_409_env_override_and_get_has_no_secret()
    {
        env = new EnvSettings(
            ServerConnections.Split("Data Source=devsql01;User ID=dev1;Password=" + "ENVSECRET" + ";Encrypt=True",
                "Data Source=127.0.0.1,14333;User ID=sa;Password=ENVSECRET2;TrustServerCertificate=True"),
            "AW_Origin", "AW_Sandbox", new BackupSettings());
        await StartAsync();

        var res = await PutAsync(Body());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using (var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()))
        {
            Assert.Equal("ENV_OVERRIDE", doc.RootElement.GetProperty("reason").GetString());
            Assert.Equal("env", doc.RootElement.GetProperty("source").GetString());
        }
        Assert.Empty(target.Switched);

        var text = await client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("ENVSECRET", text);
        using var s = JsonDocument.Parse(text);
        Assert.Equal("env", s.RootElement.GetProperty("source").GetString());
        Assert.Equal("devsql01", s.RootElement.GetProperty("origin").GetProperty("server").GetString());
        Assert.True(s.RootElement.GetProperty("origin").GetProperty("hasStoredPassword").GetBoolean());
    }

    [Fact]
    public async Task Put_while_busy_is_409_locked()
    {
        target.Busy = true;
        var res = await PutAsync(Body());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("LOCKED", doc.RootElement.GetProperty("reason").GetString());
        Assert.False(File.Exists(Path.Combine(dir, ProfileSettingsService.ProfileFileName)));
    }

    [Fact]
    public async Task Test_endpoint_probes_without_saving_and_warns_on_sysadmin()
    {
        probe.Sysadmin = true;
        var res = await client.PostAsync("/api/settings/test", Json(Body()));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, text);
        using var doc = JsonDocument.Parse(text);
        var origin = doc.RootElement.GetProperty("origin");
        Assert.True(origin.GetProperty("ok").GetBoolean());
        Assert.True(origin.GetProperty("isSysadmin").GetBoolean());
        Assert.False(origin.GetProperty("canBackup").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("sandbox").GetProperty("isLoopback").GetBoolean());
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(warnings, w => w!.Contains("sysadmin"));
        Assert.Contains(warnings, w => w!.Contains("BACKUP"));
        Assert.Equal(2, probe.Seen.Count);
        Assert.Empty(target.Switched);
        Assert.False(File.Exists(Path.Combine(dir, ProfileSettingsService.ProfileFileName)));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/settings/test", Json(Body(sandboxServer: "devsql02")))).StatusCode);
    }

    [Fact]
    public async Task Put_and_test_need_the_guard_header()
    {
        var raw = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await raw.PutAsync("/api/settings", Json(Body()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await raw.PostAsync("/api/settings/test", Json(Body()))).StatusCode);
        Assert.Empty(target.Switched);
    }

    [Fact]
    public void Probe_error_text_has_number_only()
    {
        Assert.Equal("연결 실패(TimeoutException)", SqlServerProbe.Describe(new TimeoutException("Password=SECRET")));
    }
}
