using System.Text;
using Cathedral.Config;
using Cathedral.Extensions;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public enum MessageKind : byte
{
    MouseMove = 1,
    KeyEvent = 2,
    MouseButton = 3,
    MouseScroll = 4,
    EnterScreen = 5,
    LeaveScreen = 6,
    MasterConfig = 7,
    ScreenInfo = 8,
    SlaveLog = 9,
    MouseMoveDelta = 10,
    ScreensaverSync = 11,
    ClipboardPush = 12,         // master â†’ slave: apply this clipboard (text/image/files)
    ClipboardPull = 13,         // master â†’ slave: send me your clipboard
    ClipboardPullResponse = 14, // slave â†’ master: here's my clipboard
    // 15, 16 reserved (formerly used; do not reuse â€” breaks wire compat with older clients)
    FileTransferRequest = 17,   // master â†’ receiver: can you receive? (SourceHost = actual data sender if different)
    FileTransferStart = 26,     // data source â†’ receiver: here's what's coming (FileNames + TotalBytes)
    FileTransferChunk = 18,     // data source â†’ receiver: chunk of tar.gz data
    FileTransferDone = 19,      // data source â†’ receiver: all data sent
    FileTransferAbort = 20,     // either â†’ either: abort and clean up
    FileTransferAccepted = 25,  // receiver â†’ master: destination validated, ready to receive
    FileSelectionQuery = 21,    // master â†’ slave: what files are selected?
    FileSelectionResponse = 22, // slave â†’ master: here are the selected files
    FileStreamRequest = 23,     // master â†’ source slave: stream these files to target
    Osd = 24,                   // master â†’ slave: display an on-screen notification
    FileTransferBusy = 27,      // slave â†’ master: transfer already in progress, request refused
    ClipboardHash = 28,         // master â†’ slave: here's my clipboard hash (on screen enter)
    ClipboardPullRequest = 29,  // slave â†’ master: my hash differs, please push your clipboard
    LockScreen = 30,            // master â†’ slave: lock the screen
    ActivityPing = 31,          // either direction: poke idle timer; master re-broadcasts to other slaves if syncScreensaver
    SceneActivate = 32,         // master â†’ all agents: apply display routing and restart into named profile
    DeskInventory = 33,         // any â†’ controller: my screens and the monitors I can reach over DDC
    DeskSetInput = 34,          // controller â†’ peer: switch this monitor's input (only the peer that
                                //   currently drives a monitor can command it, so switching is delegated)
    DeskSetInputResult = 35,    // peer â†’ controller: how the delegated switch went
    DeskState = 36,             // controller â†’ all: the merged desk, so every settings window shows the same picture
    DeskCommand = 37,           // any â†’ controller: a desk action requested from another computer's settings window
    DeskConfigPush = 38,        // controller â†’ all: the shared desk document (arrangement, monitors, scenes)
    DeskDisplayPower = 39,      // controller â†’ peer: stop or resume your video output, so a monitor's
                                //   automatic input detection follows the signal (no DDC required)
    DeskConfigRequest = 40,     // follower â†’ controller: my desk document differs from yours, send it
    DeskInventoryRequest = 41,  // controller â†’ peer: refresh and return its current display inventory
    CursorPosition = 42,        // slave â†’ master: where the slave's pointer actually is, so the master
                                //   can reconcile its virtual pointer with reality (a pointer the user
                                //   moved locally would otherwise strand the crossing)
    CursorReset = 43,           // controller â†’ peers: restore every computer's cursor (troubleshooting)
    SetMonitorDisplay = 44,     // controller â†’ peer: remove/restore the peer's display for one monitor
                                //   (the monitor switched to another computer â€” the peer must stop
                                //   using the display until it is switched back)
    SetMonitorStandby = 45,     // controller â†’ peer: blank or wake one panel without touching the desktop topology
}

public record MouseMoveMessage(string Screen, int X, int Y);
public record MouseMoveDeltaMessage(int Dx, int Dy);
// Output/DisplayName/PlatformId travel with the geometry so the computer holding the keyboard can
// recognise a remote screen by the names its owner uses for it. Without them a remote screen has
// only its position and an index-based name, and a crossing that names any other identifier â€” which
// is the only kind that survives a display being plugged in or removed â€” matches nothing at all and
// is quietly dropped.
public record ScreenInfoEntry(
    string Name, int X, int Y, int Width, int Height, decimal MouseScale, decimal? RelativeMouseScale = null,
    string? Output = null, string? DisplayName = null, string? PlatformId = null);

// ReSharper disable once InconsistentNaming
public enum PeerPlatform : byte { Unknown = 0, Linux = 1, MacOS = 2, Windows = 3 }

public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform? Platform = null);
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
// IsRepeat marks an OS auto-repeat the master re-resolved (with live modifier/dead-key state) and forwarded;
// the slave injects it without tracking a new held key. UnicodeKeyRepeat is the master's per-keypress repeat
// preference: when set, Mac slaves inject repeated characters via keycode-less unicode (avoiding the
// press-and-hold accent popup) rather than re-pressing the physical key. travelling per-keypress lets a
// shared slave honour each master's own preference.
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, bool IsRepeat = false, bool UnicodeKeyRepeat = true);
public record MouseButtonMessage(MouseButton Button, bool IsPressed);
public record MouseScrollMessage(short XDelta, short YDelta);
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height);
public record ScreensaverSyncMessage(bool Active);
public record LeaveScreenMessage;
public record ClipboardPullMessage(ulong? MasterHash = null);
public record ClipboardPushMessage(string Text, string? PrimaryText = null, byte[]? ImagePng = null, string? Html = null, byte[]? Rtf = null);
public record ClipboardPullResponseMessage(string? Text, string? PrimaryText = null, byte[]? ImagePng = null, bool? Unchanged = null, string? Html = null, byte[]? Rtf = null);
public record ClipboardHashMessage(ulong Hash);
public record ClipboardPullRequestMessage;
public record LockScreenMessage(long MillisecondsSinceLastInput);

public record FileTransferRequestMessage(string? SourceHost = null);
public record FileTransferStartMessage(string[] FileNames, long TotalBytes);
public record FileTransferChunkMessage(int Sequence, byte[] Data);
public record FileTransferDoneMessage(long TotalBytesSent, byte[] Sha256);
public record FileTransferAbortMessage(string Reason);
public record FileTransferAcceptedMessage;
public record FileSelectionQueryMessage;
public record FileSelectionResponseMessage(string[]? Paths, string? NotFocusedMessage = null);
public record FileStreamRequestMessage(string[] Paths, string TargetHost);
public record OsdMessage(string Text);
public record FileTransferBusyMessage;
public record ActivityPingMessage;
public record SceneActivateMessage(string Scene);

// -- desk --
// A monitor one host can currently reach over DDC. CurrentInput is the input code that selects
// that host, learned by reading the monitor back while the host is the active source.
public record DeskMonitorReport(
    string DdcId, string Description, int? CurrentInput,
    List<string>? Aliases = null, List<int>? SupportedInputs = null);
// A screen the host's display server reports, so the desk can place monitors that answer no DDC.
public record DeskScreenReport(
    string ScreenId, string? Output, string? DisplayName, int X, int Y, int Width, int Height,
    string? PlatformId = null);
public record DeskInventoryMessage(List<DeskMonitorReport> Monitors, List<DeskScreenReport> Screens);
public record DeskSetInputMessage(string RequestId, string DdcId, int Input);
public record DeskDisplayPowerMessage(string RequestId, bool Wake);
public record DeskInventoryRequestMessage;
public record DeskConfigRequestMessage;
public record CursorPositionMessage(string Screen, int X, int Y);
public record CursorResetMessage;
public record SetMonitorDisplayMessage(string LocalSourceId, bool Enabled);
public record SetMonitorStandbyMessage(string LocalSourceId, bool Standby);
public record DeskSetInputResultMessage(string RequestId, bool Success, string? Detail);
public record DeskStateMonitor(
    string Id, string Label, int DeskX, int DeskY, int Width, int Height,
    string? ActiveHost, List<DeskStateSource> Sources, bool Sleeping = false);
public record DeskStateSource(string Host, int? Input, bool Reachable, List<int>? AvailableInputs = null);
public record DeskStateMessage(
    string? Fingerprint,
    string Controller, List<string> Hosts, List<string> ConnectedHosts,
    List<DeskStateMonitor> Monitors, List<string> Scenes, string? CurrentScene,
    string? ControllerOverride = null);
public enum DeskCommandKind : byte { SetMonitorHost = 1, SetController = 2, SaveScene = 3, ActivateScene = 4, SaveArrangement = 6, DeleteScene = 7 }
public record DeskArrangementEntry(string Monitor, int DeskX, int DeskY, int Width, int Height, string? Label);
public record DeskCommandMessage(
    DeskCommandKind Kind, string? Monitor = null, string? Host = null, string? Scene = null,
    int? Input = null, List<DeskArrangementEntry>? Arrangement = null);
public record DeskConfigPushMessage(string Json);

public static class MessageSerializer
{
    // wire format: [1 byte kind][utf-8 json]
    public static byte[] Encode<T>(MessageKind kind, T message)
    {
        var json = message.ToSaneJsonBytes(SaneJson.CompactOptions);
        var result = new byte[1 + json.Length];
        result[0] = (byte)kind;
        json.CopyTo(result, 1);
        return result;
    }

    public static DecodedMessage Decode(byte[] payload)
    {
        if (payload.Length == 0) throw new ArgumentException("Empty payload", nameof(payload));
        var kind = (MessageKind)payload[0];
        return new DecodedMessage(kind, payload.AsMemory(1));
    }
}

public record DecodedMessage(MessageKind Kind, ReadOnlyMemory<byte> Bytes)
{
    // lazy string conversion â€” only used in tests and low-frequency paths
    public string Json => Encoding.UTF8.GetString(Bytes.Span);
    public T Deserialize<T>() => Bytes.FromSaneJson<T>()!;
}
