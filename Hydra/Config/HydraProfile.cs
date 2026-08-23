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
    Mode Mode { get; }
    string? Controller { get; }
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

    // Adopts a new arrangement without restarting.
    //
    // The input router rebuilds its layout from Hosts whenever the screens change, so replacing
    // them here is enough for a rearranged desk to take effect within seconds. Before this, the
    // crossings were written to the config and read back only at startup: the desk said the pointer
    // could cross and the router — still holding the layout it was built with — disagreed.
    void ApplyHosts(List<HostConfig> hosts);
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
    public Mode Mode => _activeProfile?.ResolveMode(Name) ?? Mode.Slave;
    public string? Controller => _activeProfile?.Controller;
    public List<DeskMonitorConfig> Monitors { get; } = configFile.Monitors;
    private volatile List<HostConfig>? _hosts;

    public List<HostConfig> Hosts => _hosts ?? _activeProfile?.Hosts ?? [];

    public void ApplyHosts(List<HostConfig> hosts) => _hosts = hosts;
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
