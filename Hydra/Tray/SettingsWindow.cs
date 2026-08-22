using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cathedral.Config;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Desk;
using Hydra.Display;
using Microsoft.Extensions.Logging;

namespace Hydra.Tray;

internal sealed class SettingsWindow : Window
{
    private readonly string _configPath;
    private readonly IDisplayRouter _displayRouter;
    private readonly Action _restartAfterSave;
    private readonly IDeskService? _desk;
    private readonly TextBlock _status;
    private readonly TextBox _machineName = new() { PlaceholderText = "This computer's name" };
    private readonly ComboBox _role = Choice("Start a new desk on this computer", "Join a desk that already exists");
    private readonly TextBox _deskName = new() { PlaceholderText = "e.g. studio" };
    private readonly TextBox _password = new() { PlaceholderText = "Shared secret (16+ characters)", PasswordChar = '●' };
    private readonly NumericUpDown _relayPort = Number(5000, 1024, 65535);
    private readonly NumericUpDown _mouseScale = Number(1, 0.1m, 10, 0.1m);
    private readonly CheckBox _syncScreensaver = new() { Content = "Keep screen savers in sync", IsChecked = true };
    private readonly CheckBox _screenLock = new() { Content = "Lock the other computers when this computer locks" };
    private readonly CheckBox _hideCursor = new() { Content = "Hide an idle cursor", IsChecked = false };
    private readonly CheckBox _accelerateWheel = new() { Content = "Smooth accelerated scrolling", IsChecked = true };
    private HydraConfigFile? _loaded;

    internal SettingsWindow(string configPath, IDisplayRouter? displayRouter, IDeskService? desk, string? initialStatus, Action restartAfterSave)
    {
        _configPath = Path.GetFullPath(configPath);
        _displayRouter = displayRouter ?? new DisplayRouter(Microsoft.Extensions.Logging.Abstractions.NullLogger<DisplayRouter>.Instance);
        _desk = desk;
        _restartAfterSave = restartAfterSave;
        Title = "ScreenFuse Settings";
        Width = 980;
        Height = 780;
        MinWidth = 760;
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
        UpdateRoleState();

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                Tab("Monitors", MonitorsTab()),
                Tab("Connection", ConnectionTab()),
                Tab("Preferences", PreferencesTab()),
            },
        };

        var save = Action("Save and restart", SaveAsync, accent: true);
        var diagnostics = Action("Display diagnostics", DiagnosticsAsync);
        var startup = Action("Launch on startup", InstallStartupAsync);
        var close = Action("Close", () => { Close(); return Task.CompletedTask; });

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

    // The desk itself is live: it is driven by the desk service, not by this form, so it needs
    // ScreenFuse to be running. During first-time setup there is nothing to arrange yet.
    private Control MonitorsTab() => _desk != null
        ? new DeskPanel(_desk, SetStatus)
        : Scroll(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("Your desk", "Fill in the connection details, save, and ScreenFuse will show every monitor on the desk here — including the ones attached to the other computers."),
            },
        });

    private Control ConnectionTab()
    {
        var copyCode = Action("Copy join code", async () =>
        {
            ValidateDeskFields();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(BuildJoinCode()));
            await clipboard.SetDataAsync(transfer);
            SetStatus("Join code copied. Paste it into ScreenFuse on the other computer.");
        });
        var pasteCode = Action("Paste join code", async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
            var code = await clipboard.TryGetTextAsync() ?? "";
            ApplyJoinCode(code);
            _role.SelectedIndex = 1;
            SetStatus("Join code applied. Save, and this computer will appear on the desk.");
        });

        return Scroll(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("This computer", "Give every computer a short, unique name."),
                Field("Computer name", _machineName),
                Field("Is this the first computer on the desk?", _role,
                    "The first computer runs the connection the others join. It keeps running it even after you hand the keyboard to another computer."),
                Section("Local desk connection", "ScreenFuse finds the desk automatically on the LAN. The desk name and secret must match on every computer."),
                Field("Desk name", _deskName),
                Field("Shared secret", _password, "Generated automatically for a new desk. Use the join code instead of retyping it."),
                Field("Relay port", _relayPort, "The default works on most home and office LANs."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyCode, pasteCode } },
            },
        });
    }

    private Control PreferencesTab() => Scroll(new StackPanel
    {
        Spacing = 14,
        Children =
        {
            Section("Pointer and system behavior", "These defaults are tuned for a responsive local network."),
            Field("Pointer speed when another computer drives this one", _mouseScale, "1.0 keeps the operating system's normal scale."),
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
        _role.SelectedIndex = first?.EmbeddedStyxServer == null && first?.EmbeddedStyx != null ? 1 : 0;
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

    // Only this form's fields are rewritten. The desk — the monitor table, the arrangement, each
    // scene's assignments and controller — is owned by the Monitors tab and carried across
    // untouched, so saving a changed password can never quietly flatten the desk.
    private HydraConfigFile BuildConfig()
    {
        ValidateDeskFields();
        var local = LocalName();
        var hostsDesk = _role.SelectedIndex != 1;
        var desk = Required(_deskName, "Desk name");
        var password = Required(_password, "Shared secret");
        var port = Decimal.ToInt32(_relayPort.Value ?? 5000);

        var existing = _loaded?.Profiles.Count > 0
            ? _loaded.Profiles
            : [new HydraConfig { Mode = hostsDesk ? Mode.Master : Mode.Slave, ProfileName = "Default" }];

        var profiles = existing.Select(profile => new HydraConfig
        {
            // preserved — the desk
            ProfileName = profile.ProfileName ?? "Default",
            Controller = profile.Controller,
            Hosts = profile.Hosts,
            DisplayRouting = profile.DisplayRouting,
            Conditions = profile.Conditions,
            ScreenDefinitions = profile.ScreenDefinitions,
            RemoteOnly = profile.RemoteOnly,
            UnicodeKeyRepeat = profile.UnicodeKeyRepeat,
            DeadCorners = profile.DeadCorners,

            // from this form
            Mode = profile.Controller != null ? profile.Mode : hostsDesk ? Mode.Master : Mode.Slave,
            EmbeddedStyxServer = hostsDesk ? new EmbeddedStyxServerConfig { Port = port, Password = password, DiscoveryName = desk } : null,
            EmbeddedStyx = hostsDesk ? null : new EmbeddedStyxConfig { Server = $"auto://{desk}", Password = password },
            MouseScale = _mouseScale.Value,
            SyncScreensaver = _syncScreensaver.IsChecked == true,
            ScreenLockPropagation = _screenLock.IsChecked == true,
            HideCursor = _hideCursor.IsChecked == true,
            AccelerateMouseWheel = _accelerateWheel.IsChecked == true,
        }).ToList();

        // mouseScale, hideCursor and screenLockPropagation are role-specific. A desk with a
        // controller shares one document and ignores the ones that do not apply; a desk without one
        // still validates strictly, so drop them for the role this machine has.
        if (profiles.All(p => p.Controller == null))
            profiles = profiles.Select(p => p.Mode == Mode.Master
                ? Strip(p, mouseScale: true)
                : Strip(p, mouseScale: false)).ToList();

        return new HydraConfigFile
        {
            Name = local,
            ControlPort = _loaded?.ControlPort ?? 24801,
            LogLevel = _loaded?.LogLevel ?? LogLevel.Information,
            LogFile = _loaded?.LogFile,
            SessionLogFile = _loaded?.SessionLogFile,
            LockFile = _loaded?.LockFile,
            LogTruncate = _loaded?.LogTruncate ?? false,
            Monitors = _loaded?.Monitors ?? [],
            Profiles = profiles,
        };
    }

    private static HydraConfig Strip(HydraConfig profile, bool mouseScale) => new()
    {
        ProfileName = profile.ProfileName,
        Controller = profile.Controller,
        Mode = profile.Mode,
        Hosts = profile.Hosts,
        DisplayRouting = profile.DisplayRouting,
        Conditions = profile.Conditions,
        ScreenDefinitions = mouseScale ? [] : profile.ScreenDefinitions,
        RemoteOnly = profile.RemoteOnly,
        UnicodeKeyRepeat = profile.UnicodeKeyRepeat,
        DeadCorners = profile.DeadCorners,
        EmbeddedStyx = profile.EmbeddedStyx,
        EmbeddedStyxServer = profile.EmbeddedStyxServer,
        NetworkConfig = profile.NetworkConfig,
        MouseScale = mouseScale ? null : profile.MouseScale,
        SyncScreensaver = profile.SyncScreensaver,
        ScreenLockPropagation = mouseScale && profile.ScreenLockPropagation,
        HideCursor = mouseScale && profile.HideCursor,
        AccelerateMouseWheel = profile.AccelerateMouseWheel,
    };

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
            (_loaded?.Profiles ?? []).Select(p => p.ProfileName).Where(n => !string.IsNullOrWhiteSpace(n))!);

    private void ApplyJoinCode(string code)
    {
        var decoded = NativeJoinCode.Decode(code);
        _deskName.Text = decoded.Desk;
        _password.Text = decoded.Secret;
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

    private void UpdateRoleState() => _relayPort.IsEnabled = _role.SelectedIndex != 1;

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
    private static ComboBox Choice(params string[] choices) => NoWheel(new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 300 });
    private static NumericUpDown Number(decimal value, decimal min, decimal max, decimal increment = 1) => NoWheel(new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = increment, MinWidth = 140 });

    // Stops the mouse wheel changing a value.
    //
    // A ComboBox and a NumericUpDown both take the wheel as input, so scrolling the settings page
    // with the pointer anywhere over one silently changes it. On this desk that is not a harmless
    // edit — a monitor's picker applies immediately, so scrolling past it switches a real monitor to
    // another computer. The wheel is taken before the control sees it and given to the page, which
    // is what the user was reaching for.
    internal static T NoWheel<T>(T control) where T : Control
    {
        control.AddHandler(
            InputElement.PointerWheelChangedEvent,
            (_, e) =>
            {
                e.Handled = true;
                if (control.FindAncestorOfType<ScrollViewer>() is not { } scroller) return;
                var reach = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y - e.Delta.Y * 50, 0, reach));
            },
            RoutingStrategies.Tunnel);
        return control;
    }
    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }

    internal static Button Action(string label, Func<Task> action, bool accent = false)
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
