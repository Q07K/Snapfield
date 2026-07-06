using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

/// <summary>
/// Notification-area icon and menu. WinForms NotifyIcon is used because WPF has
/// no native tray support. The menu rebuilds on open: a status header, one
/// disconnect entry per connected device, the tabs, and quit.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _hintShown;

    public TrayManager(App app)
    {
        _app = app;

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu(); // live state every time it opens
        RebuildMenu();

        _icon = new NotifyIcon
        {
            Text = "Snapfield",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _app.ShowMain(MainTab.Connect);

        // The icon itself shows the connection state: a green LED when connected,
        // orange while driving a remote, plain when idle. Piggybacks on the pill.
        _app.Network.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NetworkViewModel.PillKind) or nameof(NetworkViewModel.PillText))
                UpdateIcon();
        };
        UpdateIcon();
    }

    private IntPtr _iconHandle;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void UpdateIcon()
    {
        var net = _app.Network;
        Color? led = net.PillKind switch
        {
            "Ok" => Color.FromArgb(0x5F, 0xCF, 0x8A),
            "Live" => Color.FromArgb(0xEB, 0xA9, 0x56),
            _ => null,
        };

        using var bmp = Snapfield.Platform.IconArt.Render(32);
        if (led is { } c)
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new SolidBrush(Color.FromArgb(0x0A, 0x0A, 0x11)); // punch-out so the LED reads anywhere
            g.FillEllipse(ring, 32 - 15, 32 - 15, 15, 15);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, 32 - 13, 32 - 13, 11, 11);
        }

        var handle = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        var tip = $"Snapfield — {net.PillText}";
        _icon.Text = tip.Length <= 63 ? tip : tip[..63]; // NotifyIcon caps tooltip length
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    /// <summary>The tray menu runs on the WPF UI thread, so reading the view
    /// models directly is safe.</summary>
    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var net = _app.Network;

        _menu.Items.Add(new ToolStripMenuItem(net.PillText) { Enabled = false });

        if (net.ConnectedPeerViews.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var p in net.ConnectedPeerViews.ToList())
            {
                var id = p.Id;
                _menu.Items.Add(new ToolStripMenuItem($"{p.Name} 끊기", null, (_, _) => net.DisconnectPeer(id)));
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("연결", null, (_, _) => _app.ShowMain(MainTab.Connect))
        { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripMenuItem("모니터 배치", null, (_, _) => _app.ShowMain(MainTab.Calibrate)));
        _menu.Items.Add(new ToolStripMenuItem("설정", null, (_, _) => _app.ShowMain(MainTab.Settings)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => _app.ExitFromTray()));
    }

    public void ShowHideHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.BalloonTipTitle = "Snapfield";
        _icon.BalloonTipText = "백그라운드에서 계속 실행 중입니다. 종료는 트레이 아이콘 우클릭 → 종료.";
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_iconHandle != IntPtr.Zero) { DestroyIcon(_iconHandle); _iconHandle = IntPtr.Zero; }
    }
}
