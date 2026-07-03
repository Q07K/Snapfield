using System.Drawing;
using System.Windows.Forms;
using Snapfield.App.ViewModels;

namespace Snapfield.App;

/// <summary>
/// Notification-area icon and menu. WinForms NotifyIcon is used because WPF has
/// no native tray support. The menu is deliberately thin — open the window (to a
/// tab) or quit; everything else lives in the Settings tab.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _icon;
    private bool _hintShown;

    public TrayManager(App app)
    {
        _app = app;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("연결", null, (_, _) => _app.ShowMain(MainTab.Connect))
        { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("모니터 배치", null, (_, _) => _app.ShowMain(MainTab.Calibrate)));
        menu.Items.Add(new ToolStripMenuItem("설정", null, (_, _) => _app.ShowMain(MainTab.Settings)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => _app.ExitFromTray()));

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Snapfield",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _app.ShowMain(MainTab.Connect);
    }

    public void ShowHideHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.BalloonTipTitle = "Snapfield";
        _icon.BalloonTipText = "백그라운드에서 계속 실행 중입니다. 종료는 트레이 아이콘 우클릭 → 종료.";
        _icon.ShowBalloonTip(3000);
    }

    private static Icon CreateIcon()
    {
        using var bmp = Snapfield.Platform.IconArt.Render(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
