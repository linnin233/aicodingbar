using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AiCodingBar.Config;
using AiCodingBar.Models;
using AiCodingBar.Server;

namespace AiCodingBar.Dashboard;

public partial class ConfigPanel : UserControl
{
    private readonly ConfigManager _config;
    private readonly Action? _onConfigChanged;
    private ObservableCollection<KeyValuePair<string, StateMapping>> _mappings = new();

    public ConfigPanel(ConfigManager config, Action? onConfigChanged = null)
    {
        InitializeComponent();
        _config = config;
        _onConfigChanged = onConfigChanged;

        LoadConfig();
        RefreshHookStatus();

        SaveBtn.Click += (s, e) => SaveConfig();
        ResetBtn.Click += (s, e) =>
        {
            _config.Current.StateMapping = ConfigModel.DefaultMappings();
            LoadConfig();
            SaveConfig();
        };

        InstallHooksBtn.Click += async (s, e) =>
        {
            InstallHooksBtn.IsEnabled = false;
            HookStatusText.Text = "Hook 状态: 安装中...";
            var ok = await HookInstaller.InstallAsync();
            HookStatusText.Text = ok ? "Hook 状态: 已安装" : "Hook 状态: 安装失败(检查 Node.js 是否可用)";
            InstallHooksBtn.IsEnabled = true;
        };

        UninstallHooksBtn.Click += async (s, e) =>
        {
            UninstallHooksBtn.IsEnabled = false;
            HookStatusText.Text = "Hook 状态: 卸载中...";
            var ok = await HookInstaller.UninstallAsync();
            HookStatusText.Text = ok ? "Hook 状态: 已卸载" : "Hook 状态: 卸载失败";
            UninstallHooksBtn.IsEnabled = true;
        };
    }

    private void RefreshHookStatus()
    {
        HookStatusText.Text = HookInstaller.IsInstalled()
            ? "Hook 状态: 已安装"
            : "Hook 状态: 未安装";
    }

    private void LoadConfig()
    {
        _mappings = new ObservableCollection<KeyValuePair<string, StateMapping>>(
            _config.Current.StateMapping.Select(kv =>
                new KeyValuePair<string, StateMapping>(kv.Key, new StateMapping
                {
                    State = kv.Value.State,
                    Name = kv.Value.Name,
                    Abbr = kv.Value.Abbr,
                    Color = kv.Value.Color,
                    StateKind = kv.Value.StateKind,
                    Priority = kv.Value.Priority,
                    AutoReturnMs = kv.Value.AutoReturnMs,
                })
            )
        );
        MappingGrid.ItemsSource = _mappings;

        ModeCombo.SelectedValue = _config.Current.Taskbar.Mode switch
        {
            "aggregate" => "aggregate",
            "highlight" => "highlight",
            _ => "compact",
        };
        ThresholdBox.Text = _config.Current.Taskbar.AutoSwitchThreshold.ToString();
        ShowZeroCheckBox.IsChecked = _config.Current.Taskbar.ShowZeroCounts;
    }

    private void SaveConfig()
    {
        _config.Current.StateMapping = _mappings.ToDictionary(kv => kv.Key, kv => kv.Value);
        _config.Current.Taskbar.Mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "compact";
        if (int.TryParse(ThresholdBox.Text, out var threshold))
            _config.Current.Taskbar.AutoSwitchThreshold = threshold;
        _config.Current.Taskbar.ShowZeroCounts = ShowZeroCheckBox.IsChecked == true;
        _config.Save();
        _onConfigChanged?.Invoke();
        MessageBox.Show("配置已保存", "AiCodingBar", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
