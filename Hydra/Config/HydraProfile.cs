using Cathedral.Extensions;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public interface IHydraProfile
{
    // root-level settings (constant for the life of this process)
    string Name { get; }
    LogLevel LogLevel { get; }
    bool AutoUpdate { get; }
    bool DebugShield { get; }
    bool DebugMouse { get; }

    // active profile settings
    string? ProfileName { get; }

    // Which role this computer plays right now. Handing the keyboard to another computer changes
    // it while the process runs, so this is a reading, not a startup fact.
    Mode Mode { get; }

    // The computer whose keyboard and mouse drive the desk. Every machine stores the same name and
    // the one that matches its own is the controller.
    string? Controller { get; }
    bool IsController { get; }

    // Raised after ApplyController, so everything that behaves differently in each role can change
    // over where it stands instead of being restarted into the new one.
    event Action? ControllerChanged;

    // Hand control to another computer, live.
    void ApplyController(string? host);
    List<DeskMonitorConfig> Monitors { get; }
    List<HostConfig> Hosts { get; }
    List<ScreenDefinition> ScreenDefinitions { get; }
    decimal? MouseScale { get; }
    decimal? RelativeMouseScale { get; }
    string? NetworkConfig { get; }
    bool HideCursor { get; }
    bool RemoteOnly { get; }
    bool SyncScreensaver { get; }
    bool ScreenLockPropagation { get; }
    bool AccelerateMouseWheel { get; }
    bool UnicodeKeyRepeat { get; }
    int? DeadCorners { get; }
    DisplayRoutingConfig DisplayRouting { get; }

    // computed from Name + Hosts
    HostConfig? LocalHost { get; }
    IEnumerable<HostConfig> RemoteHosts { get; }

    // Adopts a new arrangement without restarting, and says so.
    //
    // Replacing the list is not enough on its own. The input router builds a layout from Hosts and
    // then keeps it, rebuilding only when the screens change or a peer comes and goes — and dragging
    // a monitor around the desk changes no screen at all, so the rebuild never came. The desk said
    // the pointer could cross and the router, still holding the layout it started with, disagreed;
    // the arrangement appeared to take effect only after a restart, or after a peer reconnected and
    // rebuilt it by accident.
    void ApplyHosts(List<HostConfig> hosts);

    // Raised after ApplyHosts, so whoever is holding a layout derived from Hosts can rebuild it.
    event Action? HostsChanged;
}

public class HydraProfile(HydraConfigFile configFile, HydraConfig? activeProfile, string? networkConfigOverride = null) : IHydraProfile
{
    private readonly HydraConfig? _activeProfile = activeProfile;

    public string Name { get; } = configFile.Name ?? Environment.MachineName.Split('.')[0];
    public LogLevel LogLevel { get; } = configFile.LogLevel;
    public bool AutoUpdate { get; } = configFile.AutoUpdate;
    public bool DebugShield { get; } = configFile.DebugShield;
    public bool DebugMouse { get; } = configFile.DebugMouse;

    public string? ProfileName => _activeProfile?.ProfileName;

    // Seeded from the active profile — which Program has already reconciled with the hand-taken
    // override on disk — and replaced live when control is handed over. A profile naming nobody is
    // a legacy config and keeps falling back to its own explicit mode.
    private volatile string? _controller = activeProfile?.Controller;
    public string? Controller => _controller;

    public Mode Mode => HydraConfig.ResolveMode(_controller, _activeProfile?.Mode ?? Config.Mode.Slave, Name);
    public bool IsController => Mode == Config.Mode.Master;

    public event Action? ControllerChanged;

    public void ApplyController(string? host)
    {
        var next = string.IsNullOrWhiteSpace(host) ? null : host;
        if (string.Equals(_controller, next, StringComparison.OrdinalIgnoreCase)) return;
        _controller = next;
        ControllerChanged?.Invoke();
    }
    public List<DeskMonitorConfig> Monitors { get; } = configFile.Monitors;
    private volatile List<HostConfig>? _hosts;

    public List<HostConfig> Hosts => _hosts ?? _activeProfile?.Hosts ?? [];

    public event Action? HostsChanged;

    public void ApplyHosts(List<HostConfig> hosts)
    {
        _hosts = hosts;
        HostsChanged?.Invoke();
    }
    public List<ScreenDefinition> ScreenDefinitions => _activeProfile?.ScreenDefinitions ?? [];
    public decimal? MouseScale => _activeProfile?.MouseScale;
    public decimal? RelativeMouseScale => _activeProfile?.RelativeMouseScale;
    public string? NetworkConfig => networkConfigOverride ?? _activeProfile?.NetworkConfig;
    public bool HideCursor => _activeProfile?.HideCursor ?? false;
    public bool RemoteOnly => _activeProfile?.RemoteOnly ?? false;
    public bool SyncScreensaver => _activeProfile?.SyncScreensaver ?? true;
    public bool ScreenLockPropagation => _activeProfile?.ScreenLockPropagation ?? false;
    public bool AccelerateMouseWheel => _activeProfile?.AccelerateMouseWheel ?? true;
    public bool UnicodeKeyRepeat => _activeProfile?.UnicodeKeyRepeat ?? true;
    public int? DeadCorners => _activeProfile?.DeadCorners;
    public DisplayRoutingConfig DisplayRouting => _activeProfile?.DisplayRouting ?? new();

    public HostConfig? LocalHost => Hosts.FirstOrDefault(h => h.Name.EqualsIgnoreCase(Name));
    public IEnumerable<HostConfig> RemoteHosts => Hosts.Where(h => !h.Name.EqualsIgnoreCase(Name));
}
