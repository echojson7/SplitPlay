// SPDX-License-Identifier: MIT
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SplitPlay.Core.Api;
using SplitPlay.Core.Configuration;
using SplitPlay.Core.Json;
using SplitPlay.Core.Lifecycle;
using SplitPlay.Service.Hosting;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Tests;

/// <summary>An in-process service on a TestServer with a private data root, pipe namespace and fast timings.</summary>
internal sealed class TestService : IAsyncDisposable
{
    private readonly bool _ownsDataRoot;

    private TestService(WebApplication app, string dataRoot, bool ownsDataRoot)
    {
        App = app;
        DataRoot = dataRoot;
        _ownsDataRoot = ownsDataRoot;
        Launcher = app.Services.GetRequiredService<FakeChildLauncher>();
        Platform = app.Services.GetRequiredService<Platform.FakeDeskPlatform>();
    }

    public Platform.FakeDeskPlatform Platform { get; }

    public WebApplication App { get; }

    public string DataRoot { get; }

    /// <summary>Client for the in-process server with an admin bearer token (as the CLI gets one); available once started.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The bearer token <see cref="Client"/> sends.</summary>
    public string Token { get; private set; } = "";

    /// <summary>A client without any credential (a plain browser or another local process).</summary>
    public HttpClient AnonymousClient() => App.GetTestClient();

    public FakeChildLauncher Launcher { get; }

    public TestServer Server => (TestServer)App.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();

    public static string NewDataRoot() => Path.Combine(Path.GetTempPath(), "splitplay-svc-" + Guid.NewGuid().ToString("N")[..12]);

    public static async Task<TestService> StartAsync(IEnumerable<string>? extraArgs = null, string? webRoot = null, string? dataRoot = null,
        Action<FakeChildLauncher>? setup = null)
    {
        var ownsDataRoot = dataRoot is null;
        dataRoot ??= NewDataRoot();
        var ns = "sp" + Guid.NewGuid().ToString("N")[..8] + "\\";
        List<string> args =
        [
            "--console",
            "--data-root", dataRoot,
            $"--Supervisor:PipeNamespace={ns}",
            "--Supervisor:Launcher=fake",
            "--Supervisor:Platform=fake",
            "--Supervisor:RelogonAfterLayoutCopy=false",
            "--Supervisor:BluetoothPollMs=100",
            "--Supervisor:HeartbeatIntervalMs=100",
            // 1 s of silence: generous enough for a loaded CI runner; the hang test sets 3 itself.
            "--Supervisor:MissedHeartbeats=10",
            "--Supervisor:HelloTimeoutMs=5000",
            "--Supervisor:ReadyTimeoutMs=5000",
            "--Supervisor:ShutdownTimeoutMs=2000",
            "--Supervisor:InitialBackoffMs=50",
            "--Supervisor:MaxBackoffMs=200",
            "--Supervisor:MaxFailuresInWindow=3",
            "--Logging:LogLevel:Default=Debug",
            "--Firewall:Enabled=false",
        ];
        args.AddRange(extraArgs ?? []);
        var options = ServiceHostOptions.Parse(args);
        options.WebRoot = webRoot;

        var app = ServiceHost.Build(options, b => b.WebHost.UseTestServer());
        var service = new TestService(app, dataRoot, ownsDataRoot);
        setup?.Invoke(service.Launcher);
        await app.StartAsync();
        // The start-up check of TermService/UmRdpService runs in the background; let it finish before tests change the fake.
        await app.Services.GetRequiredService<RequiredServicesStarter>().Completion;
        service.Token = app.Services.GetRequiredService<Api.AuthService>().IssueBearer(TimeSpan.FromHours(1)).Token;
        service.Client = app.GetTestClient();
        service.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", service.Token);
        return service;
    }

    public async Task<DeskDto> CreateDeskAsync(string id, string extraJson = "")
    {
        var body = $$"""{"id":"{{id}}"{{extraJson}}}""";
        var response = await Client.PostAsync("/api/v1/desks", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.DeskDto))!;
    }

    public async Task<DeskDto> GetDeskAsync(string id) =>
        (await Client.GetFromJsonAsync($"/api/v1/desks/{id}", SplitPlayJsonContext.Default.DeskDto))!;

    public async Task<DeskDto> PostDeskActionAsync(string id, string action)
    {
        var response = await Client.PostAsync($"/api/v1/desks/{id}/{action}", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(SplitPlayJsonContext.Default.DeskDto))!;
    }

    /// <summary>Polls the desk until <paramref name="done"/> holds; fails the test after the timeout.</summary>
    public async Task<DeskDto> WaitForAsync(string id, Func<DeskDto, bool> done, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        DeskDto desk;
        while (true)
        {
            desk = await GetDeskAsync(id);
            if (done(desk))
            {
                return desk;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"desk {id} did not reach {what ?? "the expected state"}; last state {desk.State}, last error {desk.LastError}");
            }

            await Task.Delay(25);
        }
    }

    public Task<DeskDto> WaitForStateAsync(string id, DeskState state, TimeSpan? timeout = null) =>
        WaitForAsync(id, d => d.State == state, timeout, state.ToString());

    public SplitPlayConfig ReadConfigFile() => ConfigStore.Parse(File.ReadAllText(Path.Combine(DataRoot, ConfigStore.FileName))).Config;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        if (_ownsDataRoot)
        {
            TryDelete(DataRoot);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
