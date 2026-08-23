using Common;
using System.Text;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Desk;
using Hydra.Display;
using Hydra.Discovery;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.MacOs;
using Hydra.Platform.Windows;
using Hydra.Relay;
using Hydra.Screen;
using Hydra.Scenes;
using Hydra.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Command-line runs need the console that launched them; the tray agent has none by design.
if (args.Any(a => a is "--doctor" or "--scene" or "--install" or "--uninstall" or "--version"))
    ConsoleAttach.ToParent();

// ensure console can display non-ASCII characters (e.g. '€', 'ø') in debug logs
try { Console.OutputEncoding = Encoding.UTF8; }
catch (IOException) { /* no console attached — nothing to configure */ }

// catch unhandled exceptions on any thread before they silently kill the process
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
    // restore system cursors in case we crash while they're blanked
    if (OperatingSystem.IsWindows())
        WindowsCursorSnapshot.RestoreDefaults();
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

if (args.Contains("--install"))
{
    if (OperatingSystem.IsWindows()) ServiceCommands.Install();
    else if (OperatingSystem.IsMacOS()) AgentCommands.Install();
    else if (OperatingSystem.IsLinux()) LinuxServiceCommands.Install();
    return;
}
if (args.Contains("--uninstall"))
{
    if (OperatingSystem.IsWindows()) ServiceCommands.Uninstall();
    else if (OperatingSystem.IsMacOS()) AgentCommands.Uninstall();
    else if (OperatingSystem.IsLinux()) LinuxServiceCommands.Uninstall();
    return;
}

var controlPortArg = ReadIntOption(args, "--port") ?? 24801;
var defaultConfigPath = Environment.GetEnvironmentVariable("CONFIG")
    ?? HydraConfigFile.DefaultPath();
if (args.Contains("--setup"))
{
    var setupPath = defaultConfigPath;
    var needsOnboarding = !File.Exists(setupPath);
    string? setupError = null;
    try
    {
        (_, setupPath) = HydraConfigFile.LoadAll(Env.Config);
        needsOnboarding = false;
    }
    catch (FileNotFoundException) { }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
    {
        needsOnboarding = false;
        setupError = $"ScreenFuse needs a valid configuration: {ex.Message}";
    }
    TrayApplication.Run(null, setupPath, setupOnly: true, initialStatus: setupError, onboarding: needsOnboarding);
    return;
}
// Stops ScreenFuse from the command line, including instances that are not this one. ScreenFuse
// runs as a background agent with no Dock icon, so it never appears in the macOS Force Quit list —
// leaving no obvious way to stop it when something is wrong.
if (args.Contains("--quit"))
{
    ProcessRestart.PreventRestarts();
    if (OperatingSystem.IsMacOS()) AgentCommands.StopAgent();
    if (OperatingSystem.IsWindows() && ServiceCommands.IsInstalled())
    {
        // Ending the process is not enough: the service is registered to restart five seconds after
        // any failure, and being killed is one.
        Console.WriteLine(ServiceCommands.StopService()
            ? "Stopped the ScreenFuse service."
            : "Could not stop the ScreenFuse service — run this from an Administrator prompt, or it will start itself again in a few seconds.");
    }
    var stopped = 0;
    foreach (var other in System.Diagnostics.Process.GetProcesses())
    {
        try
        {
            if (other.Id == Environment.ProcessId) continue;
            if (!other.ProcessName.Contains("screenfuse", StringComparison.OrdinalIgnoreCase)) continue;
            other.Kill(entireProcessTree: true);
            stopped++;
        }
        catch (Exception) { /* already gone, or not ours to stop */ }
    }
    Console.WriteLine(stopped == 0
        ? "No other ScreenFuse process was running."
        : $"Stopped {stopped} ScreenFuse process(es).");
    return;
}

// Puts the settings back to nothing, keeping a copy. A desk that has gone wrong is otherwise very
// hard to tell apart from a bug, because the stored desk is exactly what the next start builds on.
if (args.Contains("--reset"))
{
    var resetPath = Environment.GetEnvironmentVariable("CONFIG") ?? HydraConfigFile.DefaultPath();
    var directory = Path.GetDirectoryName(resetPath)!;
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var removed = new List<string>();

    foreach (var name in new[] { Path.GetFileName(resetPath), ".screenfuse-scene", ".screenfuse-controller" })
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) continue;
        File.Move(path, Path.Combine(directory, $"{name}.{stamp}.bak"), overwrite: true);
        removed.Add(name);
    }

    Console.WriteLine(removed.Count == 0
        ? $"Nothing to reset — no settings found in {directory}."
        : $"Reset {string.Join(", ", removed)}. A copy of each was kept as <name>.{stamp}.bak in {directory}.");
    Console.WriteLine("Start ScreenFuse again to set the desk up from scratch.");
    return;
}

if (args.Contains("--doctor"))
{
    var report = await new DisplayRouter(NullLogger<DisplayRouter>.Instance).DoctorAsync();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}
var sceneArg = ReadStringOption(args, "--scene");
if (sceneArg != null)
{
    await ActivateSceneFromCli(sceneArg, controlPortArg);
    return;
}

if (OperatingSystem.IsWindows())
{
    if (args.Contains("--service")) { ServiceHost.Run(args); return; }
    if (args.Contains("--session"))
        RunMode.IsSessionChild = true;
}

HydraConfigFile configFile;
List<HydraConfig> profiles;
string configPath;
string? lastConfigError = null;
while (true)
{
    try
    {
        (configFile, configPath) = HydraConfigFile.LoadAll(Env.Config);
        profiles = configFile.Profiles;
        break;
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
    {
        var graphicalSession = !OperatingSystem.IsLinux()
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        if (graphicalSession)
        {
            TrayApplication.Run(null, defaultConfigPath, setupOnly: true,
                initialStatus: $"ScreenFuse needs a valid configuration: {ex.Message}",
                onboarding: ex is FileNotFoundException);
            return;
        }

        // don't hard-exit on a missing/invalid config: under launchd/service KeepAlive that turns into a
        // ~5s relaunch storm that spams the redirect logs forever. Stay alive and retry so a corrected
        // config is picked up automatically. Log the message once (and again only if it changes).
        if (ex.Message != lastConfigError)
        {
            Console.Error.WriteLine(ex.Message);
            lastConfigError = ex.Message;
        }
        Sync.Wait(Task.Delay(TimeSpan.FromSeconds(30)));
    }
}

// acquire process lock if configured — prevents two instances from running with the same config
ProcessLock? processLock = null;
if (configFile.LockFile is { } lockFileSetting)
{
    var lockPath = Path.IsPathRooted(lockFileSetting)
        ? lockFileSetting
        : Path.GetFullPath(lockFileSetting, Path.GetDirectoryName(configPath)!);
    try
    {
        processLock = ProcessLock.Acquire(lockPath);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return;
    }
}

// on macOS, pre-start the shield before DI so network state is available for config resolution.
// the shield (NSApplication) activates WiFi/location on demand when "wifi" is sent via stdin.
MacNetworkState? macNetworkState = null;
MacShieldProcess? macShield = null;
if (OperatingSystem.IsMacOS())
{
    var needsWifi = HydraConfig.HasSsidConditions(profiles);
    macNetworkState = new MacNetworkState();
    macShield = new MacShieldProcess(macNetworkState, needsWifi);
    Sync.Wait(macShield.WaitForInitialState(TimeSpan.FromSeconds(3)));
}

var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
var services = builder.Services;

services.AddEnvironmentConfiguration();

// detect current network/screens and resolve which profile to use
var detector = Sync.Wait(CreateDetector(macNetworkState, services));
HydraConfig? config;
if (configFile.Profile != null)
    config = HydraConfig.Resolve(profiles, new ConditionState([], 1), configFile.Profile);
else if (!HydraConfig.HasConditions(profiles))
    config = profiles[0]; // single unconditional profile — no detection needed
else
{
    var activeSsids = (HydraConfig.HasSsidConditions(profiles) ? Sync.Wait(detector.GetActiveSsids()) : []) ?? [];
    var screenCount = HydraConfig.HasScreenCountConditions(profiles) ? GetScreenCount() : 1;
    var isPluggedIn = HydraConfig.HasPluggedInConditions(profiles) ? Sync.Wait(detector.GetIsPluggedIn()) : null;
    config = HydraConfig.Resolve(profiles, new ConditionState(activeSsids, screenCount, isPluggedIn));
}

var sceneStore = new SceneOverrideStore(configPath);
var selectedScene = configFile.Profile ?? sceneStore.Read();
if (selectedScene != null)
{
    var selected = profiles.FirstOrDefault(p => p.ProfileName?.Equals(selectedScene, StringComparison.OrdinalIgnoreCase) == true);
    if (selected != null)
        config = selected;
    else if (configFile.Profile == null)
        Console.Error.WriteLine($"Ignoring stale ScreenFuse scene '{selectedScene}' — no matching profile exists.");
}

// control can be handed to another computer without editing any scene, so an override taken by hand
// wins over the controller the active scene names — the same shape as the scene override above.
var controllerStore = new ControllerOverrideStore(configPath);
if (controllerStore.Read() is { } controllerOverride && config != null && !string.Equals(config.Controller, controllerOverride, StringComparison.OrdinalIgnoreCase))
{
    var index = profiles.IndexOf(config);
    config = config.WithController(controllerOverride);
    if (index >= 0) profiles[index] = config;
}

// derive network config blob after the persisted scene override has selected the active profile
string? embeddedNetworkConfig = null;
if (config?.EmbeddedStyx != null)
{
    embeddedNetworkConfig = Sync.Wait(NetworkConfig.ComputeEmbeddedBlob(config.EmbeddedStyx.Server, config.EmbeddedStyx.Password));
}
else if (config?.EmbeddedStyxServer != null)
    embeddedNetworkConfig = Sync.Wait(NetworkConfig.ComputeEmbeddedBlob($"http://localhost:{config.EmbeddedStyxServer.Port}", config.EmbeddedStyxServer.Password));

var profile = new HydraProfile(configFile, config, embeddedNetworkConfig);
services.AddSingleton<IHydraProfile>(profile);

services.AddSereneConsoleLogging(c => c.MinLogLevel = profile.LogLevel);

var logFileSetting = RunMode.IsSessionChild ? configFile.SessionLogFile : configFile.LogFile;
if (logFileSetting is { } logFile)
{
    var logPath = Path.IsPathRooted(logFile)
        ? logFile
        : Path.GetFullPath(logFile, Path.GetDirectoryName(configPath)!);
    if (configFile.LogTruncate && File.Exists(logPath))
        new FileStream(logPath, FileMode.Truncate).Dispose();
    services.AddSereneFileLogging(logPath, c => c.MinLogLevel = profile.LogLevel);
}

var startupLog = Sync.Wait(services.CreateLogger<HydraProfile>());
startupLog.LogInformation("Active profile: {ProfileName}", profile.ProfileName ?? "<none>");

if (config?.EmbeddedStyxServer != null)
{
    startupLog.LogInformation("Embedded Styx relay on port {Port}", config.EmbeddedStyxServer.Port);
    startupLog.LogInformation("Remote hosts can connect with: embeddedStyx: {{\"server\": \"http://<your-ip>:{Port}\", \"password\": \"<password>\"}}", config.EmbeddedStyxServer.Port);
}

// shared services always registered
services.AddSingleton(profiles);
services.AddSingleton<ICmdRunner, CmdRunner>();
services.AddSingleton<INetworkDetector>(_ => detector);
services.AddSingleton<IWorldState, WorldState>();
services.AddSingleton(sceneStore);
services.AddSingleton<IDisplayRouter, DisplayRouter>();
services.AddSingleton<DormancyState>();
services.AddSingleton<IDormancyState>(sp => sp.GetRequiredService<DormancyState>());
services.AddHostedService(sp => sp.GetRequiredService<DormancyState>());
services.AddLazyResolvers(); // enables Lazy<T> injection — used to break circular deps (e.g. ActivityTracker ↔ IRelaySender)

// shield always runs on macOS — handles cursor shielding + network state detection
if (OperatingSystem.IsMacOS() && macShield != null && macNetworkState != null)
{
    macShield.DebugShield = profile.DebugShield;
    services.AddSingleton(macNetworkState);
    services.AddSingleton(macShield);
    services.AddHostedService(_ => macShield);
}

// network watcher always runs — logs state on startup, triggers restarts on change
services.AddSingleton(sp => new NetworkWatcher(
    sp.GetRequiredService<INetworkDetector>(),
    GetScreenCount,
    profiles,
    config,
    configFile.Profile,
    sp.GetRequiredService<IDormancyState>(),
    sp.GetRequiredService<ILogger<NetworkWatcher>>()));
services.AddHostedService(sp => sp.GetRequiredService<NetworkWatcher>());

if (config != null)
{
    // console mode: no X display available — use evdev input and null screen detector
    var linuxConsoleMode = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") == null;

    // screen detector must be registered before any service that awaits IScreenDetector.Get() at startup
    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenDetector, MacScreenDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenDetector, WindowsScreenDetector>();
    else if (linuxConsoleMode)
        services.AddHostedService<IScreenDetector, NullScreenDetector>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenDetector, XorgScreenDetector>();
    else
        throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

    if (profile.Mode == Mode.Master)
    {
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformInput, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformInput, WindowsInputHandler>();
        else if (linuxConsoleMode)
        {
            if (!profile.RemoteOnly)
            {
                Console.Error.WriteLine("No display server available (DISPLAY not set). Set remoteOnly: true in hydra.conf for console operation.");
                return;
            }
            services.AddSingleton<IPlatformInput, EvdevInputHandler>();
        }
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IPlatformInput, XorgInputHandler>();
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddHostedService<ICursorHider, CursorHiderService>();
        services.AddHostedService<InputRouter>();
    }
    else if (profile.Mode == Mode.Slave)
    {
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<MacOutputHandler>();
            services.AddSingleton<IPlatformOutput>(sp => new CoalescingOutputWrapper(sp.GetRequiredService<MacOutputHandler>()));
            services.AddSingleton<ICursor>(sp => sp.GetRequiredService<MacOutputHandler>());
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<WindowsOutputHandler>();
#pragma warning disable CA1416
            services.AddSingleton<IPlatformOutput>(sp =>
            {
                var handler = sp.GetRequiredService<WindowsOutputHandler>();
                handler.Initialize();
                return new CoalescingOutputWrapper(handler);
            });
            services.AddSingleton<ICursor>(sp => sp.GetRequiredService<WindowsOutputHandler>());
#pragma warning restore CA1416
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<XorgOutputHandler>();
            services.AddSingleton<IPlatformOutput>(sp => new CoalescingOutputWrapper(sp.GetRequiredService<XorgOutputHandler>()));
            services.AddSingleton<ICursor>(sp => sp.GetRequiredService<XorgOutputHandler>());
        }
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddSingleton<IPlatformInput, SlavePlatformInput>();

        // real event tap for local keyboard/mouse activity tracking (events pass through — nothing is consumed)
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<ILocalEventTap, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<ILocalEventTap, WindowsInputHandler>();
        else if (!linuxConsoleMode)
            services.AddSingleton<ILocalEventTap, XorgInputHandler>();
        else
            services.AddSingleton<ILocalEventTap>(sp => sp.GetRequiredService<IPlatformInput>()); // no-op on console

        services.AddHostedService<ICursorHider, CursorHiderService>();
        services.AddHostedService<SlaveLocalInputWatcher>();

        // forwarder buffers log entries; SlaveLogSender drains them to masters
        var forwarder = new SlaveLogForwarder();
        services.AddSingleton(forwarder);
        services.AddSereneCustomLogging(e => forwarder.ForwardAsync(e).AsTask(), c => c.MinLogLevel = LogLevel.Debug);
        services.AddHostedService<SlaveLogSender>();

    }

    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenSaverSync, MacScreenSaverSync>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenSaverSync, WindowsScreenSaverSync>();
    else if (linuxConsoleMode)
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenSaverSync, XorgScreenSaverSync>();
    else
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();

    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IClipboardSync, MacClipboardSync>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IClipboardSync, WindowsClipboardSync>();
    else if (OperatingSystem.IsLinux() && !linuxConsoleMode)
        services.AddSingleton<IClipboardSync, XorgClipboardSync>();
    else
        services.AddSingleton<IClipboardSync, NullClipboardSync>();

    // file selection detector: reads selected files from Finder/Explorer for copy hotkey
    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IFileSelectionDetector, MacFileSelectionDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IFileSelectionDetector, WindowsFileSelectionDetector>();
    else if (OperatingSystem.IsLinux() && !linuxConsoleMode)
        services.AddSingleton<IFileSelectionDetector, LinuxFileSelectionDetector>();
    else
        services.AddSingleton<IFileSelectionDetector, NullFileSelectionDetector>();

    // file transfer: dialog and drop target resolver depend on platform; service is shared master/slave
    if (OperatingSystem.IsMacOS())
    {
        // macShield implements IFileTransferDialog and IOsdNotification (already registered as singleton above)
        services.AddSingleton<IFileTransferDialog>(sp => sp.GetRequiredService<MacShieldProcess>());
        services.AddSingleton<IOsdNotification>(sp => sp.GetRequiredService<MacShieldProcess>());
        services.AddSingleton<IDropTargetResolver, MacDropTargetResolver>();
    }
    else if (OperatingSystem.IsWindows())
    {
        services.AddSingleton<IFileTransferDialog, WindowsProgressDialog>();
        services.AddSingleton<IOsdNotification, WindowsOsdNotification>();
        services.AddSingleton<IDropTargetResolver, WindowsDropTargetResolver>();
    }
    else
    {
        services.AddSingleton<IFileTransferDialog, NullFileTransferDialog>();
        services.AddSingleton<IOsdNotification, NullOsdNotification>();
        if (OperatingSystem.IsLinux() && !linuxConsoleMode)
            services.AddSingleton<IDropTargetResolver, LinuxDropTargetResolver>();
        else
            services.AddSingleton<IDropTargetResolver, NullDropTargetResolver>();
    }
    services.AddSingleton<FileTransferService>();

    // embedded Styx must be registered before the relay connection so it starts first
    if (config.EmbeddedStyxServer != null)
    {
        services.AddSingleton(config.EmbeddedStyxServer);
        services.AddHostedService<EmbeddedStyxServer>();
        services.AddHostedService<LanDiscoveryBroadcaster>();
    }

    if (profile.Mode == Mode.Slave)
        services.AddHostedService<IRelaySender, SlaveRelayConnection>();
    else
        services.AddHostedService<IRelaySender, MasterRelayConnection>();
    services.AddSingleton<IActivityTracker, ActivityTracker>();
    services.AddSingleton<ISceneCoordinator, SceneCoordinator>();
    services.AddHostedService(sp => (SceneCoordinator)sp.GetRequiredService<ISceneCoordinator>());
    services.AddSingleton(configFile);
    services.AddSingleton(new DeskConfigStore(configPath));
    services.AddSingleton(controllerStore);
    services.AddSingleton<IDeskService, DeskService>();
    services.AddHostedService(sp => (DeskService)sp.GetRequiredService<IDeskService>());
    // Every computer answers, not just the one holding the keyboard. It is loopback-only, and a desk
    // that misbehaves is almost always two computers disagreeing — which cannot be seen at all if
    // only one of them can be asked what it thinks.
    if (configFile.Profile == null)
        services.AddHostedService(sp => new SceneControlServer(
            sp.GetRequiredService<ISceneCoordinator>(),
            sp.GetRequiredService<IDeskService>(),
            configFile.ControlPort,
            sp.GetRequiredService<ILogger<SceneControlServer>>()));
}

if (OperatingSystem.IsWindows() && RunMode.IsSessionChild)
    services.AddHostedService<SessionChildLifetime>();

var app = builder.Build();

if (macShield != null)
{
    var shieldLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shield");
    macShield.Log = shieldLog;
    shieldLog.LogInformation("auth={Auth} ssid={Ssid}",
        macNetworkState!.WifiAuthStatus switch { 0 => "notDetermined", 1 => "restricted", 2 => "denied", 3 or 4 => "authorized", _ => "none" },
        macNetworkState.Ssid ?? "(none)");

    // wire shield state changes to immediate network re-check
    macShield.OnNetworkStateChanged = () => app.Services.GetRequiredService<NetworkWatcher>().TriggerCheck();
}

// wire screen changes to condition re-check when screenCount conditions are configured
if (HydraConfig.HasScreenCountConditions(profiles))
{
    var screenDetector = app.Services.GetService<IScreenDetector>();
    if (screenDetector != null)
    {
        var watcher = app.Services.GetRequiredService<NetworkWatcher>();
        screenDetector.ScreensChanged += _ => watcher.TriggerCheck();
    }
}

Sync.Wait(app.StartAsync());
using var trayShutdown = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(TrayApplication.RequestShutdown);
var canShowTray = !OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
if (canShowTray)
    TrayApplication.Run(app.Services, configPath);
else
    await app.WaitForShutdownAsync();
await app.StopAsync();
processLock?.Dispose();
// Quitting from the tray has to leave nothing behind. A native event tap or message loop running on
// a foreground thread would otherwise keep the process alive — and visible in the macOS app switcher
// — long after the icon is gone.
Environment.Exit(Environment.ExitCode);

// creates the platform-specific network detector for use before DI is set up
static async Task<INetworkDetector> CreateDetector(MacNetworkState? macNetworkState, IServiceCollection logServices)
{
    if (OperatingSystem.IsMacOS()) return new MacNetworkDetector(macNetworkState);
    var cmdRunner = new CmdRunner(await logServices.CreateLogger<CmdRunner>());
    if (OperatingSystem.IsWindows()) return new WindowsNetworkDetector();
    if (OperatingSystem.IsLinux()) return new LinuxNetworkDetector(cmdRunner, await logServices.CreateLogger<LinuxNetworkDetector>());
    throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");
}

// returns the current number of connected screens
static int GetScreenCount()
{
    if (OperatingSystem.IsMacOS()) return MacDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsWindows()) return WindowsDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsLinux())
    {
        var display = Hydra.Platform.Linux.NativeMethods.XOpenDisplay(null);
        if (display == nint.Zero) return 1;
        try
        {
            var root = Hydra.Platform.Linux.NativeMethods.XDefaultRootWindow(display);
            return XorgDisplayHelper.GetAllScreens(display, root).Count;
        }
        finally
        {
            _ = Hydra.Platform.Linux.NativeMethods.XCloseDisplay(display);
        }
    }
    return 1;
}

static string? ReadStringOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static int? ReadIntOption(string[] arguments, string name) =>
    int.TryParse(ReadStringOption(arguments, name), out var value) ? value : null;

static async Task ActivateSceneFromCli(string scene, int port)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var response = await client.PostAsync($"http://127.0.0.1:{port}/api/scenes/{Uri.EscapeDataString(scene)}", null);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) Environment.ExitCode = 1;
        Console.WriteLine(body);
    }
    catch (Exception ex)
    {
        Environment.ExitCode = 1;
        Console.Error.WriteLine($"Could not reach the ScreenFuse master on 127.0.0.1:{port}: {ex.Message}");
    }
}
