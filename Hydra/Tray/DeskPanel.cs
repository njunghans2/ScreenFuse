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
    private readonly ComboBox _controller = SettingsWindow.NoWheel(new ComboBox { MinWidth = 190 });
    private readonly ComboBox _profiles = SettingsWindow.NoWheel(new ComboBox { MinWidth = 190 });
    private readonly TextBox _newProfile = new() { PlaceholderText = "Name this setup", MinWidth = 190 };
    private readonly TextBlock _peers = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _role = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _wiring = new() { Spacing = 10 };

    private bool _suppress;

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
                    new Expander
                    {
                        Header = "How each computer is wired",
                        Content = new StackPanel
                        {
                            Spacing = 10,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "ScreenFuse learns these on its own: a computer that can read a monitor is looking at its own input, so it records the code. Fill one in by hand only when a computer has never been on that monitor while ScreenFuse was running. Try switches the monitor over so you can see which cable the code selects.",
                                    Opacity = 0.72,
                                    TextWrapping = TextWrapping.Wrap,
                                },
                                _wiring,
                            },
                        },
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

        BuildWiring(snapshot);
    }

    private void BuildWiring(DeskSnapshot snapshot)
    {
        _wiring.Children.Clear();
        foreach (var monitor in snapshot.Monitors)
        {
            var rows = new StackPanel { Spacing = 6 };
            // The monitor itself reports which codes VCP 0x60 accepts, so offer those by name rather
            // than asking anyone to know that 17 means HDMI 1. Fall back to a number box only for
            // monitors that would not say.
            var offered = monitor.Sources
                .SelectMany(s => s.AvailableInputs ?? [])
                .Concat(monitor.Sources.Where(s => s.Input != null).Select(s => s.Input!.Value))
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            foreach (var host in snapshot.Hosts)
            {
                var source = monitor.Source(host);
                Control input;
                Func<int?> read;
                if (offered.Count > 0)
                {
                    var combo = SettingsWindow.NoWheel(new ComboBox
                    {
                        ItemsSource = offered.Select(InputName).ToList(),
                        MinWidth = 150,
                        SelectedIndex = source?.Input is { } known ? offered.IndexOf(known) : -1,
                    });
                    input = combo;
                    read = () => combo.SelectedIndex >= 0 ? offered[combo.SelectedIndex] : null;
                }
                else
                {
                    var number = SettingsWindow.NoWheel(new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, Value = source?.Input, MinWidth = 150 });
                    input = number;
                    read = () => number.Value is { } v ? decimal.ToInt32(v) : null;
                }

                var test = SettingsWindow.Action("Try", async () =>
                {
                    if (read() is not { } value) { _status("Pick an input first."); return; }
                    Report(await _desk.ProbeInputAsync(monitor.Id, host, value));
                });
                rows.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("2*,Auto,Auto,*"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        Col(new TextBlock { Text = host, VerticalAlignment = VerticalAlignment.Center }, 0),
                        Col(input, 1),
                        Col(test, 2),
                        Col(new TextBlock
                        {
                            Text = source?.Reachable == true ? "on this monitor now" : source?.Input == null ? "not known yet" : "",
                            Opacity = 0.6,
                            VerticalAlignment = VerticalAlignment.Center,
                        }, 3),
                    },
                });
            }

            _wiring.Children.Add(new Border
            {
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = monitor.Label, FontWeight = FontWeight.SemiBold },
                        rows,
                    },
                },
            });
        }

        if (_wiring.Children.Count == 0)
            _wiring.Children.Add(new TextBlock { Text = "No monitors have been discovered yet.", Opacity = 0.6 });
    }

    private async Task SetMonitorHostAsync(string monitorId, string host) =>
        Report(await _desk.SetMonitorHostAsync(monitorId, host));

    private async Task SaveArrangementAsync(IReadOnlyList<DeskPlacement> placements) =>
        Report(await _desk.SaveArrangementAsync(placements));

    private void Report(DeskActionResult result) => _status(result.Message);

    // MCCS assigns these meanings to VCP 0x60; the code is kept in the label because a monitor's
    // idea of "HDMI 1" and the socket printed on its back do not always agree.
    private static string InputName(int code) => code switch
    {
        1 => "VGA 1 (1)",
        2 => "VGA 2 (2)",
        3 => "DVI 1 (3)",
        4 => "DVI 2 (4)",
        5 => "Composite 1 (5)",
        7 => "S-Video 1 (7)",
        9 => "Component 1 (9)",
        15 => "DisplayPort 1 (15)",
        16 => "DisplayPort 2 (16)",
        17 => "HDMI 1 (17)",
        18 => "HDMI 2 (18)",
        27 => "USB-C (27)",
        _ => $"Input {code}",
    };

    private static StackPanel Header(string title, string description) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, Opacity = 0.72, TextWrapping = TextWrapping.Wrap },
        },
    };

    private static T Col<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
