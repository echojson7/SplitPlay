// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Hosting;

/// <summary>What the Service Control Manager reports about one service.</summary>
/// <param name="Name">Service name (the SCM key, e.g. <c>TermService</c>).</param>
/// <param name="DisplayName">Windows display name; null when the service does not exist.</param>
/// <param name="Status">A <see cref="HostServiceStatus"/> value.</param>
/// <param name="StartType"><c>automatic</c>, <c>automaticDelayed</c>, <c>manual</c>, <c>disabled</c>, <c>boot</c>, <c>system</c>; null when missing.</param>
/// <param name="AcceptsStop">The running service accepts SERVICE_CONTROL_STOP.</param>
/// <param name="IsDriver">A kernel or file system driver (started by Windows with its device, not by SplitPlay).</param>
public sealed record ServiceInfo(string Name, string? DisplayName, string Status, string? StartType, bool AcceptsStop, bool IsDriver)
{
    public bool Exists => Status != HostServiceStatus.Missing;

    public static ServiceInfo Missing(string name) => new(name, null, HostServiceStatus.Missing, null, false, false);
}

/// <summary>A start or stop request the SCM refused (access denied, disabled, dependency failed, …).</summary>
public sealed class ServiceControlException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Windows services as far as <see cref="RelatedServices"/> needs them. <see cref="WindowsServiceControl"/> in production,
/// <see cref="FakeServiceControl"/> in tests and on non-Windows hosts.
/// </summary>
public interface IServiceControl
{
    /// <summary>Current state; <see cref="ServiceInfo.Missing"/> when the service is not installed.</summary>
    ServiceInfo Query(string name);

    /// <summary>Asks the SCM to start the service (and what it depends on); returns without waiting for it to run.</summary>
    /// <exception cref="ServiceControlException">The SCM refused.</exception>
    void Start(string name);

    /// <summary>
    /// Stops the service, its running dependents first. May block while dependents stop. For the SplitPlay service
    /// itself this ends the current process.
    /// </summary>
    /// <exception cref="ServiceControlException">The SCM refused.</exception>
    void Stop(string name);
}

/// <summary>In-memory services: start and stop take effect at once and are recorded in <see cref="Actions"/>.</summary>
public sealed class FakeServiceControl : IServiceControl
{
    private readonly ConcurrentDictionary<string, ServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _actions = new();

    public FakeServiceControl()
    {
        Set(new ServiceInfo("SplitPlay", "SplitPlay Host Service", HostServiceStatus.Running, "automatic", true, false));
        Set(new ServiceInfo("TermService", "Remote Desktop Services", HostServiceStatus.Running, "automatic", true, false));
        Set(new ServiceInfo("UmRdpService", "Remote Desktop Services UserMode Port Redirector", HostServiceStatus.Running, "manual", true, false));
        Set(new ServiceInfo("ViGEmBus", "Virtual Gamepad Emulation Bus", HostServiceStatus.Running, "manual", false, true));
        Set(new ServiceInfo("Audiosrv", "Windows Audio", HostServiceStatus.Running, "automatic", true, false));
    }

    /// <summary>Every start and stop, in order (<c>start TermService</c>, <c>stop SplitPlay</c>).</summary>
    public IReadOnlyList<string> Actions => [.. _actions];

    /// <summary>When set, <see cref="Start"/> and <see cref="Stop"/> fail with this message.</summary>
    public string? FailWith { get; set; }

    public void Set(ServiceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        _services[info.Name] = info;
    }

    public void Remove(string name) => _services.TryRemove(name, out _);

    public ServiceInfo Query(string name) => _services.TryGetValue(name, out var info) ? info : ServiceInfo.Missing(name);

    public void Start(string name)
    {
        _actions.Enqueue($"start {name}");
        Change(name, HostServiceStatus.Running);
    }

    public void Stop(string name)
    {
        _actions.Enqueue($"stop {name}");
        Change(name, HostServiceStatus.Stopped);
    }

    private void Change(string name, string status)
    {
        if (FailWith is { } message)
        {
            throw new ServiceControlException(message);
        }

        if (!_services.TryGetValue(name, out var info))
        {
            throw new ServiceControlException($"service {name} does not exist");
        }

        _services[name] = info with { Status = status };
    }
}
