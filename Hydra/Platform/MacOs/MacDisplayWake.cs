using System.Runtime.InteropServices;
using System.Text.Json;

namespace Hydra.Platform.MacOs;

// Reconnects displays the window server has dropped, using the same private-API mechanism as the
// DisplaySleeper tool (CGSConfigureDisplayEnabled + a .forSession display configuration). ScreenFuse's
// old "wake" only ran `caffeinate -u`, which asserts user activity but never re-enables a display
// the window server disabled — so after a monitor was put to sleep, or switched to another computer
// and macOS lost track of it, a DDC switch back to it found no signal on the Mac.
//
// The display ID of a disabled display is gone from CGGetOnlineDisplayList, so identity has to be
// recorded before it disappears. DisplaySleeper persists exactly that; this reads its store (and
// ScreenFuse's own, same shape) so displays it slept can be woken here too, and refreshes
// ScreenFuse's store from the online list on every wake so future drops stay recoverable without
// DisplaySleeper installed.
internal static class MacDisplayWake
{
    private static string ScreenFuseStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenFuse", "displays.json");

    private static string DisplaySleeperStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplaySleeper", "displays.json");

    private sealed record DisplayRecord(string Uuid, string Name, uint DisplayId, bool Connected);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ConfigureDisplayEnabled(nint config, uint displayId, bool enabled);

    // CGSConfigureDisplayEnabled is undocumented and absent from every SDK .tbd, so it cannot be
    // linked against — resolve it at runtime from SkyLight (or CoreGraphics, which re-exports it).
    // ponytail: dlsym rather than a re-declared extern. Ceiling: silently degrades to no-op if
    // Apple ever drops the symbol; DDC-only switching still works, it just can't resurrect a
    // display macOS already dropped.
    private static readonly ConfigureDisplayEnabled? Configure = Resolve();

    private static ConfigureDisplayEnabled? Resolve()
    {
        foreach (var framework in new[]
                 {
                     "/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight",
                     "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
                 })
        {
            try
            {
                var handle = NativeLibrary.Load(framework);
                var export = NativeLibrary.GetExport(handle, "CGSConfigureDisplayEnabled");
                return Marshal.GetDelegateForFunctionPointer<ConfigureDisplayEnabled>(export);
            }
            catch (Exception) { }
        }
        return null;
    }

    // Reconnects every display the window server is not currently driving, and refreshes the
    // remembered store from the online list so identities survive for the next time. Returns the
    // number of displays reconnected. Safe to call when nothing is asleep — a redundant reconnect
    // is a no-op (CGS rejects it, which is reconciled against the live list first).
    internal static unsafe int WakeDisconnected()
    {
        if (Configure == null) return 0;

        var online = OnlineRecords();
        Remember(online);
        var active = ActiveDisplayIds();

        var reconnected = 0;
        foreach (var record in LoadRemembered().Values)
        {
            // A display that is already driving needs nothing. One the window server dropped — a
            // hot-plug blip while the monitor switched inputs is enough — is reconnected with its
            // current id when it is still online, otherwise with the remembered one: a dropped
            // display keeps its id in practice, and only a display the window server no longer
            // lists accepts the configuration in the first place.
            var live = online.FirstOrDefault(d => d.Uuid.Equals(record.Uuid, StringComparison.OrdinalIgnoreCase));
            if (live != null && active.Contains(live.DisplayId)) continue;
            if (!SetConnected((live ?? record).DisplayId, connected: true)) continue;
            Remember([record with { Connected = true }]);
            reconnected++;
        }
        return reconnected;
    }

    private static unsafe List<uint> ActiveDisplayIds()
    {
        const uint max = 32;
        var ids = stackalloc uint[(int)max];
        uint count = 0;
        if (NativeMethods.CGGetActiveDisplayList(max, ids, out count) != 0) return [];
        return [.. new ReadOnlySpan<uint>(ids, (int)count)];
    }

    // Enables or disables one display by its UUID (the monitor identity the desk config carries),
    // so a monitor that switched to another computer can be removed from — or restored to — the
    // Mac's arrangement. Returns false when the display is unknown or the configuration failed.
    internal static unsafe bool SetDisplayConnected(string uuid, bool connected)
    {
        if (Configure == null) return false;
        var record = LoadRemembered().Values.FirstOrDefault(r => r.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
        // The current display id wins over the remembered one: ids shift when a display is
        // disabled and re-enabled, and configuring the stale id is refused by the window server.
        var live = OnlineRecords().FirstOrDefault(d => d.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));

        // The store is what lets a display that has *vanished* be found again, so a display still
        // in front of us needs nothing from it. Requiring the record anyway meant the first
        // disconnect of a display this Mac had never woken was refused outright — the monitor
        // changed hands and macOS went on driving a panel nobody was looking at.
        record ??= live;
        if (record == null) return false;

        // A display that is already driving is left alone. The switch's hold loop asks for this
        // every few hundred milliseconds — the monitor's hot-plug blip can drop the display at any
        // point in that window — and a display configuration for a display that needs none still
        // reconfigures the arrangement, which the user sees as the desktop flickering.
        if (connected && live != null && ActiveDisplayIds().Contains(live.DisplayId)) return true;

        if (!SetConnected((live ?? record).DisplayId, connected)) return false;
        Remember([record with { Connected = connected }]);
        return true;
    }

    private static bool SetConnected(uint displayId, bool connected)
    {
        if (NativeMethods.CGBeginDisplayConfiguration(out var config) != 0) return false;
        if (Configure!(config, displayId, connected) != 0)
        {
            NativeMethods.CGCancelDisplayConfiguration(config);
            return false;
        }
        // .forSession, never .permanently: a reboot must always bring every display back. This is
        // the escape hatch for displayplacer#109, where a disabled display vanished from the device
        // tree with replugging as the only recovery.
        return NativeMethods.CGCompleteDisplayConfiguration(config, NativeMethods.KCGConfigureForSession) == 0;
    }

    private static unsafe List<DisplayRecord> OnlineRecords()
    {
        const uint max = 32;
        var ids = stackalloc uint[(int)max];
        uint count = 0;
        if (NativeMethods.CGGetOnlineDisplayList(max, ids, out count) != 0) return [];
        var result = new List<DisplayRecord>((int)count);
        for (uint i = 0; i < count; i++)
            result.Add(new DisplayRecord(NativeMethods.DisplayUuid(ids[i]), Name(ids[i]), ids[i], Connected: true));
        return result;
    }

    private static string Name(uint displayId)
    {
        try
        {
            var bounds = NativeMethods.CGDisplayBounds(displayId);
            return $"{bounds.Size.X:0}x{bounds.Size.Y:0}";
        }
        catch (Exception) { return displayId.ToString(); }
    }

    private static Dictionary<string, DisplayRecord> LoadRemembered()
    {
        var records = new Dictionary<string, DisplayRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { DisplaySleeperStatePath, ScreenFuseStatePath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, DisplayRecord>>(
                    File.ReadAllText(path), JsonOptions);
                if (parsed == null) continue;
                foreach (var (uuid, record) in parsed)
                    records[uuid] = record;
            }
            catch (Exception) { /* a corrupt state file must not stop the desk waking displays */ }
        }
        return records;
    }

    private static void Remember(IEnumerable<DisplayRecord> updated)
    {
        try
        {
            var store = LoadRemembered();
            foreach (var record in updated)
                store[record.Uuid] = record;
            var dir = Path.GetDirectoryName(ScreenFuseStatePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ScreenFuseStatePath, JsonSerializer.Serialize(store, JsonOptions));
        }
        catch (Exception) { /* persistence is best-effort — reconnect still works this run */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
