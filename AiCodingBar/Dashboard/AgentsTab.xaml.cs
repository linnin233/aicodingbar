using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiCodingBar.Config;
using AiCodingBar.Models;
using AiCodingBar.Server;
using AiCodingBar.Taskbar;

namespace AiCodingBar.Dashboard;

public partial class AgentsTab : UserControl
{
    private readonly ConfigManager _config;
    private readonly NativeTaskbarText _taskbarText;
    private Dictionary<string, Expander> _expanders = new();

    public AgentsTab(ConfigManager config, NativeTaskbarText taskbarText)
    {
        InitializeComponent();
        _config = config;
        _taskbarText = taskbarText;

        BuildAgentRows();
    }

    /// <summary>
    /// 为每个 agent 动态构建 Expander 行（clawd-on-desk 风格）
    /// </summary>
    private void BuildAgentRows()
    {
        AgentsStack.Children.Clear();
        _expanders.Clear();

        var agents = AgentConfigFactory.GetDefaults();
        var agentConfig = _config.Current.Agents;

        foreach (var agent in agents)
        {
            // 从 config 加载用户覆盖
            if (agentConfig.TryGetValue(agent.AgentId, out var saved))
            {
                agent.Enabled = saved.Enabled;
                agent.PermissionsEnabled = saved.PermissionsEnabled;
                agent.NotificationHookEnabled = saved.NotificationHookEnabled;
            }

            // Hook 状态标签
            var hookStatus = agent.AgentId == "claude-code"
                ? (HookInstaller.IsInstalled() ? "hook" : "not installed")
                : agent.AgentId == "opencode"
                    ? (HookInstaller.IsOpencodePluginInstalled() ? "plugin" : "not installed")
                    : "";

            var hookColor = hookStatus.Contains("not") ? "#E04040" : "#00A040";

            // Master toggle + badge 标题
            var headerPanel = new Grid { Margin = new Thickness(0) };
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel { Orientation = Orientation.Horizontal };
            leftStack.Children.Add(new TextBlock
            {
                Text = agent.DisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (!string.IsNullOrEmpty(hookStatus))
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hookColor)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = hookStatus,
                        Foreground = Brushes.White,
                        FontSize = 10,
                    }
                };
                leftStack.Children.Add(badge);
            }

            headerPanel.Children.Add(leftStack);

            // Master toggle
            var masterToggle = new CheckBox
            {
                Style = (Style)FindResource("ToggleSwitch"),
                IsChecked = agent.Enabled,
                Tag = agent.AgentId,
                VerticalAlignment = VerticalAlignment.Center,
            };
            masterToggle.Checked += AgentMasterToggled;
            masterToggle.Unchecked += AgentMasterToggled;
            Grid.SetColumn(masterToggle, 1);
            headerPanel.Children.Add(masterToggle);

            // Expander 正文 — 子设置行
            var detailPanel = new StackPanel { Margin = new Thickness(8, 8, 0, 4) };

            if (agent.SupportsPermission)
            {
                var permToggle = new CheckBox
                {
                    Style = (Style)FindResource("ToggleSwitch"),
                    Content = agent.AgentId == "claude-code" ? "Show permission status" : "Permission handling",
                    IsChecked = agent.PermissionsEnabled,
                    IsEnabled = agent.Enabled,
                    Tag = $"{agent.AgentId}:perms",
                    Margin = new Thickness(0, 0, 0, 6),
                };
                permToggle.Checked += AgentDetailToggled;
                permToggle.Unchecked += AgentDetailToggled;
                detailPanel.Children.Add(permToggle);
            }

            if (agent.NotificationHookEnabled || agent.AgentId == "claude-code")
            {
                var notifToggle = new CheckBox
                {
                    Style = (Style)FindResource("ToggleSwitch"),
                    Content = "Show notification / question",
                    IsChecked = agent.NotificationHookEnabled,
                    IsEnabled = agent.Enabled,
                    Tag = $"{agent.AgentId}:notif",
                    Margin = new Thickness(0, 0, 0, 6),
                };
                notifToggle.Checked += AgentDetailToggled;
                notifToggle.Unchecked += AgentDetailToggled;
                detailPanel.Children.Add(notifToggle);
            }

            // 操作按钮行
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (agent.AgentId == "claude-code")
            {
                actionRow.Children.Add(CreateActionButton("Install Claude Hooks", async () =>
                {
                    await HookInstaller.InstallAsync();
                    _taskbarText.Refresh();
                }));
                actionRow.Children.Add(CreateActionButton("Uninstall", async () =>
                {
                    await HookInstaller.UninstallAsync();
                    _taskbarText.Refresh();
                }));
            }
            else if (agent.AgentId == "opencode")
            {
                actionRow.Children.Add(CreateActionButton("Register opencode plugin", async () =>
                {
                    await HookInstaller.InstallOpencodePluginAsync();
                    _taskbarText.Refresh();
                }));
            }
            detailPanel.Children.Add(actionRow);

            var expander = new Expander
            {
                Header = headerPanel,
                Content = detailPanel,
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            };

            _expanders[agent.AgentId] = expander;
            AgentsStack.Children.Add(expander);
        }
    }

    private static Button CreateActionButton(string text, Func<Task> onClick)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += async (s, e) => await onClick();
        return btn;
    }

    private void AgentMasterToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string agentId) return;

        var enabled = cb.IsChecked == true;

        // 更新 config
        _config.Current.Agents.GetOrAdd(agentId, () => new AgentConfig()).Enabled = enabled;
        _config.Save();

        // 启用/禁用子控件
        if (_expanders.TryGetValue(agentId, out var expander))
        {
            DisableSubToggles(expander.Content, !enabled);
        }
    }

    private void AgentDetailToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string tag) return;
        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        var agentId = parts[0];
        var flag = parts[1];

        var cfg = _config.Current.Agents.GetOrAdd(agentId, () => new AgentConfig());
        if (flag == "perms") cfg.PermissionsEnabled = cb.IsChecked == true;
        if (flag == "notif") cfg.NotificationHookEnabled = cb.IsChecked == true;
        _config.Save();
    }

    private static void DisableSubToggles(object? content, bool disabled)
    {
        if (content is not Panel panel) return;
        foreach (var child in panel.Children)
        {
            if (child is CheckBox cb && cb.Tag is string)
                cb.IsEnabled = !disabled;
        }
    }
}
