// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;

namespace SplitPlay.Tray;

/// <summary>
/// Owns the notification-area icon, its menu and the dashboard window. The menu lists the desks (start, stop) and a
/// "Moonlight is waiting for a PIN" item while a client waits; a new PIN request also shows a notification that opens
/// the dashboard, where the PIN is typed in (the web UI's PIN row).
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly Uri _url;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Control _invoker;
    private readonly DeskWatcher? _desks;
    private Font? _boldFont;

    /// <summary>The desks and PIN requests the menu was built for, and its desk items by id (updated in place while open).</summary>
    private string _menuKey = "";
    private readonly Dictionary<string, (ToolStripMenuItem Item, ToolStripMenuItem Start, ToolStripMenuItem Stop)> _deskItems = [];
    private DashboardForm? _dashboard;

    public TrayContext(Uri url, bool visible = true)
    {
        _url = url;
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SplitPlay",
            ContextMenuStrip = _menu,
            Visible = visible,
        };
        _icon.DoubleClick += (_, _) => ShowDashboard();
        _icon.BalloonTipClicked += (_, _) => ShowDashboard();

        // A window handle on the UI thread so that other threads (the "show" signal, the desk poller) can post to it.
        _invoker = new Control();
        _invoker.CreateControl();

        if (visible)
        {
            // Sign-in (the HKLM Run entry) or a start by hand: a stopped service is started right away, without a
            // prompt. Without the permission to start it, the dashboard asks for elevation when it opens.
            _ = Task.Run(ServiceStarter.TryStart);

            _desks = new DeskWatcher(url, Post);
            _desks.Changed += OnDesksChanged;
            _desks.PinRequested += OnPinRequested;
            _desks.Start();
        }
    }

    /// <summary>Shows and activates the dashboard; safe from any thread.</summary>
    public void ShowDashboardFromAnyThread() => Post(ShowDashboard);

    public int MenuItemCount => _menu.Items.Count;

    /// <summary>Runs <paramref name="action"/> on the UI thread (at once when already there).</summary>
    private void Post(Action action)
    {
        if (_invoker.IsDisposed)
        {
            return;
        }

        if (_invoker.InvokeRequired)
        {
            try
            {
                _invoker.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The handle went away while quitting.
            }
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Open, the desks (name and state, a submenu with Start and Stop), the waiting PIN requests, Quit. Rebuilt when the
    /// menu opens and when the desks change while it is open.
    /// </summary>
    private void BuildMenu()
    {
        _menu.SuspendLayout();
        try
        {
            var old = _menu.Items.Cast<ToolStripItem>().ToList();
            _menu.Items.Clear();
            foreach (var item in old)
            {
                item.Dispose();
            }

            var open = new ToolStripMenuItem(TrayText.Open, null, (_, _) => ShowDashboard());
            _boldFont ??= new Font(open.Font, FontStyle.Bold);
            open.Font = _boldFont;
            _menu.Items.Add(open);

            var desks = _desks?.Desks ?? [];
            _menuKey = MenuKey(desks);
            _deskItems.Clear();
            foreach (var desk in desks.Where(d => d.PinSince is not null))
            {
                _menu.Items.Add(new ToolStripMenuItem($"{TrayText.PinWaiting} · {desk.Name}", null, (_, _) => ShowDashboard()));
            }

            if (desks.Count > 0)
            {
                _menu.Items.Add(new ToolStripSeparator());
                foreach (var desk in desks)
                {
                    _menu.Items.Add(DeskItem(desk));
                }
            }

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(TrayText.Quit, null, (_, _) => Quit());
        }
        finally
        {
            _menu.ResumeLayout();
        }
    }

    private static string MenuKey(IReadOnlyList<TrayDesk> desks) =>
        string.Join('|', desks.Select(d => $"{d.Id}:{d.PinSince is not null}"));

    private ToolStripMenuItem DeskItem(TrayDesk desk)
    {
        var item = new ToolStripMenuItem();
        var start = new ToolStripMenuItem(TrayText.Start, null, async (_, _) => await ActAsync(desk, start: true));
        var stop = new ToolStripMenuItem(TrayText.Stop, null, async (_, _) => await ActAsync(desk, start: false));
        item.DropDownItems.Add(start);
        item.DropDownItems.Add(stop);
        _deskItems[desk.Id] = (item, start, stop);
        UpdateDeskItem(desk);
        return item;
    }

    private void UpdateDeskItem(TrayDesk desk)
    {
        if (_deskItems.TryGetValue(desk.Id, out var items))
        {
            items.Item.Text = $"{desk.Name} · {TrayText.State(desk.State)}";
            items.Start.Enabled = desk.CanStart;
            items.Stop.Enabled = desk.CanStop;
        }
    }

    private async Task ActAsync(TrayDesk desk, bool start)
    {
        if (_desks is null)
        {
            return;
        }

        var error = await _desks.ActAsync(desk.Id, start);
        if (error is not null && _icon.Visible)
        {
            _icon.ShowBalloonTip(5000, $"{desk.Name} · {TrayText.ActionFailed}", error, ToolTipIcon.Warning);
        }
    }

    private void OnDesksChanged()
    {
        var waiting = _desks?.Desks.Where(d => d.PinSince is not null).Select(d => d.Name).ToList() ?? [];
        // NotifyIcon.Text is limited to 127 characters.
        var text = waiting.Count > 0 ? $"SplitPlay · {TrayText.PinWaiting}" : "SplitPlay";
        _icon.Text = text.Length > 127 ? text[..127] : text;
        if (_menu.Visible && _desks is not null && MenuKey(_desks.Desks) == _menuKey)
        {
            // Same desks: update the open menu in place, so that a submenu under the cursor stays open. Another set
            // of desks or PIN requests shows the next time the menu opens.
            foreach (var desk in _desks.Desks)
            {
                UpdateDeskItem(desk);
            }
        }
    }

    private void OnPinRequested(IReadOnlyList<TrayDesk> desks)
    {
        // The dashboard in front already shows the PIN row.
        if (_dashboard is { IsDisposed: false, Visible: true } && Form.ActiveForm == _dashboard)
        {
            return;
        }

        _icon.ShowBalloonTip(15000, TrayText.PinWaiting, TrayText.PinPrompt(string.Join(", ", desks.Select(d => d.Name))), ToolTipIcon.Info);
    }

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
        _desks?.Dispose();
        _icon.Visible = false;
        _dashboard?.CloseForReal();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _desks?.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _boldFont?.Dispose();
            _invoker.Dispose();
            _dashboard?.Dispose();
        }

        base.Dispose(disposing);
    }
}
