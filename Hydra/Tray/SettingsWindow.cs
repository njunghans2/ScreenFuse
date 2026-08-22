using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Cathedral.Config;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Display;
using Hydra.Screen;
using Hydra.Scenes;
using Microsoft.Extensions.Logging;

namespace Hydra.Tray;

internal sealed class SettingsWindow : Window
{
    private readonly string _configPath;
    private readonly IDisplayRouter _displayRouter;
    private readonly Action _restartAfterSave;
    private readonly IReadOnlyList<string> _connectedPeers;
    private readonly TextBlock _status;
    private readonly TextBox _machineName = new() { PlaceholderText = "This computer's name" };
    private readonly ComboBox _role = Choice("Controls the desk (master)", "Joins the desk (secondary computer)");
    private readonly TextBox _deskName = new() { PlaceholderText = "e.g. studio" };
    private readonly TextBox _password = new() { PlaceholderText = "Shared secret (16+ characters)", PasswordChar = '●' };
    private readonly NumericUpDown _relayPort = Number(5000, 1024, 65535);
    private readonly NumericUpDown _mouseScale = Number(1, 0.1m, 10, 0.1m);
    private readonly CheckBox _syncScreensaver = new() { Content = "Keep screen savers in sync", IsChecked = true };
    private readonly CheckBox _screenLock = new() { Content = "Lock the other computers when this computer locks" };
    private readonly CheckBox _hideCursor = new() { Content = "Hide an idle cursor", IsChecked = false };
    private readonly CheckBox _accelerateWheel = new() { Content = "Smooth accelerated scrolling", IsChecked = true };
    private readonly StackPanel _peerRows = new() { Spacing = 8 };
    private readonly StackPanel _sceneRows = new() { Spacing = 12 };
    private readonly TextBlock _computerHint = Hint("");
    private readonly List<PeerEditor> _peers = [];
    private readonly List<SceneEditor> _scenes = [];
    private Button? _addPeerButton;
    private HydraConfigFile? _loaded;

    internal SettingsWindow(string configPath, IDisplayRouter? displayRouter, ISceneCoordinator? coordinator, string? initialStatus, Action restartAfterSave)
    {
        _configPath = Path.GetFullPath(configPath);
        _displayRouter = displayRouter ?? new DisplayRouter(Microsoft.Extensions.Logging.Abstractions.NullLogger<DisplayRouter>.Instance);
        _connectedPeers = coordinator?.ConnectedPeers ?? [];
        _restartAfterSave = restartAfterSave;
        Title = "ScreenFuse Settings";
        Width = 960;
        Height = 760;
        MinWidth = 720;
        MinHeight = 560;
        Icon = TrayIconImage.Create();

        var loadWarning = LoadForm();
        _status = new TextBlock
        {
            Text = initialStatus ?? loadWarning ?? $"Ready — settings are stored in {_configPath}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
        };

        _role.SelectionChanged += (_, _) => UpdateRoleState();
        _machineName.TextChanged += (_, _) => RefreshPeerSources();
        UpdateRoleState();

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                Tab("Desk", DeskTab()),
                Tab("Computers", ComputersTab()),
                Tab("Scenes & monitors", ScenesTab()),
                Tab("Preferences", PreferencesTab()),
            },
        };

        var save = Button("Save and restart", SaveAsync, accent: true);
        var diagnostics = Button("Display diagnostics", DiagnosticsAsync);
        var startup = Button("Launch on startup", InstallStartupAsync);
        var close = Button("Close", () => { Close(); return Task.CompletedTask; });

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(22),
            RowSpacing = 12,
            Children =
            {
                At(new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = "ScreenFuse", FontSize = 28, FontWeight = FontWeight.SemiBold },
                        Hint("Set up the whole desk here. No configuration syntax is required."),
                    },
                }, 0),
                At(tabs, 1),
                At(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, diagnostics, startup, close } }, 2),
                At(_status, 3),
            },
        };
    }

    internal void SetStatus(string message) => _status.Text = message;

    private Control DeskTab()
    {
        var copyCode = Button("Copy join code", async () =>
        {
            ValidateDeskFields();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(BuildJoinCode()));
            await clipboard.SetDataAsync(transfer);
            SetStatus("Join code copied. Paste it into ScreenFuse on the other computer.");
        });
        var pasteCode = Button("Paste join code", async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
            var code = await clipboard.TryGetTextAsync() ?? "";
            ApplyJoinCode(code);
            _role.SelectedIndex = 1;
            SetStatus("Join code applied. Add matching scene names, then save.");
        });

        return Scroll(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("This computer", "Give every computer a short, unique name."),
                Field("Computer name", _machineName),
                Field("What does this computer do?", _role),
                Section("Local desk connection", "ScreenFuse discovers the master automatically on the LAN. The desk name and secret must match on every computer."),
                Field("Desk name", _deskName),
                Field("Shared secret", _password, "Generated automatically for a new desk. Use the join code instead of retyping it."),
                Field("Relay port", _relayPort, "The default works on most home and office LANs."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyCode, pasteCode } },
            },
        });
    }

    private Control ComputersTab()
    {
        _addPeerButton = Button("Add computer", () =>
        {
            AddPeer("Computer", LocalName(), Direction.Right);
            return Task.CompletedTask;
        });
        return Scroll(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Section("Screen layout", "ScreenFuse starts from the display arrangement already configured in Windows, macOS, or Linux. Pairing chooses a matching edge automatically whenever the connected display layouts make it clear."),
                _computerHint,
                new Expander
                {
                    Header = "Fine-tune manually",
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            Hint("Use this only when computers have no shared display anchor, or when you want a custom crossing edge."),
                            new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("2*,2*,*,Auto"), ColumnSpacing = 8,
                                Children = { HeaderCol("Computer", 0), HeaderCol("Placed next to", 1), HeaderCol("On its", 2), HeaderCol("", 3) },
                            },
                            _peerRows,
                            _addPeerButton,
                        },
                    },
                },
            },
        });
    }

    private Control ScenesTab()
    {
        var add = Button("Add scene", () =>
        {
            AddScene(new SceneFormData($"Scene {_scenes.Count + 1}", [], DisplayPower.Keep, 500));
            return Task.CompletedTask;
        });
        return Scroll(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Section("Desk scenes", "A scene changes the shared monitor inputs and then switches every computer to the matching layout. Use the same scene names on every computer."),
                _sceneRows,
                add,
            },
        });
    }

    private Control PreferencesTab() => Scroll(new StackPanel
    {
        Spacing = 14,
        Children =
        {
            Section("Pointer and system behavior", "These defaults are tuned for a responsive local network."),
            Field("Pointer speed on this secondary computer", _mouseScale, "1.0 keeps the operating system's normal scale."),
            _syncScreensaver,
            _screenLock,
            _hideCursor,
            _accelerateWheel,
        },
    });

    private string? LoadForm()
    {
        string? warning = null;
        if (File.Exists(_configPath))
        {
            try { _loaded = HydraConfigFile.Parse(File.ReadAllText(_configPath), _configPath); }
            catch (Exception ex) { warning = $"The existing settings could not be read; defaults are shown. Saving will replace them. {ex.Message}"; }
        }

        var first = _loaded?.Profiles.FirstOrDefault();
        _machineName.Text = _loaded?.Name ?? Environment.MachineName.Split('.')[0];
        _role.SelectedIndex = first?.Mode == Mode.Slave ? 1 : 0;
        _deskName.Text = first?.EmbeddedStyxServer?.DiscoveryName
            ?? DeskFromAutoUri(first?.EmbeddedStyx?.Server)
            ?? "my-desk";
        _password.Text = first?.EmbeddedStyxServer?.Password
            ?? first?.EmbeddedStyx?.Password
            ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _relayPort.Value = first?.EmbeddedStyxServer?.Port ?? 5000;
        _mouseScale.Value = first?.MouseScale ?? 1;
        _syncScreensaver.IsChecked = first?.SyncScreensaver ?? true;
        _screenLock.IsChecked = first?.ScreenLockPropagation ?? false;
        _hideCursor.IsChecked = first?.HideCursor ?? false;
        _accelerateWheel.IsChecked = first?.AccelerateMouseWheel ?? true;

        if (first?.Mode == Mode.Master)
        {
            foreach (var host in first.Hosts.Where(h => !h.Name.Equals(_machineName.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var edge = first.Hosts
                    .SelectMany(source => source.Neighbours.Where(n => n.Mirror && n.Name.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).Select(n => (source.Name, Neighbour: n)))
                    .FirstOrDefault();
                AddPeer(host.Name, string.IsNullOrWhiteSpace(edge.Name) ? LocalName() : edge.Name, edge.Neighbour?.Direction ?? Direction.Right);
            }
            foreach (var connected in _connectedPeers.Where(n => !n.Equals(LocalName(), StringComparison.OrdinalIgnoreCase) && _peers.All(p => !p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
                AddPeer(connected, LocalName(), Direction.Right);
        }

        if (_loaded?.Profiles.Count > 0)
        {
            foreach (var profile in _loaded.Profiles)
            {
                var power = profile.DisplayRouting.WakeDisplays ? DisplayPower.Wake
                    : profile.DisplayRouting.SleepDisplays ? DisplayPower.Sleep : DisplayPower.Keep;
                AddScene(new SceneFormData(
                    profile.ProfileName ?? "Default",
                    profile.DisplayRouting.Inputs.Select(i => new MonitorFormData(i.Id, i.Input)).ToList(),
                    power,
                    profile.DisplayRouting.SettleDelayMs));
            }
        }
        else AddScene(new SceneFormData("Default", [], DisplayPower.Keep, 500));

        return warning;
    }

    private async Task SaveAsync()
    {
        try
        {
            var file = BuildConfig();
            await NativeSettingsPersistence.SaveAsync(file, _configPath);
            SetStatus("Saved. Restarting ScreenFuse…");
            await Task.Delay(350);
            _restartAfterSave();
        }
        catch (Exception ex) { SetStatus($"Please fix the setup details: {ex.Message}"); }
    }

    private HydraConfigFile BuildConfig()
    {
        ValidateDeskFields();
        var local = LocalName();
        var master = _role.SelectedIndex != 1;
        var desk = Required(_deskName, "Desk name");
        var password = Required(_password, "Shared secret");
        var port = Decimal.ToInt32(_relayPort.Value ?? 5000);

        var peerNames = _peers.Select(p => p.Name).ToList();
        if (peerNames.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Every computer needs a name.");
        if (peerNames.Append(local).Distinct(StringComparer.OrdinalIgnoreCase).Count() != peerNames.Count + 1)
            throw new InvalidOperationException("Computer names must be unique.");

        var hosts = master ? BuildHosts(local, peerNames) : [];
        if (_scenes.Count == 0) throw new InvalidOperationException("Add at least one scene.");
        var sceneNames = _scenes.Select(s => s.SceneName).ToList();
        if (sceneNames.Any(string.IsNullOrWhiteSpace) || sceneNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sceneNames.Count)
            throw new InvalidOperationException("Every scene needs a unique name.");

        var profiles = _scenes.Select(scene => new HydraConfig
        {
            ProfileName = scene.SceneName,
            Mode = master ? Mode.Master : Mode.Slave,
            EmbeddedStyxServer = master ? new EmbeddedStyxServerConfig { Port = port, Password = password, DiscoveryName = desk } : null,
            EmbeddedStyx = master ? null : new EmbeddedStyxConfig { Server = $"auto://{desk}", Password = password },
            Hosts = master ? CloneHosts(hosts) : [],
            MouseScale = master ? null : _mouseScale.Value,
            SyncScreensaver = _syncScreensaver.IsChecked == true,
            ScreenLockPropagation = master && _screenLock.IsChecked == true,
            HideCursor = master && _hideCursor.IsChecked == true,
            AccelerateMouseWheel = _accelerateWheel.IsChecked == true,
            DisplayRouting = scene.BuildRouting(),
        }).ToList();

        return new HydraConfigFile
        {
            Name = local,
            ControlPort = _loaded?.ControlPort ?? 24801,
            LogLevel = _loaded?.LogLevel ?? LogLevel.Information,
            LogFile = _loaded?.LogFile,
            SessionLogFile = _loaded?.SessionLogFile,
            LockFile = _loaded?.LockFile,
            LogTruncate = _loaded?.LogTruncate ?? false,
            Profiles = profiles,
        };
    }

    private List<HostConfig> BuildHosts(string local, List<string> peerNames)
    {
        var all = peerNames.Prepend(local).ToDictionary(n => n, n => new HostConfig { Name = n }, StringComparer.OrdinalIgnoreCase);
        foreach (var peer in _peers)
        {
            var source = peer.SourceName;
            if (!all.TryGetValue(source, out var sourceHost))
                throw new InvalidOperationException($"'{peer.Name}' must be placed next to an existing computer.");
            if (source.Equals(peer.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{peer.Name}' cannot be next to itself.");
            sourceHost.Neighbours.Add(new NeighbourConfig { Name = peer.Name, Direction = peer.Direction, Mirror = true });
        }
        return all.Values.ToList();
    }

    private static List<HostConfig> CloneHosts(IEnumerable<HostConfig> hosts) => hosts.Select(h => new HostConfig
    {
        Name = h.Name,
        Neighbours = h.Neighbours.Select(n => new NeighbourConfig { Name = n.Name, Direction = n.Direction, Mirror = n.Mirror }).ToList(),
    }).ToList();

    private void ValidateDeskFields()
    {
        _ = Required(_machineName, "Computer name");
        var desk = Required(_deskName, "Desk name");
        if (desk.Length > 64) throw new InvalidOperationException("Desk name must be 64 characters or fewer.");
        if (Required(_password, "Shared secret").Length < 16) throw new InvalidOperationException("Shared secret must be at least 16 characters.");
    }

    private string BuildJoinCode()
        => NativeJoinCode.Encode(
            Required(_deskName, "Desk name"),
            Required(_password, "Shared secret"),
            _scenes.Select(s => s.SceneName).Where(n => !string.IsNullOrWhiteSpace(n)));

    private void ApplyJoinCode(string code)
    {
        var decoded = NativeJoinCode.Decode(code);
        _deskName.Text = decoded.Desk;
        _password.Text = decoded.Secret;
        if (decoded.Scenes.Count > 0)
        {
            _scenes.Clear();
            _sceneRows.Children.Clear();
            foreach (var name in decoded.Scenes) AddScene(new SceneFormData(name, [], DisplayPower.Keep, 500));
        }
        ValidateDeskFields();
    }

    private async Task DiagnosticsAsync()
    {
        try
        {
            var report = await _displayRouter.DoctorAsync();
            SetStatus(string.Join(Environment.NewLine, report.Select(r => $"{(r.Success ? "✓" : "✗")} {r.Command}: {r.Detail}")));
        }
        catch (Exception ex) { SetStatus($"Display diagnostics failed: {ex.Message}"); }
    }

    private Task InstallStartupAsync()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
            var start = new ProcessStartInfo(exe, "--install") { UseShellExecute = true };
            if (OperatingSystem.IsWindows()) start.Verb = "runas";
            _ = Process.Start(start);
            SetStatus("Startup installation launched.");
        }
        catch (Exception ex) { SetStatus($"Could not install startup entry: {ex.Message}"); }
        return Task.CompletedTask;
    }

    private void AddPeer(string name, string source, Direction direction)
    {
        PeerEditor? editor = null;
        editor = new PeerEditor(name, source, direction, () =>
        {
            _peers.Remove(editor!);
            _peerRows.Children.Remove(editor!.View);
            RefreshPeerSources();
        }, RefreshPeerSources);
        _peers.Add(editor);
        _peerRows.Children.Add(editor.View);
        RefreshPeerSources();
    }

    private void RefreshPeerSources()
    {
        var names = _peers.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Prepend(LocalName()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var peer in _peers) peer.SetSourceChoices(names.Where(n => !n.Equals(peer.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private void AddScene(SceneFormData data)
    {
        SceneEditor? editor = null;
        editor = new SceneEditor(data, () =>
        {
            _scenes.Remove(editor!);
            _sceneRows.Children.Remove(editor!.View);
        });
        _scenes.Add(editor);
        _sceneRows.Children.Add(editor.View);
    }

    private void UpdateRoleState()
    {
        var master = _role.SelectedIndex != 1;
        _peerRows.IsEnabled = master;
        if (_addPeerButton != null) _addPeerButton.IsEnabled = master;
        _relayPort.IsEnabled = master;
        _computerHint.Text = master
            ? "This computer owns the keyboard and mouse. Arrange every other computer below."
            : "Secondary computers receive their layout from the master; no local computer map is needed.";
        _screenLock.IsEnabled = master;
        _hideCursor.IsEnabled = master;
        _mouseScale.IsEnabled = !master;
    }

    private string LocalName() => (_machineName.Text ?? "").Trim();
    private static string Required(TextBox box, string label) => string.IsNullOrWhiteSpace(box.Text) ? throw new InvalidOperationException($"{label} is required.") : box.Text.Trim();
    private static string? DeskFromAutoUri(string? server) => server?.StartsWith("auto://", StringComparison.OrdinalIgnoreCase) == true ? server[7..] : null;

    private static Border Field(string label, Control control, string? help = null)
    {
        var stack = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, control } };
        if (help != null) stack.Children.Add(Hint(help));
        return new Border { Child = stack, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static StackPanel Section(string title, string description) => new()
    {
        Spacing = 3,
        Margin = new Thickness(0, 8, 0, 0),
        Children = { new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold }, Hint(description) },
    };

    private static TextBlock Hint(string text) => new() { Text = text, Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, Padding = new Thickness(12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private static TabItem Tab(string header, Control content) => new() { Header = header, Content = content };
    private static ComboBox Choice(params string[] choices) => new() { ItemsSource = choices, SelectedIndex = 0, MinWidth = 260 };
    private static NumericUpDown Number(decimal value, decimal min, decimal max, decimal increment = 1) => new() { Value = value, Minimum = min, Maximum = max, Increment = increment, MinWidth = 140 };
    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static TextBlock HeaderCol(string text, int column)
    {
        var block = Hint(text);
        Grid.SetColumn(block, column);
        return block;
    }

    private static Button Button(string label, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = label };
        if (accent) button.Classes.Add("accent");
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) when (TopLevel.GetTopLevel(button) is SettingsWindow window) { window.SetStatus(ex.Message); }
        };
        return button;
    }
}

internal static class NativeSettingsPersistence
{
    internal static string SerializeAndValidate(HydraConfigFile file, string path)
    {
        var options = new JsonSerializerOptions(SaneJson.Options) { WriteIndented = true };
        var json = JsonSerializer.Serialize(file, options);
        _ = HydraConfigFile.Parse(json, path);
        return json;
    }

    internal static async Task SaveAsync(HydraConfigFile file, string path)
    {
        var json = SerializeAndValidate(file, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json + Environment.NewLine, new UTF8Encoding(false));
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temp, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, privateMode);
            if (File.Exists(path + ".bak")) File.SetUnixFileMode(path + ".bak", privateMode);
        }
    }
}

internal record JoinCodeData(string Desk, string Secret, IReadOnlyList<string> Scenes);

internal static class NativeJoinCode
{
    private const string Prefix = "screenfuse-join:";

    internal static string Encode(string desk, string secret, IEnumerable<string> scenes) =>
        $"{Prefix}{Uri.EscapeDataString(desk)}:{Uri.EscapeDataString(secret)}:{string.Join(',', scenes.Select(Uri.EscapeDataString))}";

    internal static JoinCodeData Decode(string code)
    {
        if (!code.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The clipboard does not contain a ScreenFuse join code.");
        var parts = code.Trim()[Prefix.Length..].Split(':', 3);
        if (parts.Length < 2) throw new InvalidOperationException("The ScreenFuse join code is incomplete.");
        var scenes = parts.Length == 3
            ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.UnescapeDataString).ToArray()
            : [];
        return new(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), scenes);
    }
}

internal sealed class PeerEditor
{
    private readonly TextBox _name;
    private readonly ComboBox _source;
    private readonly ComboBox _direction = new() { ItemsSource = Enum.GetNames<Direction>(), MinWidth = 100 };
    internal Control View { get; }
    internal string Name => (_name.Text ?? "").Trim();
    internal string SourceName => (_source.SelectedItem as string ?? "").Trim();
    internal Direction Direction => Enum.TryParse<Direction>(_direction.SelectedItem as string, out var value) ? value : Direction.Right;

    internal PeerEditor(string name, string source, Direction direction, Action remove, Action changed)
    {
        _name = new TextBox { Text = name, PlaceholderText = "Computer name" };
        _source = new ComboBox { MinWidth = 130 };
        _source.Tag = source;
        _direction.SelectedItem = direction.ToString();
        _name.TextChanged += (_, _) => changed();
        var removeButton = new Button { Content = "Remove" };
        removeButton.Click += (_, _) => remove();
        View = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,2*,*,Auto"),
            ColumnSpacing = 8,
            Children = { Col(_name, 0), Col(_source, 1), Col(_direction, 2), Col(removeButton, 3) },
        };
    }

    internal void SetSourceChoices(IEnumerable<string> names)
    {
        var selected = _source.SelectedItem as string ?? _source.Tag as string;
        var choices = names.ToList();
        _source.ItemsSource = choices;
        _source.SelectedItem = choices.FirstOrDefault(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        _source.Tag = null;
    }

    private static T Col<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}

internal enum DisplayPower { Keep, Wake, Sleep }
internal record MonitorFormData(string Id, int Input);
internal record SceneFormData(string Name, List<MonitorFormData> Monitors, DisplayPower Power, int SettleDelayMs);

internal sealed class SceneEditor
{
    private readonly TextBox _name;
    private readonly ComboBox _power = new() { ItemsSource = new[] { "Keep displays active", "Wake displays", "Sleep displays" }, SelectedIndex = 0, MinWidth = 190 };
    private readonly NumericUpDown _settle = new() { Minimum = 0, Maximum = 10000, Increment = 100, Value = 500, MinWidth = 110 };
    private readonly StackPanel _routes = new() { Spacing = 7 };
    private readonly List<MonitorRouteEditor> _monitors = [];
    internal Control View { get; }
    internal string SceneName => (_name.Text ?? "").Trim();

    internal SceneEditor(SceneFormData data, Action remove)
    {
        _name = new TextBox { Text = data.Name, PlaceholderText = "Scene name", MinWidth = 200 };
        _power.SelectedIndex = data.Power switch { DisplayPower.Wake => 1, DisplayPower.Sleep => 2, _ => 0 };
        _settle.Value = data.SettleDelayMs;
        foreach (var monitor in data.Monitors) AddMonitor(monitor);

        var addMonitor = new Button { Content = "Add monitor route" };
        addMonitor.Click += (_, _) => AddMonitor(new MonitorFormData("*", 15));
        var removeScene = new Button { Content = "Remove scene" };
        removeScene.Click += (_, _) => remove();

        View = new Border
        {
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("2*,2*,*,Auto"), ColumnSpacing = 8,
                        Children = { Col(Labeled("Scene name", _name), 0), Col(Labeled("Display action", _power), 1), Col(Labeled("Settle delay (ms)", _settle), 2), Col(removeScene, 3) },
                    },
                    SettingsWindowHint("Monitor ID accepts the identifier shown by Display diagnostics. Common input values: DisplayPort 1 = 15, HDMI 1 = 17, HDMI 2 = 18, USB-C = 27."),
                    _routes,
                    addMonitor,
                },
            },
        };
    }

    internal DisplayRoutingConfig BuildRouting()
    {
        var inputs = _monitors.Select(m => m.Build()).ToList();
        return new DisplayRoutingConfig
        {
            Inputs = inputs,
            WakeDisplays = _power.SelectedIndex == 1,
            SleepDisplays = _power.SelectedIndex == 2,
            SettleDelayMs = Decimal.ToInt32(_settle.Value ?? 500),
        };
    }

    private void AddMonitor(MonitorFormData data)
    {
        MonitorRouteEditor? editor = null;
        editor = new MonitorRouteEditor(data, () =>
        {
            _monitors.Remove(editor!);
            _routes.Children.Remove(editor!.View);
        });
        _monitors.Add(editor);
        _routes.Children.Add(editor.View);
    }

    private static TextBlock SettingsWindowHint(string text) => new() { Text = text, Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Labeled(string label, Control control) => new() { Spacing = 3, Children = { SettingsWindowHint(label), control } };
    private static T Col<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}

internal sealed class MonitorRouteEditor
{
    private readonly TextBox _id;
    private readonly ComboBox _preset;
    private readonly NumericUpDown _custom;
    internal Control View { get; }

    internal MonitorRouteEditor(MonitorFormData data, Action remove)
    {
        _id = new TextBox { Text = data.Id, PlaceholderText = "Monitor identifier" };
        _preset = new ComboBox
        {
            ItemsSource = new[] { "DisplayPort 1 (15)", "DisplayPort 2 (16)", "HDMI 1 (17)", "HDMI 2 (18)", "USB-C (27)", "Custom" },
            MinWidth = 170,
        };
        _preset.SelectedIndex = data.Input switch { 15 => 0, 16 => 1, 17 => 2, 18 => 3, 27 => 4, _ => 5 };
        _custom = new NumericUpDown { Value = data.Input, Minimum = 0, Maximum = 255, Increment = 1, MinWidth = 100, IsEnabled = _preset.SelectedIndex == 5 };
        _preset.SelectionChanged += (_, _) => _custom.IsEnabled = _preset.SelectedIndex == 5;
        var removeButton = new Button { Content = "Remove" };
        removeButton.Click += (_, _) => remove();
        View = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("3*,2*,*,Auto"), ColumnSpacing = 8,
            Children = { Col(Labeled("Monitor", _id), 0), Col(Labeled("Input", _preset), 1), Col(Labeled("Custom value", _custom), 2), Col(removeButton, 3) },
        };
    }

    internal MonitorInputConfig Build()
    {
        var id = (_id.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Monitor identifier is required.");
        var input = _preset.SelectedIndex switch { 0 => 15, 1 => 16, 2 => 17, 3 => 18, 4 => 27, _ => Decimal.ToInt32(_custom.Value ?? 0) };
        return new MonitorInputConfig { Id = id, Input = input };
    }

    private static StackPanel Labeled(string label, Control control) => new() { Spacing = 3, Children = { new TextBlock { Text = label, Opacity = 0.72 }, control } };
    private static T Col<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
