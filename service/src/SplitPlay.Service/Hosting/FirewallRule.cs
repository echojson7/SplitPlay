// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SplitPlay.Service.Hosting;

/// <summary>Settings bound from the <c>Firewall</c> configuration section.</summary>
public sealed class FirewallOptions
{
    public const string Section = "Firewall";

    /// <summary>The rule the installer creates (disabled) for the web UI port.</summary>
    public const string WebUiRuleName = "SplitPlay Web UI";

    /// <summary>Turn rule changes off (tests, developer machines).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Enables or disables the "SplitPlay Web UI" firewall rule when <c>host.lanAccess</c> changes.</summary>
public interface IFirewallRule
{
    Task SetWebUiRuleEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows: <c>netsh advfirewall firewall set rule name="SplitPlay Web UI" new enable=yes|no</c>.
/// Elsewhere, or with <c>Firewall:Enabled=false</c>, it only logs. Failures are logged, never thrown: the
/// configuration change itself has already been saved.
/// </summary>
public sealed class NetshFirewallRule(IOptions<FirewallOptions> options, ILogger<NetshFirewallRule> logger) : IFirewallRule
{
    public async Task SetWebUiRuleEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || !OperatingSystem.IsWindows())
        {
            logger.LogInformation("Firewall rule '{Rule}' would be {State} (rule changes disabled on this host)",
                FirewallOptions.WebUiRuleName, enabled ? "enabled" : "disabled");
            return;
        }

        var startInfo = new ProcessStartInfo("netsh.exe")
        {
            Arguments = $"advfirewall firewall set rule name=\"{FirewallOptions.WebUiRuleName}\" new enable={(enabled ? "yes" : "no")}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("netsh did not start");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                logger.LogInformation("Firewall rule '{Rule}' {State}", FirewallOptions.WebUiRuleName, enabled ? "enabled" : "disabled");
            }
            else
            {
                logger.LogWarning("netsh could not change firewall rule '{Rule}' (exit {Code}): {Output}",
                    FirewallOptions.WebUiRuleName, process.ExitCode, output.Trim());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("Could not run netsh to change firewall rule '{Rule}': {Message}", FirewallOptions.WebUiRuleName, ex.Message);
        }
    }
}
