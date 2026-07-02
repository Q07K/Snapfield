using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using Snapfield.Core.Persistence;

namespace Snapfield.App;

/// <summary>
/// Owns the notification-area icon and its menu. WinForms NotifyIcon is used
/// because WPF has no native tray support.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Snapfield";

    private readonly App _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _restoreItem;
    private bool _hideHintShown;

    public TrayManager(App app)
    {
        _app = app;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("보정 창 열기", null, (_, _) => _app.ShowMain()) { Font = new Font(SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold) };
        var network = new ToolStripMenuItem("네트워크 창 열기", null, (_, _) => _app.ShowNetwork());
        _autoStartItem = new ToolStripMenuItem("로그인 시 자동 실행") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
        _autoStartItem.CheckedChanged += (_, _) => SetAutoStart(_autoStartItem.Checked);
        _restoreItem = new ToolStripMenuItem("실행 시 마지막 연결 복원") { CheckOnClick = true, Checked = SettingsStore.Load().RestoreOnLaunch };
        _restoreItem.CheckedChanged += (_, _) =>
            SettingsStore.Save(SettingsStore.Load() with { RestoreOnLaunch = _restoreItem.Checked });
        var exit = new ToolStripMenuItem("종료", null, (_, _) => _app.ExitFromTray());

        menu.Items.Add(open);
        menu.Items.Add(network);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(_restoreItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Snapfield",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _app.ShowMain();
    }

    /// <summary>Balloon shown the first time a window hides to the tray.</summary>
    public void ShowHideHint()
    {
        if (_hideHintShown) return;
        _hideHintShown = true;
        _icon.BalloonTipTitle = "Snapfield";
        _icon.BalloonTipText = "백그라운드에서 계속 실행 중입니다. 종료는 트레이 아이콘 우클릭 → 종료.";
        _icon.ShowBalloonTip(3000);
    }

    // ── Auto-start (HKCU Run key) ────────────────────────────────────────────

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enable && Environment.ProcessPath is string exe)
                key.SetValue(RunValueName, $"\"{exe}\"");
            else if (!enable)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    // ── Icon: two little monitors on the shared plane ────────────────────────

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var blue = new SolidBrush(Color.FromArgb(0x4A, 0x7B, 0xE0));
        using var orange = new SolidBrush(Color.FromArgb(0xE0, 0xA4, 0x4A));
        g.FillRectangle(blue, 2, 7, 17, 13);
        g.FillRectangle(orange, 21, 11, 9, 9);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
