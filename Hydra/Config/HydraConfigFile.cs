using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hydra.Config;

public class HydraConfigFile
{
    // Disabled by default until a ScreenFuse release feed is configured. This prevents
    // a derived build from replacing itself with an unrelated upstream Hydra binary.
    public bool AutoUpdate { get; init; } = false;

    [JsonConverter(typeof(LogLevelConverter))]
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    // optional — if set, hydra refuses to start if another instance holds the lock on this file
    public string? LockFile { get; init; }

    // optional — if set, log output is also written to this file
    public string? LogFile { get; init; }

    // optional — if set, the session child writes log output to this file (service mode only)
    public string? SessionLogFile { get; init; }

    // if true, truncate logFile/sessionLogFile to 0 bytes on startup
    public bool LogTruncate { get; init; } = false;

    // optional — defaults to machine hostname without domain
    public string? Name { get; init; }

    // optional — if set, this profile is always selected regardless of conditions (useful for debugging)
    public string? Profile { get; init; }

    // loopback-only JSON automation API used by `screenfuse --scene NAME`
    public int ControlPort { get; init; } = 24801;

    public bool DebugShield { get; init; } = false;
    public bool DebugMouse { get; init; } = false;

    // The physical desk: every monitor, where it sits, and how each computer reaches it. Shared by
    // all scenes — the wiring does not change when a scene switches which computer a monitor shows.
    public List<DeskMonitorConfig> Monitors { get; init; } = [];

    public List<HydraConfig> Profiles { get; init; } = [];

    public DeskMonitorConfig? Monitor(string id) => Monitors.FirstOrDefault(m => m.Id.EqualsIgnoreCase(id));

    // convenience method for single-profile scenarios (tests, simple setups)
    public static HydraConfigFile Load(IConfiguration config)
    {
        var (file, _) = LoadAll(config);
        return file;
    }

    public static (HydraConfigFile file, string path) LoadAll(IConfiguration config)
    {
        var binaryDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var path = config.GetStringOrNull("CONFIG")
            ?? FindConfig(DefaultPath())
            ?? FindConfig(Path.Combine(binaryDir, "screenfuse.conf"))
            ?? FindConfig(Path.Combine(binaryDir, "hydra.conf"))
            ?? FindConfig(Path.Combine(Directory.GetCurrentDirectory(), "screenfuse.conf"))
            ?? FindConfig(Path.Combine(Directory.GetCurrentDirectory(), "hydra.conf"))
            ?? throw new FileNotFoundException("No screenfuse.conf found. Set CONFIG=/path/to/screenfuse.conf and try again.");

        var json = File.ReadAllText(path);
        var file = Parse(json, path);
        return (file, path);
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(appData, "ScreenFuse", "screenfuse.conf");
    }

    internal static HydraConfigFile Parse(string json, string path)
    {
        var file = json.FromSaneJson<HydraConfigFile>()
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
        if (file.ControlPort is < 1024 or > 65535)
            throw new InvalidOperationException("controlPort must be between 1024 and 65535.");
        ValidateMonitors(file.Monitors);
        file.Profiles.ForEach(p => HydraConfig.ExpandMirrors(p.Hosts));
        HydraConfig.Validate(file.Profiles, file.Name ?? Environment.MachineName.Split('.')[0], file.Profile);
        return file;
    }

    private static void ValidateMonitors(List<DeskMonitorConfig> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Id))
                throw new InvalidOperationException("Every desk monitor needs an id.");
            if (monitor.Width < 0 || monitor.Height < 0)
                throw new InvalidOperationException($"Desk monitor '{monitor.Id}' has a negative size.");
            foreach (var source in monitor.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Host))
                    throw new InvalidOperationException($"Desk monitor '{monitor.Id}' has a source with no computer name.");
                if (source.Input is < 0 or > 255)
                    throw new InvalidOperationException($"Desk monitor '{monitor.Id}' input for '{source.Host}' must be between 0 and 255.");
            }
            var dupSource = monitor.Sources
                .GroupBy(s => s.Host.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupSource != null)
                throw new InvalidOperationException($"Desk monitor '{monitor.Id}' lists computer '{dupSource.Key}' twice.");
        }

        var duplicate = monitors
            .GroupBy(m => m.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"screenfuse.conf has duplicate desk monitor id '{duplicate.Key}'.");
    }

    private static string? FindConfig(string path) => File.Exists(path) ? path : null;
}

// maps SereneLogger short names (trce/dbug/info/warn/fail/crit) to LogLevel
internal sealed class LogLevelConverter : JsonConverter<LogLevel>
{
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToLowerInvariant() switch
        {
            "trce" or "trace" => LogLevel.Trace,
            "dbug" or "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "fail" or "error" => LogLevel.Error,
            "crit" or "critical" => LogLevel.Critical,
            var s => throw new JsonException($"Unknown log level: '{s}'")
        };

    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => value.ToString()
        });
}
