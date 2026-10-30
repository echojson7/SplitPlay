// SPDX-License-Identifier: MIT
namespace SplitPlay.Tray;

/// <summary>Owns the notification-area icon, its menu and the dashboard window.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly Uri _url;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Control _invoker;
    private DashboardForm? _dashboard;

    public TrayContext(Uri url, bool visible = true)
    {
        _url = url;
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SplitPlay",
            ContextMenuStrip = _menu,
            Visible = visible,
        };
        _icon.DoubleClick += (_, _) => ShowDashboard();

        // A window handle on the UI thread so that other threads (the "show" signal) can post to it.
        _invoker = new Control();
        _invoker.CreateControl();

        if (visible)
        {
            // Sign-in (the HKLM Run entry) or a start by hand: a stopped service is started right away, without a
            // prompt. Without the permission to start it, the dashboard asks for elevation when it opens.
            _ = Task.Run(ServiceStarter.TryStart);
        }
    }

    /// <summary>Shows and activates the dashboard; safe from any thread.</summary>
    public void ShowDashboardFromAnyThread()
    {
        if (_invoker.IsDisposed)
        {
            return;
        }

        if (_invoker.InvokeRequired)
        {
            _invoker.BeginInvoke(ShowDashboard);
        }
        else
        {
            ShowDashboard();
        }
    }

    public int MenuItemCount => _menu.Items.Count;

    private void ShowDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(_url);
        }

        _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized)
        {
            _dashboard.WindowState = FormWindowState.Normal;
        }

        _dashboard.Activate();
        _dashboard.BringToFront();
    }

    private void Quit()
    {
        _icon.Visible = false;
        _dashboard?.CloseForReal();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _invoker.Dispose();
            _dashboard?.Dispose();
        }

        base.Dispose(disposing);
    }
}
