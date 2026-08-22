using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Hydra.Config;
using Hydra.Display;
using Hydra.Platform;
using Hydra.Scenes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hydra.Tray;

internal sealed class TrayApplication : Application
{
    private static IServiceProvider? _services;
    private static string _configPath = "";
    private static bool _setupOnly;
    private static bool _onboarding;
    private static string? _initialStatus;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private OnboardingWindow? _onboardingWindow;
    private DispatcherTimer? _statusTimer;
    private NativeMenuItem? _controlMenu;
    private Hydra.Desk.IDeskService? _desk;
    private string _controlSignature = "";

    internal static void Run(IServiceProvider? services, string configPath, bool setupOnly = false, string? initialStatus = null, bool onboarding = false)
    {
        _services = services;
        _configPath = configPath;
        _setupOnly = setupOnly;
        _onboarding = onboarding;
        _initialStatus = initialStatus;
        Build().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }

    internal static void RequestShutdown() => Dispatcher.UIThread.Post(() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    // ShowInDock=false is what actually keeps ScreenFuse out of the macOS Dock and app switcher.
    // LSUIElement in Info.plist only applies when the binary is launched as a bundle, which is not
    // the case for a plain unzip or a launchd job pointing straight at the executable.
    private static AppBuilder Build() => AppBuilder.Configure<TrayApplication>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var menu = new NativeMenu();
        var coordinator = _services?.GetService<ISceneCoordinator>();
        var profile = _services?.GetService<IHydraProfile>();
        var desk = _services?.GetService<Hydra.Desk.IDeskService>();
        if (coordinator != null)
        {
            var peerStatus = new NativeMenuItem(PeerSummary(coordinator)) { IsEnabled = false };
            menu.Add(peerStatus);
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                peerStatus.Header = PeerSummary(coordinator);
                if (_tray != null)
                    _tray.ToolTipText = $"ScreenFuse — {coordinator.CurrentScene ?? "automatic"} — {PeerSummary(coordinator)}";
                RefreshControlMenu();
            };
            _statusTimer.Start();
            desktop.Exit += (_, _) => _statusTimer?.Stop();
            menu.Add(new NativeMenuItemSeparator());
            foreach (var scene in coordinator.AvailableScenes)
            {
                var item = new NativeMenuItem(scene)
                {
                    IsChecked = scene.Equals(coordinator.CurrentScene, StringComparison.OrdinalIgnoreCase),
                    IsEnabled = profile?.Mode != Mode.Slave,
                };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        var result = await coordinator.ActivateAsync(scene);
                        if (!result.Accepted) ShowSettings(result.Message);
                    }
                    catch (Exception ex) { ShowSettings($"Could not activate scene: {ex.Message}"); }
                };
                menu.Add(item);
            }
            menu.Add(new NativeMenuItemSeparator());
        }

        // Taking the keyboard back has to be reachable from the computer you are actually sitting
        // at, in one click — that is the whole point of the role being switchable.
        if (desk != null)
        {
            var control = new NativeMenuItem("Keyboard and mouse") { Menu = new NativeMenu() };
            menu.Add(control);
            _controlMenu = control;
            _desk = desk;
            RefreshControlMenu();
            menu.Add(new NativeMenuItemSeparator());
        }

        var settings = new NativeMenuItem("Advanced settings…");
        settings.Click += (_, _) => ShowSettings();
        menu.Add(settings);
        var doctor = new NativeMenuItem("Display diagnostics…");
        doctor.Click += async (_, _) =>
        {
            var router = _services?.GetService<IDisplayRouter>() ?? new DisplayRouter(Microsoft.Extensions.Logging.Abstractions.NullLogger<DisplayRouter>.Instance);
            var report = await router.DoctorAsync();
            ShowSettings(string.Join(Environment.NewLine, report.Select(r => $"{(r.Success ? "✓" : "✗")} {r.Command}: {r.Detail}")));
        };
        menu.Add(doctor);
        var startup = new NativeMenuItem("Launch on startup");
        startup.Click += (_, _) =>
        {
            try
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
                var start = new ProcessStartInfo(exe, "--install") { UseShellExecute = true };
                if (OperatingSystem.IsWindows()) start.Verb = "runas";
                _ = Process.Start(start);
            }
            catch (Exception ex) { ShowSettings($"Could not install startup entry: {ex.Message}"); }
        };
        menu.Add(startup);
        menu.Add(new NativeMenuItemSeparator());
        var restart = new NativeMenuItem("Restart ScreenFuse");
        restart.Click += (_, _) => ProcessRestart.Restart("requested from the tray menu");
        menu.Add(restart);
        var quit = new NativeMenuItem("Quit ScreenFuse");
        quit.Click += (_, _) =>
        {
            _tray!.IsVisible = false;
            // On macOS the launch agent owns this process. Exiting without telling launchd just
            // hands it a dead job to restart, which is why Quit used to put the icon straight back.
            if (OperatingSystem.IsMacOS()) Platform.MacOs.AgentCommands.StopAgent();
            _services?.GetService<IHostApplicationLifetime>()?.StopApplication();
            desktop.Shutdown();
        };
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = TrayIconImage.Create(),
            ToolTipText = coordinator == null ? "ScreenFuse setup" : $"ScreenFuse — {coordinator.CurrentScene ?? "automatic"}",
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        if (_setupOnly)
        {
            if (_onboarding) ShowOnboarding();
            else ShowSettings(_initialStatus);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // The desk only settles once the peers report in, so the list is rebuilt when it actually
    // changes rather than on every tick — recreating native menu items under an open menu is what
    // makes tray menus flicker and drop clicks.
    private void RefreshControlMenu()
    {
        if (_controlMenu?.Menu is not { } items || _desk == null) return;
        var snapshot = _desk.Snapshot;
        var signature = $"{snapshot.Controller}|{string.Join(',', snapshot.Hosts)}|{string.Join(',', snapshot.ConnectedHosts)}";
        if (signature == _controlSignature) return;
        _controlSignature = signature;

        items.Items.Clear();
        foreach (var host in snapshot.Hosts)
        {
            var isController = host.Equals(snapshot.Controller, StringComparison.OrdinalIgnoreCase);
            var reachable = isController
                || host.Equals(snapshot.LocalHost, StringComparison.OrdinalIgnoreCase)
                || snapshot.ConnectedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
            var item = new NativeMenuItem(host == snapshot.LocalHost ? $"{host} (this computer)" : host)
            {
                IsChecked = isController,
                IsEnabled = reachable && !isController,
            };
            var target = host;
            item.Click += async (_, _) =>
            {
                var result = await _desk.SetControllerAsync(target);
                if (!result.Accepted) ShowSettings(result.Message);
            };
            items.Items.Add(item);
        }
    }

    // A desk with no configured peers has nothing to count against, so a bare "1/0" is worse than
    // saying what is actually true. Only show the fraction once the desk expects specific computers.
    private static string PeerSummary(ISceneCoordinator coordinator)
    {
        var connected = coordinator.ConnectedPeers.Count;
        if (coordinator.ExpectedPeers.Count > 0)
            return $"Peers: {connected}/{coordinator.ExpectedPeers.Count} connected";
        return connected switch
        {
            0 => "No other computer connected",
            1 => "1 computer connected",
            _ => $"{connected} computers connected",
        };
    }

    private void ShowSettings(string? message = null)
    {
        _settings ??= new SettingsWindow(
            _configPath,
            _services?.GetService<IDisplayRouter>(),
            _services?.GetService<Hydra.Desk.IDeskService>(),
            message,
            RestartAfterSave);
        if (message != null) _settings.SetStatus(message);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void ShowOnboarding()
    {
        _onboardingWindow ??= new OnboardingWindow(_configPath, RestartAfterSave, () => ShowSettings());
        _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        _onboardingWindow.Show();
        _onboardingWindow.Activate();
    }

    private void RestartAfterSave()
    {
        if (!_setupOnly)
        {
            ProcessRestart.Restart("settings saved");
            return;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

internal static class TrayIconImage
{
    internal static WindowIcon Create() => new(new MemoryStream(CreatePng()));

    internal static byte[] CreatePng()
    {
        const int size = 32;
        var raw = new byte[(size * 4 + 1) * size];
        for (var y = 0; y < size; y++)
        {
            var row = y * (size * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < size; x++)
            {
                var i = row + 1 + x * 4;
                var left = x is >= 3 and <= 20 && y is >= 5 and <= 22;
                var right = x is >= 11 and <= 28 && y is >= 10 and <= 27;
                var border = (left && (x is 3 or 20 || y is 5 or 22)) || (right && (x is 11 or 28 || y is 10 or 27));
                raw[i] = border ? (byte)88 : (byte)20;
                raw[i + 1] = border ? (byte)166 : (byte)32;
                raw[i + 2] = border ? (byte)255 : (byte)48;
                raw[i + 3] = left || right ? (byte)255 : (byte)0;
            }
        }
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        Chunk(output, "IHDR", [0,0,0,size,0,0,0,size,8,6,0,0,0]);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string name, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type); output.Write(data);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput, 0); data.CopyTo(crcInput, type.Length);
        var checksum = ComputePngCrc(crcInput);
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, checksum);
        output.Write(crc);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return crc ^ 0xffffffff;
    }
}
