using System.Windows;
using System.Windows.Controls;
using AiCodingBar.Config;
using AiCodingBar.Models;
using AiCodingBar.Taskbar;

namespace AiCodingBar.Dashboard;

public partial class DisplayTab : UserControl
{
    private readonly ConfigManager _config;
    private readonly NativeTaskbarText _taskbarText;

    public DisplayTab(ConfigManager config, NativeTaskbarText taskbarText)
    {
        InitializeComponent();
        _config = config;
        _taskbarText = taskbarText;

        LoadConfig();

        SaveBtn.Click += (s, e) => SaveConfig();
        ResetBtn.Click += (s, e) => ResetConfig();
    }

    private void LoadConfig()
    {
        var t = _config.Current.Taskbar;
        ModeCombo.SelectedValue = t.Mode;
        ShowLine2Check.IsChecked = t.ShowLine2;
        Line2MaxBox.Text = t.Line2MaxSessions.ToString();
        ShowDurationCheck.IsChecked = t.ShowDuration;
        ShowZeroCheck.IsChecked = t.ShowZeroCounts;
        ThresholdBox.Text = t.AutoSwitchThreshold.ToString();
        FontNameBox.Text = t.FontName;
        FontSizeBox.Text = t.FontSize.ToString("0.#");
        AutoFontCheck.IsChecked = t.AutoFontSize;
    }

    private void SaveConfig()
    {
        var t = _config.Current.Taskbar;
        t.Mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "compact";
        t.ShowLine2 = ShowLine2Check.IsChecked == true;
        if (int.TryParse(Line2MaxBox.Text, out var lm)) t.Line2MaxSessions = lm;
        t.ShowDuration = ShowDurationCheck.IsChecked == true;
        t.ShowZeroCounts = ShowZeroCheck.IsChecked == true;
        if (int.TryParse(ThresholdBox.Text, out var th)) t.AutoSwitchThreshold = th;
        t.FontName = FontNameBox.Text.Trim();
        if (float.TryParse(FontSizeBox.Text, out var fs)) t.FontSize = fs;
        t.AutoFontSize = AutoFontCheck.IsChecked == true;

        _config.Save();
        _taskbarText.Refresh();
        StatusText.Text = "已保存 ✓";
    }

    private void ResetConfig()
    {
        _config.Current.Taskbar = new TaskbarConfig();
        _config.Save();
        LoadConfig();
        _taskbarText.Refresh();
        StatusText.Text = "已恢复默认 ✓";
    }
}
