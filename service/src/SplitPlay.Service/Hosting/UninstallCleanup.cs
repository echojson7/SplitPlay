// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Options;
using SplitPlay.Core.Configuration;
using SplitPlay.Service.Configuration;
using SplitPlay.Service.Platform;
using SplitPlay.Service.Supervision;

namespace SplitPlay.Service.Hosting;

/// <summary>
/// What the uninstaller undoes per desk, after the service is gone (<c>SplitPlay.Service.exe cleanup</c>): each desk's
/// Steam isolation values, its sign-in screen entry and its firewall rules; with <c>deleteAccounts</c> also the Windows
/// accounts SplitPlay created, their profiles and stored passwords. Accounts SplitPlay did not create are never deleted.
/// Stored passwords otherwise stay with the data folder (a reinstall over kept data needs them). Every step is best
/// effort and reported.
/// </summary>
public static class UninstallCleanup
{
    /// <summary>How long to wait for the sessions of accounts about to be deleted to log off.</summary>
    public static TimeSpan LogoffWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>What was done and what failed, one line each.</summary>
    public sealed record Report(IReadOnlyList<string> Done, IReadOnlyList<string> Failed);

    public static Report Run(IDeskPlatform platform, DataPaths paths, SplitPlayConfig config, bool deleteAccounts, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        var done = new List<string>();
        var failed = new List<string>();

        void Try(string what, Action action)
        {
            try
            {
                action();
                done.Add(what);
                logger.LogInformation("{What}", what);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add($"{what}: {ex.Message}");
                logger.LogWarning("{What} failed: {Message}", what, ex.Message);
            }
        }

        // Desks in config.json, and desk folders left by desks that are no longer there.
        var desks = config.Desks.ToDictionary(d => d.Id, StringComparer.Ordinal);
        if (Directory.Exists(paths.DesksDirectory))
        {
            foreach (var dir in Directory.EnumerateDirectories(paths.DesksDirectory))
            {
                var id = Path.GetFileName(dir);
                desks.TryAdd(id, new DeskConfig { Id = id });
            }
        }

        bool Ours(DeskConfig desk) =>
            desk.User is { } user && desk.UserManaged != false && platform.FindAccount(user)?.CreatedBySplitPlay == true;

        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (deleteAccounts)
        {
            foreach (var user in platform.ListUsers().Where(u => u.CreatedBySplitPlay).Select(u => u.Name))
            {
                accounts.Add(user);
            }

            foreach (var desk in desks.Values.Where(Ours))
            {
                accounts.Add(desk.User!);
            }

            // An account a desk uses as the owner's own (userManaged=false) is never logged off or deleted, whatever its
            // comment says.
            foreach (var desk in desks.Values.Where(d => d.UserManaged == false && d.User is not null))
            {
                accounts.Remove(desk.User!);
            }

            LogOff(platform, accounts, logger);
        }

        foreach (var desk in desks.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            Try($"desk {desk.Id}: Steam settings restored", () => platform.RevertSteamIsolation(desk.Id));
            Try($"desk {desk.Id}: sign-in screen entry restored", () => platform.ShowOnLogon(desk.Id));
            Try($"desk {desk.Id}: firewall rules removed", () => platform.RemoveFirewallRules(desk.Id));
            if (deleteAccounts && Ours(desk))
            {
                Try($"desk {desk.Id}: account {desk.User} deleted", () => platform.DeleteDesk(desk, deleteUser: true));
                accounts.Remove(desk.User!);
            }
        }

        // SplitPlay-created accounts no desk uses any more (a desk deleted without its account).
        foreach (var user in accounts.Order(StringComparer.OrdinalIgnoreCase))
        {
            Try($"account {user} deleted", () => platform.DeleteManagedAccount(user));
        }

        return new Report(done, failed);
    }

    private static void LogOff(IDeskPlatform platform, HashSet<string> users, ILogger logger)
    {
        if (users.Count == 0)
        {
            return;
        }

        IReadOnlyList<HostSession> Remaining()
        {
            try
            {
                return [.. platform.Sessions().Where(s => s.User is not null && users.Contains(s.User))];
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning("Could not list the sessions: {Message}", ex.Message);
                return [];
            }
        }

        foreach (var session in Remaining())
        {
            try
            {
                platform.LogoffSession(session.Id);
                logger.LogInformation("Logging off session {Session} of {User}", session.Id, session.User);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning("Could not log off session {Session} of {User}: {Message}", session.Id, session.User, ex.Message);
            }
        }

        // A loaded profile cannot be deleted: wait for the logoffs.
        var deadline = DateTime.UtcNow + LogoffWait;
        while (Remaining().Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// <c>SplitPlay.Service.exe cleanup [--data-root DIR] [--delete-accounts]</c>, run elevated by the uninstaller.
    /// Exit code 0 when every step worked, 1 when some failed (they are printed), 64 for bad arguments.
    /// </summary>
    public static int Command(IReadOnlyList<string> args)
    {
        var root = ServiceHostOptions.DefaultDataRoot();
        var deleteAccounts = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--delete-accounts":
                    deleteAccounts = true;
                    break;
                case "--data-root" when i + 1 < args.Count:
                    root = Path.GetFullPath(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"error: unknown argument '{args[i]}' (cleanup [--data-root DIR] [--delete-accounts])");
                    return 64;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("error: cleanup needs Windows");
            return 64;
        }

        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = loggers.CreateLogger("SplitPlay.Cleanup");
        var paths = new DataPaths(root);
        ConfigLoadResult loaded;
        try
        {
            loaded = File.Exists(paths.ConfigFile)
                ? ConfigStore.Parse(File.ReadAllText(paths.ConfigFile))
                : ConfigStore.Parse("""{"schemaVersion":1}""");
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not read {File} ({Message}); cleaning up by the desk folders only", paths.ConfigFile, ex.Message);
            loaded = ConfigStore.Parse("""{"schemaVersion":1}""");
        }

        var platform = new WindowsDeskPlatform(paths, new ConfigService(paths, loaded), Options.Create(new SupervisorOptions()),
            Options.Create(new FirewallOptions()), loggers.CreateLogger<WindowsDeskPlatform>());
        var report = Run(platform, paths, loaded.Config, deleteAccounts, logger);
        Console.WriteLine($"cleanup: {report.Done.Count} done, {report.Failed.Count} failed");
        foreach (var line in report.Failed)
        {
            Console.WriteLine($"failed: {line}");
        }

        return report.Failed.Count == 0 ? 0 : 1;
    }
}
