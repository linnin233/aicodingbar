using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace TaskbarMonitor.Dashboard;

public partial class ConfigPanel : UserControl
{
    private readonly Config _config;
    private readonly Action? _onConfigChanged;
    private ObservableCollection<KeyValuePair<string, StateMapping>> _mappings = new();
    private bool _loading = true;

    public ConfigPanel(Config config, Action? onConfigChanged = null)
    {
        InitializeComponent();
        _config = config;
        _onConfigChanged = onConfigChanged;

        LoadConfig();
        _loading = false;
        RefreshHookStatus();

        SaveBtn.Click += (s, e) => SaveConfig();
        ResetBtn.Click += (s, e) =>
        {
            _config.StateMapping = Config.DefaultMappings();
            LoadConfig();
            SaveConfig();
        };

        LangCombo.SelectionChanged += (s, e) =>
        {
            if (LangCombo.SelectedIndex < 0) return;
            _config.Language = LangCombo.SelectedIndex == 0 ? "zh" : "en";
            ConfigManager.Save(_config);
            _onConfigChanged?.Invoke();
        };

        ModeCombo.SelectionChanged += (s, e) =>
        {
            if (_loading || ModeCombo.SelectedIndex < 0) return;
            var mode = (ModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "compact";
            _config.Taskbar.Mode = mode;
            ConfigManager.Save(_config);
            _onConfigChanged?.Invoke();
        };

        InstallHooksBtn.Click += async (s, e) =>
        {
            InstallHooksBtn.IsEnabled = false;
            var zh = _config.Language == "zh";
            HookStatusText.Text = zh ? "Hook 状态: 安装中..." : "Hook Status: installing...";
            var ok = await HookInstaller.InstallAsync();
            HookStatusText.Text = ok
                ? (zh ? "Hook 状态: 已安装" : "Hook Status: installed")
                : (zh ? "Hook 状态: 安装失败" : "Hook Status: install failed");
            InstallHooksBtn.IsEnabled = true;
        };

        UninstallHooksBtn.Click += async (s, e) =>
        {
            UninstallHooksBtn.IsEnabled = false;
            var zh = _config.Language == "zh";
            HookStatusText.Text = zh ? "Hook 状态: 卸载中..." : "Hook Status: uninstalling...";
            var ok = await HookInstaller.UninstallAsync();
            HookStatusText.Text = ok
                ? (zh ? "Hook 状态: 已卸载" : "Hook Status: uninstalled")
                : (zh ? "Hook 状态: 卸载失败" : "Hook Status: uninstall failed");
            UninstallHooksBtn.IsEnabled = true;
        };
    }

    public void ApplyLanguage(string lang)
    {
        var zh = lang == "zh";

        PageTitleText.Text = zh ? "配置" : "Config";
        PageSubText.Text = zh ? "自定义任务栏显示、状态映射和 Hook。" : "Customize taskbar display, state mappings, and hooks.";

        SecLangText.Text = zh ? "语言" : "LANGUAGE";
        LangLabelText.Text = zh ? "界面语言" : "Language";
        LangDescText.Text = zh ? "界面显示语言" : "Interface language";

        SecDisplayText.Text = zh ? "显示" : "DISPLAY";
        ModeLabelText.Text = zh ? "显示模式" : "Display Mode";
        ModeDescText.Text = zh ? "多个会话在任务栏中的显示方式" : "How multiple sessions are shown in the taskbar";
        ZeroLabelText.Text = zh ? "显示零值" : "Show Zero Counts";
        ZeroDescText.Text = zh ? "聚合模式下显示无活跃会话的状态" : "Display states with zero active sessions in aggregate mode";
        ThresholdLabelText.Text = zh ? "自动切换阈值" : "Auto-Switch Threshold";
        ThresholdDescText.Text = zh ? "会话数超过此值时切换到聚合模式" : "Switch to aggregate mode when sessions exceed this count";
        FontLabelText.Text = zh ? "字体大小" : "Font Size";
        FontDescText.Text = zh ? "任务栏显示的文字大小 (pt)" : "Text size in the taskbar display (pt)";
        SpacingLabelText.Text = zh ? "段间距" : "Segment Spacing";
        SpacingDescText.Text = zh ? "显示段之间的间距 (px)" : "Space between display segments (px)";
        PadXLabelText.Text = zh ? "水平内边距" : "Padding X";
        PadXDescText.Text = zh ? "显示区域的水平内边距 (px)" : "Horizontal padding inside the display area (px)";
        PadYLabelText.Text = zh ? "垂直内边距" : "Padding Y";
        PadYDescText.Text = zh ? "显示区域的垂直内边距 (px)" : "Vertical padding inside the display area (px)";

        SecMappingText.Text = zh ? "状态映射" : "STATE MAPPINGS";

        SaveBtn.Content = zh ? "保存" : "Save";
        ResetBtn.Content = zh ? "恢复默认" : "Reset";

        SecHooksText.Text = "HOOKS";
        HookDescText.Text = zh ? "Claude Code Hook 集成，实时监控会话状态" : "Claude Code hook integration for real-time session monitoring";
        InstallHooksBtn.Content = zh ? "安装" : "Install";
        UninstallHooksBtn.Content = zh ? "卸载" : "Uninstall";

        RefreshHookStatus();
    }

    private void RefreshHookStatus()
    {
        var zh = _config.Language == "zh";
        HookStatusText.Text = HookInstaller.IsInstalled()
            ? (zh ? "Hook 状态: 已安装" : "Hook Status: installed")
            : (zh ? "Hook 状态: 未安装" : "Hook Status: not installed");
    }

    private void LoadConfig()
    {
        _mappings = new ObservableCollection<KeyValuePair<string, StateMapping>>(
            _config.StateMapping.Select(kv =>
                new KeyValuePair<string, StateMapping>(kv.Key, new StateMapping
                {
                    State = kv.Value.State,
                    Name = kv.Value.Name,
                    Abbr = kv.Value.Abbr,
                    Color = kv.Value.Color,
                })
            )
        );
        MappingGrid.ItemsSource = _mappings;

        LangCombo.SelectedIndex = _config.Language == "en" ? 1 : 0;

        ModeCombo.SelectedValue = _config.Taskbar.Mode switch
        {
            "aggregate" => "aggregate",
            "highlight" => "highlight",
            _ => "compact",
        };
        ThresholdBox.Text = _config.Taskbar.AutoSwitchThreshold.ToString();
        ShowZeroCheckBox.IsChecked = _config.Taskbar.ShowZeroCounts;
        FontSizeBox.Text = _config.Taskbar.FontSize.ToString();
        SpacingBox.Text = _config.Taskbar.Spacing.ToString();
        PaddingXBox.Text = _config.Taskbar.PaddingX.ToString();
        PaddingYBox.Text = _config.Taskbar.PaddingY.ToString();
    }

    private void SaveConfig()
    {
        _config.StateMapping = _mappings.ToDictionary(kv => kv.Key, kv => kv.Value);
        _config.Taskbar.Mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "compact";
        if (int.TryParse(ThresholdBox.Text, out var threshold))
            _config.Taskbar.AutoSwitchThreshold = threshold;
        _config.Taskbar.ShowZeroCounts = ShowZeroCheckBox.IsChecked == true;
        if (float.TryParse(FontSizeBox.Text, out var fontSize) && fontSize >= 6 && fontSize <= 24)
            _config.Taskbar.FontSize = fontSize;
        if (int.TryParse(SpacingBox.Text, out var spacing) && spacing >= 0 && spacing <= 40)
            _config.Taskbar.Spacing = spacing;
        if (int.TryParse(PaddingXBox.Text, out var padX) && padX >= 0 && padX <= 20)
            _config.Taskbar.PaddingX = padX;
        if (int.TryParse(PaddingYBox.Text, out var padY) && padY >= 0 && padY <= 20)
            _config.Taskbar.PaddingY = padY;
        ConfigManager.Save(_config);
        _onConfigChanged?.Invoke();
        var zh = _config.Language == "zh";
        MessageBox.Show(zh ? "配置已保存" : "Config saved", "TaskbarMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
