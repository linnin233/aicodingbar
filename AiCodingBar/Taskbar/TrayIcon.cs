using System.Drawing;
using System.Windows.Forms;
using AiCodingBar.Server;

namespace AiCodingBar.Taskbar;

public class TrayIcon : IDisposable
{
    private readonly StateEngine _engine;
    private readonly Func<Config.ConfigManager> _getConfig;
    private NotifyIcon? _notifyIcon;

    public event Action? OnShowDashboard;
    public event Action? OnExit;

    public TrayIcon(StateEngine engine, Func<Config.ConfigManager> getConfig)
    {
        _engine = engine;
        _getConfig = getConfig;
    }

    public void Show()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "AiCodingBar",
            Icon = CreateDefaultIcon(),
            Visible = true,
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnShowDashboard?.Invoke();
        };

        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("暂无会话");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);

        var modeMenu = new ToolStripMenuItem("显示模式");
        AddModeItem(modeMenu, "compact", "紧凑模式");
        AddModeItem(modeMenu, "aggregate", "聚合模式");
        AddModeItem(modeMenu, "highlight", "高亮模式");
        menu.Items.Add(modeMenu);

        menu.Items.Add(new ToolStripSeparator());

        var dashboardItem = new ToolStripMenuItem("打开 Dashboard");
        dashboardItem.Click += (s, e) => OnShowDashboard?.Invoke();
        menu.Items.Add(dashboardItem);

        var hookItem = new ToolStripMenuItem("安装 Hook");
        hookItem.Click += async (s, e) =>
        {
            hookItem.Enabled = false;
            hookItem.Text = "安装中...";
            var ok = await HookInstaller.EnsureInstalledAsync();
            hookItem.Text = ok ? "Hook 已安装" : "Hook 安装失败";
            hookItem.Enabled = !ok;
        };
        menu.Items.Add(hookItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => OnExit?.Invoke();
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;

        // Update tray text periodically
        _engine.OnAnyChange += () =>
        {
            if (_notifyIcon == null) return;
            var count = _engine.Sessions.Count;
            var activeCount = _engine.Sessions.Values.Count(s => s.Status != "sleeping");
            _notifyIcon.Text = $"AiCodingBar - 活跃 {activeCount} / 总计 {count}";
            statusItem.Text = $"活跃 {activeCount} / 总计 {count}";
        };
    }

    private void AddModeItem(ToolStripMenuItem parent, string mode, string label)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (s, e) =>
        {
            _getConfig().Current.Taskbar.Mode = mode;
            _getConfig().Save();
        };
        parent.DropDownItems.Add(item);
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
    }

    private static Icon CreateDefaultIcon()
    {
        // Create a simple 16x16 icon programmatically
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Transparent);

        // Draw a simple colored circle
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(0, 128, 224));
        g.FillEllipse(brush, 2, 2, 12, 12);

        // Draw "C" initial
        using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        using var textBrush = new SolidBrush(System.Drawing.Color.White);
        g.DrawString("C", font, textBrush, 3, 1);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
