using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskbarMonitor.Dashboard;

public record StatusInfo(SolidColorBrush Color, string Name);

public partial class SessionGrid : UserControl
{
    private readonly StateEngine _engine;
    private readonly ObservableCollection<SessionState> _sessions = new();
    private bool _showAll = true;
    private string _mode = "compact";

    private static readonly Dictionary<string, StatusInfo> StatusColors = new()
    {
        ["working"]      = new(new SolidColorBrush(Color.FromRgb(0x16, 0xa3, 0x4a)), "working"),
        ["thinking"]     = new(new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)), "thinking"),
        ["error"]        = new(new SolidColorBrush(Color.FromRgb(0xdc, 0x26, 0x26)), "error"),
        ["idle"]         = new(new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), "idle"),
        ["sleeping"]     = new(new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), "sleeping"),
        ["attention"]    = new(new SolidColorBrush(Color.FromRgb(0xd9, 0x77, 0x06)), "attention"),
        ["sweeping"]     = new(new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x5b)), "sweeping"),
        ["juggling"]     = new(new SolidColorBrush(Color.FromRgb(0xb4, 0x53, 0x09)), "juggling"),
        ["notification"] = new(new SolidColorBrush(Color.FromRgb(0xb4, 0x53, 0x09)), "notification"),
        ["carrying"]     = new(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xeb)), "carrying"),
        ["complete"]     = new(new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a)), "complete"),
    };

    public SessionGrid(StateEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        SessionList.ItemsSource = _sessions;
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_sessions, new object());

        _engine.OnSessionUpdated += OnSessionUpdated;
        _engine.OnSessionRemoved += OnSessionRemoved;
        _engine.OnAnyChange += RefreshAll;

        RefreshAll();
    }

    public void ApplyLanguage(string lang)
    {
        var zh = lang == "zh";
        TitleText.Text = zh ? "会话列表" : "Sessions";
        FilterAll.Content = zh ? "全部" : "All";
        FilterActive.Content = zh ? "活跃" : "Active";
    }

    public void ApplyMode(string mode)
    {
        _mode = mode;
        RefreshAll();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _showAll = FilterAll.IsChecked == true;
        RefreshAll();
    }

    private void OnSessionUpdated(SessionState session)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
            if (existing != null)
            {
                var idx = _sessions.IndexOf(existing);
                _sessions[idx] = session;
            }
            else
            {
                _sessions.Add(session);
            }
            RefreshByMode();
        });
    }

    private void OnSessionRemoved(SessionState session)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
            if (existing != null) _sessions.Remove(existing);
            RefreshByMode();
        });
    }

    private void RefreshAll()
    {
        Dispatcher.Invoke(() =>
        {
            var filtered = _engine.Sessions.Values.AsEnumerable();
            if (!_showAll)
                filtered = filtered.Where(s => s.Status != "sleeping" && s.Status != "idle");

            _sessions.Clear();
            foreach (var s in filtered.OrderBy(s => s.SortIndex))
                _sessions.Add(s);

            RefreshByMode();
        });
    }

    private void RefreshByMode()
    {
        switch (_mode)
        {
            case "aggregate":
                ShowAggregate();
                break;
            case "highlight":
                ShowHighlight();
                break;
            default:
                ShowCompact();
                break;
        }
        UpdateCount();
    }

    private void ShowCompact()
    {
        AggregatePanel.Visibility = Visibility.Collapsed;
        HighlightCard.Visibility = Visibility.Collapsed;
        SessionList.Visibility = Visibility.Visible;

        // Restore default sort order
        var items = _sessions.ToList();
        _sessions.Clear();
        foreach (var s in items.OrderBy(s => s.SortIndex))
            _sessions.Add(s);
    }

    private void ShowAggregate()
    {
        AggregatePanel.Visibility = Visibility.Visible;
        HighlightCard.Visibility = Visibility.Collapsed;
        SessionList.Visibility = Visibility.Collapsed;

        AggregatePanel.Children.Clear();

        var counts = _engine.Sessions.Values
            .GroupBy(s => s.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        // Read ShowZeroCounts from config via parent (we don't have direct access,
        // so we show all known statuses and let the user see zeros)
        var allStatuses = new[] { "working", "thinking", "attention", "juggling", "sweeping", "notification", "error", "carrying", "complete", "idle", "sleeping" };

        foreach (var status in allStatuses)
        {
            var count = counts.GetValueOrDefault(status, 0);
            if (count == 0) continue;

            var info = StatusColors.GetValueOrDefault(status,
                new StatusInfo(new SolidColorBrush(Color.FromRgb(0x93, 0x93, 0x99)), status));

            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                MinWidth = 120,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(16, 14, 16, 14),
            };

            var stack = new StackPanel();

            // Status dot + name
            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = info.Color,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = info.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1b)),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);

            // Count
            stack.Children.Add(new TextBlock
            {
                Text = count.ToString(),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = info.Color,
                Margin = new Thickness(0, 8, 0, 0)
            });

            card.Child = stack;
            AggregatePanel.Children.Add(card);
        }
    }

    private void ShowHighlight()
    {
        AggregatePanel.Visibility = Visibility.Collapsed;

        var sessions = _engine.Sessions.Values
            .OrderByDescending(s => s.LastUpdateAt)
            .ToList();

        if (sessions.Count == 0)
        {
            HighlightCard.Visibility = Visibility.Collapsed;
            SessionList.Visibility = Visibility.Visible;
            return;
        }

        // Populate highlight card
        var latest = sessions[0];
        HighlightCard.Visibility = Visibility.Visible;

        var info = StatusColors.GetValueOrDefault(latest.Status,
            new StatusInfo(new SolidColorBrush(Color.FromRgb(0x93, 0x93, 0x99)), latest.Status));

        HlDot.Fill = info.Color;
        HlIndex.Text = latest.SortIndex.ToString();
        HlTitle.Text = latest.SessionTitle ?? "(no title)";
        HlStatus.Text = latest.Status;
        HlEvent.Text = latest.LastEvent;
        HlId.Text = $"ID: {latest.SessionId}";
        HlTool.Text = $"Tool: {latest.ToolName ?? "--"}";
        HlCwd.Text = $"CWD: {latest.Cwd ?? "--"}";
        HlBadgeText.Text = latest.Status;
        HlBadgeText.Foreground = info.Color;

        // Set DataContext for XAML bindings (LastUpdateAt display)
        HighlightCard.DataContext = latest;

        // Show remaining sessions in the list
        var remaining = sessions.Skip(1).OrderBy(s => s.SortIndex).ToList();
        SessionList.Visibility = remaining.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _sessions.Clear();
        foreach (var s in remaining)
            _sessions.Add(s);
    }

    private void UpdateCount()
    {
        CountText.Text = _sessions.Count.ToString();
    }
}
