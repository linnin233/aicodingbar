using System.Windows;
using System.Windows.Controls;
using AiCodingBar.Config;

namespace AiCodingBar.Dashboard;

public partial class ServerTab : UserControl
{
    private readonly ConfigManager _config;

    public ServerTab(ConfigManager config)
    {
        InitializeComponent();
        _config = config;

        LoadConfig();

        SaveBtn.Click += (s, e) => SaveConfig();
    }

    private void LoadConfig()
    {
        var s = _config.Current.Server;
        StartPortBox.Text = s.StartPort.ToString();
        EndPortBox.Text = s.EndPort.ToString();
        CurrentPortText.Text = ConfigManager.ReadRuntimePort().ToString();
        AutoInstallHooksCheck.IsChecked = _config.Current.AutoInstallHooks;
        AutoInstallPluginCheck.IsChecked = _config.Current.AutoInstallPlugin;
    }

    private void SaveConfig()
    {
        var s = _config.Current.Server;
        if (int.TryParse(StartPortBox.Text, out var sp)) s.StartPort = sp;
        if (int.TryParse(EndPortBox.Text, out var ep)) s.EndPort = ep;
        _config.Current.AutoInstallHooks = AutoInstallHooksCheck.IsChecked == true;
        _config.Current.AutoInstallPlugin = AutoInstallPluginCheck.IsChecked == true;

        _config.Save();
        StatusText.Text = "已保存。端口变更需重启后生效。";
    }
}
