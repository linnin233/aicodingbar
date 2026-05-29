using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiCodingBar.Models;
using AiCodingBar.Server;

namespace AiCodingBar.Dashboard;

public partial class SessionGrid : UserControl
{
    private readonly StateEngine _engine;
    private readonly ObservableCollection<SessionState> _sessions = new();
    private bool _pinned;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            PinCheckBox.IsChecked = value;
        }
    }

    public event Action<bool>? PinChanged;

    public SessionGrid(StateEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        SessionDataGrid.ItemsSource = _sessions;
        BindingOperations.EnableCollectionSynchronization(_sessions, new object());

        _engine.OnSessionUpdated += OnSessionUpdated;
        _engine.OnSessionRemoved += OnSessionRemoved;
        _engine.OnAnyChange += RefreshAll;

        PinCheckBox.Checked += (s, e) => { _pinned = true; PinChanged?.Invoke(true); };
        PinCheckBox.Unchecked += (s, e) => { _pinned = false; PinChanged?.Invoke(false); };

        FilterCombo.SelectionChanged += (s, e) => RefreshAll();

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
            UpdateCount();
        });
    }

    private void OnSessionRemoved(SessionState session)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
            if (existing != null) _sessions.Remove(existing);
            UpdateCount();
        });
    }

    private void RefreshAll()
    {
        Dispatcher.Invoke(() =>
        {
            var filtered = _engine.Sessions.Values.AsEnumerable();

            if (FilterCombo.SelectedIndex == 1) // 活跃
                filtered = filtered.Where(s => s.Status != "sleeping");

            _sessions.Clear();
            foreach (var s in filtered.OrderBy(s => s.SortIndex))
                _sessions.Add(s);

            UpdateCount();
        });
    }

    private void UpdateCount()
    {
        CountText.Text = _sessions.Count.ToString();
    }
}
