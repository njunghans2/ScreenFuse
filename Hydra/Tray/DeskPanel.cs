using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hydra.Desk;

namespace Hydra.Tray;

// The monitors tab: the whole desk on one page. Everything here acts immediately — picking a
// computer on a monitor switches its input, dragging rearranges the desk, handing over control
// restarts the agents into their new roles. Saving a profile is what you do *afterwards*, to keep
// an arrangement you liked, rather than a form you have to fill in before anything happens.
internal sealed class DeskPanel : UserControl
{
    private readonly IDeskService _desk;
    private readonly Action<string> _status;

    private readonly DeskCanvas _canvas;
    private readonly StackPanel _sharing = new() { Spacing = 6 };
    private readonly ComboBox _controller = SettingsWindow.NoWheel(new ComboBox { MinWidth = 190 });
    private readonly ComboBox _profiles = SettingsWindow.NoWheel(new ComboBox { MinWidth = 190 });
    private readonly TextBox _newProfile = new() { PlaceholderText = "Name this setup", MinWidth = 190 };
    private readonly TextBlock _peers = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _role = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };

    private bool _suppress;
    private bool _sharingSuppress;

    internal DeskPanel(IDeskService desk, Action<string> status)
    {
        _desk = desk;
        _status = status;
        _canvas = new DeskCanvas(SetMonitorHostAsync, SaveArrangementAsync);

        _controller.SelectionChanged += async (_, _) =>
        {
            if (_suppress || _controller.SelectedItem is not string host) return;
            if (string.Equals(host, _desk.Snapshot.Controller, StringComparison.OrdinalIgnoreCase)) return;
            Report(await _desk.SetControllerAsync(host));
        };

        Content = Build();
        _desk.Changed += OnChanged;
        // The settings window is thrown away and rebuilt each time it opens, so the subscription has
        // to go with it — otherwise every open leaves another live handler behind.
        DetachedFromVisualTree += (_, _) => _desk.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    private Control Build()
    {
        var activate = SettingsWindow.Action("Switch to it", async () =>
        {
            if (_profiles.SelectedItem is string name) Report(await _desk.ActivateSceneAsync(name));
        });
        var delete = SettingsWindow.Action("Delete", async () =>
        {
            if (_profiles.SelectedItem is string name) Report(await _desk.DeleteSceneAsync(name));
        });
        var save = SettingsWindow.Action("Save the desk as it is now", async () =>
        {
            Report(await _desk.SaveSceneAsync(_newProfile.Text ?? ""));
            _newProfile.Text = "";
        }, accent: true);

        return new ScrollViewer
        {
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Header("Your desk", "Every monitor on the desk, arranged the way it stands. Drag a monitor to move it — the crossing edges follow. Pick a computer on a monitor to switch that monitor's input to it right away."),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = "Keyboard and mouse:", VerticalAlignment = VerticalAlignment.Center },
                            _controller,
                        },
                    },
                    _role,
                    _canvas,
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "Mouse sharing per monitor", FontWeight = FontWeight.SemiBold },
                            new TextBlock
                            {
                                Text = "Turn off to keep the pointer from leaving or entering this monitor. The monitor keeps showing whichever computer drives it.",
                                Opacity = 0.72, TextWrapping = TextWrapping.Wrap,
                            },
                            _sharing,
                        },
                    },
                    _peers,
                    Header("Saved setups", "Keep the desk as it stands right now — which computer is on each monitor, and which one has the keyboard — under a name you can switch back to from the tray."),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _newProfile, save },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _profiles, activate, delete },
                    },
                },
            },
        };
    }

    private void Refresh()
    {
        var snapshot = _desk.Snapshot;
        _suppress = true;
        try
        {
            _controller.ItemsSource = snapshot.Hosts;
            _controller.SelectedItem = snapshot.Hosts.FirstOrDefault(h => string.Equals(h, snapshot.Controller, StringComparison.OrdinalIgnoreCase));
            var selectedProfile = _profiles.SelectedItem as string;
            _profiles.ItemsSource = snapshot.Scenes;
            _profiles.SelectedItem = snapshot.Scenes.FirstOrDefault(s => string.Equals(s, selectedProfile, StringComparison.OrdinalIgnoreCase))
                ?? snapshot.Scenes.FirstOrDefault(s => string.Equals(s, snapshot.CurrentScene, StringComparison.OrdinalIgnoreCase));
        }
        finally { _suppress = false; }

        _canvas.Update(snapshot.Monitors, snapshot.Hosts);

        _sharingSuppress = true;
        try
        {
            _sharing.Children.Clear();
            foreach (var monitor in snapshot.Monitors)
            {
                var toggle = new CheckBox
                {
                    Content = monitor.Sleeping
                        ? $"{monitor.Label} — sleeping (its display is blanked; it wakes when any monitor switches back)"
                        : monitor.Label,
                    IsChecked = monitor.CrossingEnabled,
                    IsEnabled = !monitor.Sleeping,
                    Tag = monitor.Id,
                };
                toggle.IsCheckedChanged += async (sender, _) =>
                {
                    if (_sharingSuppress || sender is not CheckBox box || box.Tag is not string id) return;
                    Report(await _desk.SetCrossingEnabledAsync(id, box.IsChecked == true));
                };
                _sharing.Children.Add(toggle);
            }
        }
        finally { _sharingSuppress = false; }

        var others = snapshot.Hosts.Where(h => !string.Equals(h, snapshot.LocalHost, StringComparison.OrdinalIgnoreCase)).ToList();
        var connected = snapshot.ConnectedHosts.Count;
        _peers.Text = others.Count == 0
            ? "No other computer has joined this desk yet."
            : connected == others.Count
                ? $"All {others.Count} other {(others.Count == 1 ? "computer is" : "computers are")} connected."
                : $"{connected} of {others.Count} connected — waiting for {string.Join(", ", others.Except(snapshot.ConnectedHosts, StringComparer.OrdinalIgnoreCase))}.";

        _role.Text = snapshot.IsController
            ? $"This computer ({snapshot.LocalHost}) has the keyboard and mouse."
            : $"{snapshot.Controller} has the keyboard and mouse. Changes made here are carried out by {snapshot.Controller}.";

    }

    private async Task SetMonitorHostAsync(string monitorId, string host) =>
        Report(await _desk.SetMonitorHostAsync(monitorId, host));

    private async Task SaveArrangementAsync(IReadOnlyList<DeskPlacement> placements) =>
        Report(await _desk.SaveArrangementAsync(placements));

    private void Report(DeskActionResult result) => _status(result.Message);

    private static StackPanel Header(string title, string description) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, Opacity = 0.72, TextWrapping = TextWrapping.Wrap },
        },
    };

}
