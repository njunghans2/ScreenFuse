# ScreenFuse

ScreenFuse makes a desk of Windows, macOS, and Linux computers behave like one multi-monitor workspace. The cursor crosses computer boundaries, keyboard input follows it, rich clipboard content is synchronized, files and folders can be transferred, and named **desk scenes** coordinate the physical input selected on shared monitors.

It is local-first and GPL-2.0. Input traffic uses Hydra's end-to-end encrypted relay protocol; the scene control API listens on loopback only.

## What works

- Seamless mouse and keyboard movement across arbitrary multi-monitor layouts.
- Windows 10/11, macOS 13+ (Apple Silicon and Intel), and Linux X11 (x64/arm64).
- Text, HTML, RTF, and image clipboard synchronization.
- File/folder transfer on Windows and macOS; Linux supports copied file URI lists through `xclip` or `wl-clipboard` and receives transfers into Downloads.
- Named scenes that switch every agent to the matching topology and coordinate monitor inputs.
- Native DDC/CI on Windows, bundled `m1ddc` on Apple Silicon macOS releases, and `ddcutil` on Linux.
- Monitor sleep/wake fallback for displays that auto-select a newly active signal.
- Native tray menu and settings on Windows, macOS, and supported Linux desktops, with first-run setup, scene switching, peer status, display diagnostics, restart, and startup installation.
- Auto-start installation: Windows service, macOS LaunchAgent, or Linux graphical autostart/systemd user service.
- Self-contained release archives; no .NET runtime installation is required.

Important limits: DDC/CI input switching depends on monitor firmware and cabling; Linux pointer capture currently requires X11; cross-machine file “dragging” uses copy/paste transfer rather than moving a live native drag object between operating systems. See [Known limitations](#known-limitations).

## Install

Download and open the ScreenFuse release on both computers. Keep both computers on the same private LAN. No addresses, ports, desk names, secrets, roles, or configuration files are needed.

1. ScreenFuse finds the other computer automatically.
2. Check that the same six-digit code appears on both computers.
3. On the controlling computer, choose whether the other computer is to the right, left, above, or below.
4. Click **Codes match — connect** on both.

ScreenFuse creates the encrypted desk, configures both computers, enables launch on sign-in, and starts. The OS may show a one-time permission or private-network firewall prompt. The pairing code is deliberately the only confirmation: without a cloud account or a pre-shared secret, that check prevents another device on the LAN from impersonating your computer.

If no configuration exists, the native first-run window opens automatically. You can reopen setup explicitly:

```text
screenfuse --setup
```

The tray's **Advanced settings…** window uses guided native controls—there is no JSON editor. It is only needed for extra computers, nonstandard topologies, named scenes, or physical monitor input switching. Existing settings are backed up before each save. Per-user settings live in the platform application-data directory, so ScreenFuse also works when installed under Program Files, `/Applications`, or another read-only location.

Discover the available monitor identifiers and DDC helper status before editing scenes:

```text
screenfuse --doctor
```

Launch-on-startup is enabled during normal pairing. It can also be repaired from the tray or with `screenfuse --install`.

On Linux, install the small platform helpers first:

```bash
sudo apt install ddcutil xclip
```

`ddcutil` normally needs access to `/dev/i2c-*`; follow your distribution's `i2c` group/udev instructions. `wl-paste` from `wl-clipboard` can replace `xclip` for file clipboard reads, although seamless input still requires an X11 session.

On macOS, grant Accessibility when prompted. Direct HDMI, DisplayPort, or USB-C connections are more reliable for DDC than docks. Intel Macs can use the sleep/wake fallback or a separately installed compatible DDC helper.

## Generated configuration reference

The tray creates and maintains the configuration automatically. The example below is included for troubleshooting and automation—not as a setup step. Each computer has its own config, scene names match, and the computer with the physical keyboard/mouse stays the master.

```json
{
  "name": "pc",
  "controlPort": 24801,
  "profiles": [
    {
      "profileName": "PC focus",
      "mode": "Master",
      "embeddedStyxServer": { "port": 5000, "password": "replace-with-a-long-random-secret", "discoveryName": "studio" },
      "displayRouting": {
        "inputs": [{ "id": "DELL U2720Q", "input": 15 }],
        "settleDelayMs": 700
      },
      "hosts": [
        { "name": "pc", "neighbours": [{ "direction": "Right", "name": "mac" }] },
        { "name": "mac" }
      ]
    },
    {
      "profileName": "Mac focus",
      "mode": "Master",
      "embeddedStyxServer": { "port": 5000, "password": "replace-with-a-long-random-secret", "discoveryName": "studio" },
      "displayRouting": {
        "inputs": [{ "id": "DELL U2720Q", "input": 17 }],
        "settleDelayMs": 700
      },
      "hosts": [
        { "name": "pc", "neighbours": [{ "direction": "Right", "name": "mac" }] },
        { "name": "mac" }
      ]
    }
  ]
}
```

`auto://studio` uses link-local UDP multicast/broadcast discovery, so DHCP address changes require no config edits. The beacon contains only the human-readable desk name, host name, and relay port—not the password or encryption material. Pick a distinct desk name on shared office networks; the shared password still authenticates and encrypts the relay session.

Common MCCS input values are DisplayPort 1 = `15`, HDMI 1 = `17`, HDMI 2 = `18`, and USB-C = `27`, but monitor firmware can use different values.

Monitor IDs are platform-specific:

- Windows: a substring of the physical monitor description (`DELL U2720Q`), logical name (`\\.\DISPLAY1`), or `*`.
- Linux: ddcutil display number (`1`), bus (`bus:6`), or `*`.
- macOS: m1ddc display index/UUID or `*`.

If DDC input selection does not work, use `"wakeDisplays": true` in the gaining machine's scene and `"sleepDisplays": true` in the losing machine's matching scene. Sleep/wake affects all displays on that computer, so DDC is preferable when dedicated monitors must stay on.

The full inherited topology/profile reference is in [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Switch scenes

Choose a scene from the ScreenFuse tray menu on the running master. Slave trays show connection state but intentionally leave scene switching to the master.

Or activate a scene from scripts, launchers, Stream Deck, Raycast, or AutoHotkey:

```text
screenfuse --scene "Mac focus"
```

Scene activation is fail-safe: if configured peers are disconnected, ScreenFuse refuses to switch only the local computer. It applies the target scene's display commands, broadcasts the scene, persists it in `.screenfuse-scene`, then restarts every agent into the new topology.

## Clipboard and files

Clipboard content synchronizes when the cursor enters another computer. File transfer uses:

- `Ctrl+Alt+Super+C` to copy selected files/folders.
- `Ctrl+Alt+Super+V` to transfer them to the computer under the cursor.

On Linux, first use the file manager's normal Copy command so it publishes `text/uri-list`, then use the ScreenFuse hotkeys. Received files go to Downloads (then Desktop/home as fallbacks). Transfers are streamed as compressed archives with a SHA-256 integrity check; folders and name conflicts are supported.

## Build and test

Requires .NET SDK 10.

```text
dotnet restore Hydra.sln
dotnet test Hydra.sln
dotnet publish Hydra --runtime win-x64 --self-contained
```

GitHub Actions builds self-contained archives for `win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`, and `linux-arm64`.

## Known limitations

- Linux seamless input uses X11/XInput2. Wayland's compositor security model requires InputCapture/RemoteDesktop portals and compositor support; that backend is not implemented here yet.
- No software can guarantee DDC/CI input switching on arbitrary monitors. Some monitors ignore VCP `0x60`, some docks block DDC, and some panels answer only on the currently active video link.
- A live operating-system drag object cannot cross OS kernels. ScreenFuse transfers files/folders and clipboard formats, but uses explicit copy/paste hotkeys rather than pretending a native drag continues across machines.
- macOS input injection requires Accessibility permission. Windows secure-desktop support requires service installation.
- Avalonia tray support is confirmed on Windows, macOS, KDE, and Ubuntu GNOME; other Linux desktops need a compatible StatusNotifier/AppIndicator tray host.
- Scene names and network credentials must match across the participating computers.

## Project provenance

ScreenFuse is based on [PacAnimal/Hydra](https://github.com/PacAnimal/hydra), whose low-latency cross-platform input, clipboard, file-transfer, topology, and relay implementation made a responsible first release possible. The combined work remains under [GPL-2.0](LICENSE). See [docs/MARKET_RESEARCH.md](docs/MARKET_RESEARCH.md) for the build-vs-adopt evidence and [NOTICE.md](NOTICE.md) for attribution.
