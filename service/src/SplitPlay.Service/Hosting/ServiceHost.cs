// SPDX-License-Identifier: MIT
using System.Net;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SplitPlay.Core;
using SplitPlay.Core.Configuration;
using SplitPlay.Service.Api;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Logging;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Hosting;

/// <summary>Builds the service's web application (also used in-process by the integration tests).</summary>
public static class ServiceHost
{
    /// <summary>
    /// Builds the host. Loads and validates <c>config.json</c> first (a file newer than this build or an invalid one
    /// throws <see cref="ConfigException"/>). <paramref name="configure"/> runs after the default registrations.
    /// </summary>
    public static WebApplication Build(ServiceHostOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var paths = new DataPaths(options.DataRoot);
        paths.EnsureCreated();
        var loaded = ConfigStore.LoadOrCreate(paths.ConfigFile);
        var host = loaded.Config.Host;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [.. options.HostArgs],
            ApplicationName = typeof(ServiceHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = options.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        if (!options.Console)
        {
            // No-op unless the process was started by the Service Control Manager.
            builder.Services.AddWindowsService(o => o.ServiceName = BuildInfo.ServiceName);
        }

        // ---- logging
        var logStore = new LogStore(paths.LogsDirectory);
        builder.Logging.SetMinimumLevel(MapLevel(host.LogLevel));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
            o.ColorBehavior = LoggerColorBehavior.Disabled;
        });
        builder.Logging.AddProvider(new LogStoreLoggerProvider(logStore));

        // ---- web server: loopback only (LAN access over HTTPS arrives later)
        var port = options.WebPort ?? host.WebPort;
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        // ---- services
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(logStore);
        builder.Services.AddSingleton(sp => new ConfigService(sp.GetRequiredService<DataPaths>(), loaded));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<SupervisorOptions>(builder.Configuration.GetSection(SupervisorOptions.Section));
        builder.Services.AddSingleton<FakeChildLauncher>();
        builder.Services.AddSingleton<ProcessChildLauncher>();
        builder.Services.AddSingleton<IChildLauncher>(sp =>
            sp.GetRequiredService<IOptions<SupervisorOptions>>().Value.EffectiveLauncher switch
            {
                "fake" => sp.GetRequiredService<FakeChildLauncher>(),
                "process" => sp.GetRequiredService<ProcessChildLauncher>(),
                var other => throw new InvalidOperationException($"unknown Supervisor:Launcher '{other}' (expected process or fake)"),
            });
        builder.Services.AddSingleton<FakeDeskPlatform>();
        builder.Services.AddSingleton<IDeskPlatform>(sp =>
            sp.GetRequiredService<IOptions<SupervisorOptions>>().Value.EffectivePlatform switch
            {
                "fake" => sp.GetRequiredService<FakeDeskPlatform>(),
                "windows" when OperatingSystem.IsWindows() => ActivatorUtilities.CreateInstance<WindowsDeskPlatform>(sp),
                var other => throw new InvalidOperationException($"unsupported Supervisor:Platform '{other}' (expected windows or fake)"),
            });
        builder.Services.AddSingleton<FakeServiceControl>();
        builder.Services.AddSingleton<IServiceControl>(sp =>
            sp.GetRequiredService<IOptions<SupervisorOptions>>().Value.EffectivePlatform switch
            {
                "windows" when OperatingSystem.IsWindows() => ActivatorUtilities.CreateInstance<WindowsServiceControl>(sp),
                _ => sp.GetRequiredService<FakeServiceControl>(),
            });
        builder.Services.AddSingleton<RelatedServices>();
        // Before the supervisor so that TermService is on its way up when autostart desks begin.
        builder.Services.AddSingleton<RequiredServicesStarter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RequiredServicesStarter>());
        builder.Services.AddSingleton<DeskSupervisor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DeskSupervisor>());
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<GpuInventory>();
        builder.Services.Configure<FirewallOptions>(builder.Configuration.GetSection(FirewallOptions.Section));
        builder.Services.AddSingleton<IFirewallRule, NetshFirewallRule>();

        configure?.Invoke(builder);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SplitPlay.Service");
        var supervisor = app.Services.GetRequiredService<IOptions<SupervisorOptions>>().Value;
        logger.LogInformation(
            "SplitPlay service {Version} starting; data root {Root}; web port {Port}; launcher {Launcher}; platform {Platform}; seat {Seat}; worker {Worker}",
            BuildInfo.Version, paths.Root, port, supervisor.EffectiveLauncher, supervisor.EffectivePlatform, supervisor.Seat, supervisor.Worker);
        if (loaded.MigratedFrom is int from)
        {
            logger.LogInformation("Migrated {File} from schema {From} to {To}", paths.ConfigFile, from, SplitPlayConfig.CurrentSchemaVersion);
        }

        if (host.LanAccess)
        {
            logger.LogWarning("host.lanAccess is set but LAN access is not implemented yet; listening on loopback only");
        }

        // Make sure the hub subscribes before the supervisor starts publishing.
        app.Services.GetRequiredService<EventHub>();

        app.UseApiErrorHandling();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapSplitPlayApi();
        // No SPA fallback: the UI uses hash routing and relative asset paths.
        return app;
    }

    private static LogLevel MapLevel(string? level) => level switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}
