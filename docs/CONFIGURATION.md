# ScreenFuse — Configuration Reference

See the [project README](../README.md) for installation and a quick-start guide.

**Contents**

- [Requirements](#requirements)
- [Config file location](#config-file-location)
- [Config fields](#config-fields)
- [Screen layout](#screen-layout)
- [Desk scenes and display routing](#desk-scenes-and-display-routing)
- [Dead corners](#dead-corners)
- [Multi-monitor](#multi-monitor)
- [Screen definitions](#screen-definitions)
- [Network-aware config](#network-aware-config)
- [Hotkeys](#hotkeys)
- [Clipboard sync](#clipboard-sync)
- [File transfer](#file-transfer)
- [Screensaver sync](#screensaver-sync)
- [Remote-only mode](#remote-only-mode)
- [Networking with Styx](#networking-with-styx)
- [Building from source](#building-from-source)

---

## Requirements

- .NET 10
- **macOS**: Accessibility permission (System Settings → Privacy & Security → Accessibility)
- **Linux (with display)**: X11 with XInput2 (Wayland not yet supported)
- **Linux (headless/console)**: `remoteOnly: true` in config; user must be in the `input` group (`sudo usermod -aG input $USER`) for `/dev/input/event*` access; `libxkbcommon` installed (`apt install libxkbcommon0`)

## Config file location

The preferred config file is `screenfuse.conf`, located next to the binary (`hydra.conf` remains a compatibility fallback). A packaged macOS app uses `~/Library/Application Support/ScreenFuse/screenfuse.conf` so settings remain writable when the app is installed in `/Applications`. Set the `CONFIG` environment variable to use a different path:

`screenfuse --setup` opens the native tray settings window; it validates and atomically saves `screenfuse.conf`, then relaunches ScreenFuse normally. A missing or invalid configuration opens the same native first-run window automatically. Named scenes are selected from the master computer's tray menu.

```bash
CONFIG=/path/to/screenfuse.conf ./screenfuse
```

## Config fields

**Root-level** (global, apply to all profiles):

- `name` — this machine's name on the network. Optional — defaults to the machine's hostname without domain. Must match one of the host names for the master to identify its own screen.
- `logLevel` — `trce`, `dbug`, `info`, `warn`, `fail`, or `crit`
- `logFile` — path to a file where log output is also written (in addition to the console); relative paths are resolved from the config file's directory (default: none)
- `logTruncate` — if `true`, truncate `logFile` to 0 bytes on each startup so it doesn't grow unbounded (default: `false`)
- `autoUpdate` — reserved compatibility field; official builds currently require explicit package updates (default: `false`)
- `controlPort` — loopback scene-control page/API port (default: `24801`)
- `lockFile` — path to a lock file to prevent multiple instances (default: none)
- `monitors` — the desk: every physical monitor, where it sits, and how each computer reaches it (see [The desk](#the-desk)). Written by the settings window; you rarely edit it by hand.
- `profiles` — array of profile objects (see below); at least one required

**Per-profile** (inside a `profiles` entry):

- `profileName` — name for this profile, logged at startup so you know which one is active (no duplicates allowed)
- `mode` — `Master` or `Slave`. Ignored when `controller` is set.
- `controller` — the name of the computer that holds the keyboard and mouse in this scene. When set, each computer derives its own role from it (`controller` runs as master, everyone else as slave), which is what lets a single desk document be shared verbatim between machines and lets control move without rewriting any config. Role-specific keys such as `hideCursor` and `mouseScale` may then travel together in one document; each machine ignores the ones that do not apply to it.
- `networkConfig` — base64 relay config string from the Styx web UI; use this to connect to a standalone Styx server
- `embeddedStyx` — connect using `{ "server": "auto://<desk-name>", "password": "<password>" }` for zero-address-config LAN discovery, or an explicit `http://<host>:<port>` URL
- `embeddedStyxServer` — run a relay in this process: `{ "port": 5000, "password": "<password>", "discoveryName": "studio" }`; `discoveryName` enables link-local advertisements for matching `auto://studio` clients
- `hosts` — list of host entries for the neighbour graph (master only; slaves don't need this)
- `screenDefinitions` — per-screen scale config (slave only; reported to master via ScreenInfo)
- `mouseScale` — fallback cursor speed multiplier for all screens on this slave (slave only)
- `deadCorners` — pixel dead zone at screen corners where transitions are blocked (default `0`, `50` is a reasonable starting value). Scaled by the screen's mouseScale. Can also be set per-host to override.
- `remoteOnly` — `true` to forward all input to remote machines immediately at startup, with no local screen involved (see [Remote-only mode](#remote-only-mode))
- `syncScreensaver` — `false` to disable screensaver synchronisation (default: `true`)
- `conditions` — optional object; if set, this profile only activates when **all** specified conditions are met (see [Network-aware config](#network-aware-config))
- `displayRouting` — physical-monitor input and sleep/wake commands for this named scene
  - `ssid` — activates when connected to this WiFi network name (case-insensitive)
  - `screenCount` — activates when exactly this many screens are connected (integer ≥ 1)
  - `isPluggedIn` — `true` activates when on AC power; `false` activates when on battery

## The desk

The **Monitors** tab of the settings window shows every monitor on the desk — including the ones attached to the other computers — arranged the way it physically stands, the same way the operating system's own display settings do. Drag a monitor to move it; pick a computer on a monitor to switch that monitor's input to it immediately. Saving a named setup afterwards is what turns the arrangement you are looking at into a scene.

Three things make this work, and they are all stored at the root of the config as `monitors`:

```json
"monitors": [
  {
    "id": "aorus-fi27q-x",
    "label": "AORUS FI27Q-X",
    "aliases": ["AORUS FI27Q-X", "AORUS", "Generic PnP Monitor"],
    "deskX": 1512, "deskY": 0, "width": 2560, "height": 1440,
    "sources": [
      { "host": "NINOG", "input": 15, "availableInputs": [1, 3, 15, 17], "ddcId": "\\\\.\\DISPLAY1", "screenId": "\\\\.\\DISPLAY1" },
      { "host": "Mac",   "input": 17, "ddcId": "1", "screenId": "AORUS FI27Q-X" }
    ]
  }
]
```

- `deskX`/`deskY`/`width`/`height` are desk coordinates — the physical layout, independent of which computer is currently on the monitor. The desk never leaves two monitors overlapping.
- `aliases` is every name any computer knows the panel by. This is what makes one monitor one monitor: Windows calls the example above "Generic PnP Monitor", the monitor's own MCCS capabilities string says "AORUS", and macOS says "AORUS FI27Q-X". Names are compared with punctuation and case removed, by containment, and names that identify nothing ("Generic PnP Monitor", "Display", "Monitor") never match anything.
- `sources` records how each computer reaches the monitor: `input` is the MCCS VCP `0x60` value that selects that computer, `availableInputs` is the set the monitor said it accepts, `ddcId` is the identifier that computer's DDC helper answers to, and `screenId` is the name its screen detector reports.
- `input` values are **learned automatically** where they can be. Fill one in by hand only for a computer that cannot read the monitor at all — typically because its DDC helper is not installed.

### Who is on a monitor

`0x60` is a property of the monitor, not of the computer asking: everyone who can read it gets the same answer, the input the monitor is showing. The desk therefore decides as follows.

- **One computer can see the monitor** — it is the one being shown, so the value it reads is the code that selects it. This is how codes learn themselves.
- **Several can see it** (a monitor that keeps inactive inputs alive, or a dock) — the value they read says which input is live, and the computer whose known code matches is the one on screen. Nothing new is learned, because the reading no longer identifies the reader.
- **Nobody can read it over DDC** — the only computer listing it as a screen is on it.

A monitor showing another computer's input usually disappears from the local enumeration entirely, which has two consequences worth knowing:

- The computer that is **not** the active source generally cannot command the monitor. Switching is therefore delegated: the desk asks whichever computer currently drives a monitor to issue the DDC command.
- The `monitors` table is the desk's memory of monitors nobody can currently see. Deleting it loses the learned input codes.

Install the DDC helper on every computer that should be able to switch monitors — `brew install m1ddc` on macOS (Apple Silicon), `sudo apt install ddcutil` on Linux; Windows needs nothing. A computer without one still appears on the desk and can still be switched *to*; it simply cannot learn its own input codes or issue a switch itself.

### When something has gone wrong

The stored desk is what the next start builds on, so a desk that has gone wrong looks exactly like a
bug that will not go away. Two commands exist for that:

```bash
screenfuse --reset   # move the settings aside, keeping a timestamped copy
screenfuse --quit    # stop ScreenFuse, including instances this one did not start
```

On Windows that is `screenfuse.exe` in the folder you extracted, and `--quit` needs an
**Administrator** prompt: ScreenFuse installs as a service registered to restart five seconds after
any failure, so ending the process without stopping the service only delays it. On macOS the binary
lives inside the app bundle, at `/Applications/ScreenFuse.app/Contents/MacOS/screenfuse`.

`--reset` renames `screenfuse.conf`, `.screenfuse-scene` and `.screenfuse-controller` to
`<name>.<timestamp>.bak` in the same directory and leaves them there, so nothing is lost. The next
start builds the desk from scratch.

`--quit` matters most on macOS, where ScreenFuse runs as a background agent with no Dock icon and so
never appears in the Force Quit list. It cancels any pending self-restart, tells launchd to drop the
agent, and stops every running instance. In Activity Monitor the process is named `screenfuse`.

### Switching without DDC

Not every desk can switch inputs, and it does not have to. If ScreenFuse does not know the input code for the computer you pick — or the monitor refuses the switch — it hands the monitor over by **moving the signal instead of the input**: the computer that should appear is woken, the one currently on the monitor stops its video output, and the monitor's own automatic input detection follows. No DDC helper, no input codes, nothing to configure.

Two things to know before relying on it:

- **It is per computer, not per monitor.** Video output is a property of a machine, so every display on both computers moves together. On a desk where one computer drives several monitors, handing one over hands over all of them. ScreenFuse says so in the result message when that applies.
- **The monitor must have automatic input detection enabled.** Most do by default; some scan only on signal loss, and a few need it turned on in their on-screen menu.

DDC is tried first whenever a code is known, because it is exact and affects one monitor. The signal handover is the fallback, and it is why a fresh desk works before anything has been set up.

The crossing edges in `hosts` are **derived** from the arrangement plus each scene's monitor assignments — two monitors that touch on the desk become a crossing only while different computers are on them, and the shared portion of the touching edge becomes the percentage range. Editing `hosts` by hand still works, but the settings window rewrites it whenever the arrangement changes.

## Handing over the keyboard

`controller` names the computer that owns the keyboard and mouse. Changing it in the settings window writes a `.screenfuse-controller` file next to the config on every computer and restarts each agent into its new role; activating a scene clears that override, because a scene names its own controller.

The relay does **not** move with the controller. The computer running `embeddedStyxServer` keeps running it, and its own role simply changes — otherwise handing over the keyboard would drop the connection that carries the handover.

Settings are shared: the controller pushes the desk document to every peer, which merges it with its own identity (`name`, log paths, `controlPort`) and its own relay stanza and pointer tuning, then restarts. There is one place to edit the desk, whichever computer you are sitting at.

## Desk scenes and display routing

Multiple named profiles without `conditions` are selectable desk scenes. Use the local master control page (`screenfuse --setup`) or CLI (`screenfuse --scene "Scene name"`) to activate one. ScreenFuse refuses a manual switch until every host configured in the current topology is connected, broadcasts the selection, persists it, and restarts each agent into the matching local profile.

Each computer defines the same scene names but its own `mode`, topology, and local monitor commands. Run `screenfuse --doctor` on each computer to discover available monitor IDs and helper status.

```json
"displayRouting": {
  "monitors": [
    { "monitor": "benq-xl2420t", "host": "Mac" }
  ],
  "inputs": [
    { "id": "DELL U2720Q", "input": 17 }
  ],
  "wakeDisplays": false,
  "sleepDisplays": false,
  "settleDelayMs": 700
}
```

- `monitors` is the desk-level form: "monitor M shows computer H". It is resolved against the root `monitors` table, and the DDC command is sent to whichever computer currently drives that monitor, so the same scene works from any machine. This is what the settings window writes.
- `inputs` is the older, machine-local form: it sends MCCS VCP `0x60` from *this* computer to matching physical monitors. Use `*` for the default/first monitor. Still supported, and applied before `monitors`.
- `wakeDisplays` or `sleepDisplays` controls all displays on that computer as an auto-input-detection fallback; they are mutually exclusive.
- `settleDelayMs` waits after routing before the coordinated restart (`0`–`10000`).
- Windows IDs are physical description substrings or logical display names; Linux IDs are ddcutil display numbers or `bus:N`; macOS IDs are m1ddc indexes/UUIDs.

## Screen layout

Each entry in `hosts` represents one machine. Declare your neighbours by direction:

```json
{
  "name": "laptop",
  "neighbours": [
    { "direction": "right", "name": "desktop" },
    { "direction": "up",    "name": "tv-box"  }
  ]
}
```

Supported directions: `left`, `right`, `up`, `down`.

**Neighbour options**:

| Field | Default | Description |
|-------|---------|-------------|
| `direction` | required | Which edge of this host triggers the transition |
| `name` | required | Target host name |
| `mirror` | `true` | Auto-create the reverse mapping on the target host (skipped if the reverse already exists) |
| `sourceStart` | `0` | Start of the source edge range (0–100%), inclusive |
| `sourceEnd` | `100` | End of the source edge range (0–100%), inclusive |
| `destStart` | `0` | Start of the destination edge range (0–100%) |
| `destEnd` | `100` | End of the destination edge range (0–100%) |
| `sourceScreen` | `null` | Restrict to a specific local screen — match by `screenName`, `displayName`, `output`, or `platformId` |
| `destScreen` | `null` | Target a specific screen on the remote host — same identifiers as `sourceScreen` |

**Range-based neighbours** let you split an edge to route to different hosts depending on where the cursor crosses:

```json
{
  "name": "laptop",
  "neighbours": [
    { "direction": "right", "name": "workstation", "sourceStart": 0,  "sourceEnd": 50  },
    { "direction": "right", "name": "monitor-host", "sourceStart": 50, "sourceEnd": 100 }
  ]
}
```

When cursor crosses the right edge in the top half (0–50%), it goes to `workstation`; bottom half goes to `monitor-host`.

**Neighbours are mirrored by default** — declaring that `laptop` has `desktop` to the right automatically creates the reverse: `desktop` has `laptop` to its left. You only need to declare one side. Both sides can still be declared explicitly if needed (the mirror is skipped if the reverse already exists).

**Missing hosts**: if a peer is offline, Hydra skips through to the next machine in the same direction (if configured). This lets you maintain a logical layout even when a machine in the middle of the chain is down.

## Dead corners

`deadCorners` defines a pixel dead zone at each corner of the screen where outbound transitions are blocked, regardless of neighbour config. The value is in pixels — `50` means the cursor must be more than 50 pixels away from a corner to trigger a transition. The pixel value is multiplied by the screen's `mouseScale` setting, so a high-DPI screen with `mouseScale: 2` and `deadCorners: 50` gets an effective 100-pixel dead zone.

Set at the profile level to apply to all hosts:

```json
{
  "profiles": [
    { "mode": "Master", "deadCorners": 50, "hosts": [...] }
  ]
}
```

Override per-host (takes precedence over the profile value):

```json
{
  "hosts": [
    {
      "name": "laptop",
      "deadCorners": 80,
      "neighbours": [...]
    }
  ]
}
```

Transitions into a host through a corner are unaffected — dead corners only block outbound transitions.

## Multi-monitor

Local screens are **auto-detected from the OS** — no config is required. On startup, Hydra logs all detected screens with their identifiers:

```
Local screens: 2
  Screen 0: {"screenName":"laptop:0","displayName":"Built-in Retina Display","platformId":"1"}
  Screen 1: {"screenName":"laptop:1","displayName":"DELL U2720Q","output":"HDMI-1","platformId":"2"}
```

Use these identifiers in `sourceScreen`/`destScreen` to target specific monitors in neighbour rules, and in `screenDefinitions` to set per-screen options. Only non-null properties are shown — `output` is omitted on platforms that don't expose connector names.

## Screen definitions

`screenDefinitions` is **slave only**. The slave reports its screen layout and scale settings to the master at connection time; the master applies the scale when routing cursor movement to that slave's screens.

Each entry specifies one or more match criteria — all specified criteria must match (case-insensitive). Use the identifiers shown at startup to build match entries.

```json
{
  "profiles": [
    {
      "mode": "Slave",
      "mouseScale": 1.5,
      "screenDefinitions": [
        { "displayName": "DELL U2720Q",             "mouseScale": 1.5 },
        { "displayName": "Built-in Retina Display", "mouseScale": 1.0 },
        { "outputName": "HDMI-1",                   "mouseScale": 0.8 }
      ]
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `displayName` | — | Match by display/monitor name (e.g. `"DELL U2720Q"`) |
| `outputName` | — | Match by output connector name (e.g. `"HDMI-1"`) |
| `platformId` | — | Match by platform-specific ID |
| `mouseScale` | — | Cursor speed multiplier on this screen; overrides the profile-level `mouseScale` |

The profile-level `mouseScale` sets a fallback multiplier for all screens on this slave. Per-screen `mouseScale` in a `screenDefinitions` entry overrides it. If neither is set, the multiplier defaults to `1.0`.

At least one match field must be set per `screenDefinitions` entry.

## Network-aware config

If you move your machine between networks (e.g. home vs. office), add multiple profiles to `hydra.conf`, each gated on the current network. Hydra picks the matching profile and restarts automatically when the network changes. If no profile matches — for example, you're at a coffee shop — Hydra idles silently. This is intentional: there are no machines to connect to anyway.

Each profile has a `profileName` — logged at startup so you always know which profile is active.

- `conditions: { "ssid": "..." }` — activates when connected to the named WiFi network (case-insensitive)
- `conditions: { "screenCount": 2 }` — activates when exactly 2 screens are connected
- `conditions: { "isPluggedIn": true }` — activates when on AC power; `false` for battery-only
- Conditions are **AND-ed** — `{ "ssid": "Office", "screenCount": 2 }` requires both to match simultaneously
- No `conditions` (or `{}`) — a named manual scene; if several exist, the first is the initial scene until a selection is persisted

A fallback profile is optional. Without one, ScreenFuse idles when no profile matches — e.g. at a coffee shop.

Rules: multiple unconditional profiles are allowed when all have unique names (manual scenes); at most one unnamed fallback is allowed; no two profiles may have identical condition tuples or duplicate names.

Hydra re-evaluates conditions automatically when the network changes, screens are connected/disconnected, or the power source changes, and restarts with the appropriate profile if needed.

**Example — laptop as slave at home, master at work:**

A common setup: at home your stationary desktop controls your laptop (the laptop is a slave). At work, your laptop is docked and controls a dedicated workstation (the laptop is the master).

```json
{
  "name": "laptop",
  "profiles": [
    {
      "profileName": "Home",
      "conditions": { "ssid": "HomeWifi" },
      "mode": "Slave",
      "networkConfig": "<base64 string>"
    },
    {
      "profileName": "Work",
      "conditions": { "ssid": "OfficeWifi" },
      "mode": "Master",
      "networkConfig": "<base64 string>",
      "hosts": [
        {
          "name": "laptop",
          "neighbours": [{ "direction": "right", "name": "workstation" }]
        },
        { "name": "workstation" }
      ]
    }
  ]
}
```

**Example — different layouts for docked vs. laptop-only:**

```json
{
  "name": "laptop",
  "profiles": [
    {
      "profileName": "Office docked",
      "mode": "Master",
      "conditions": { "ssid": "Office", "screenCount": 2 },
      "hosts": [
        { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] }
      ]
    },
    {
      "profileName": "Office undocked",
      "mode": "Master",
      "conditions": { "ssid": "Office", "screenCount": 1 },
      "hosts": [
        { "name": "laptop", "neighbours": [{ "direction": "right", "name": "desktop" }] }
      ]
    },
    { "profileName": "Away", "mode": "Slave" }
  ]
}
```

> **macOS note:** Location Services permission is only requested if at least one config uses `conditions`. Hydra never asks for location permission when running with a single unconditional config.

## Hotkeys

All hotkeys use **Ctrl+Alt+Super** (Super = ⌘ on macOS, Win on Windows) plus one letter.

| Hotkey | Action |
|--------|--------|
| `Ctrl+Alt+Super+L` | Toggle cursor lock — lock to current screen, or unlock to roam freely |
| `Ctrl+Alt+Super+M` | Toggle relative mouse mode on the current remote screen (useful for games) |
| `Ctrl+Alt+Super+C` | Copy selected files/folders to Hydra's cross-machine clipboard (macOS, Windows) |
| `Ctrl+Alt+Super+V` | Paste previously copied files to the current machine |
| `Ctrl+Alt+Super+K` | Lock every connected slave |

**Lock all slaves:** `Ctrl+Alt+Super+K` sends a lock to every connected slave — the same action `screenLockPropagation` performs when the master's own machine locks. It is the only way to trigger it from a **remote-only master**, which has no screen of its own to lock and therefore never fires the underlying event. It is not gated on `screenLockPropagation`: that setting governs automatic propagation, while the hotkey is an explicit request. A slave that has seen local input more recently than the master still declines to lock, on the assumption that someone is sitting at it.

**Lock in remote-only mode:** since there is no local screen, the lock hotkey acts as a **remote toggle** — behaviour depends on whether the machine has a screen of its own.

- **With a local screen** (a desktop or laptop set `remoteOnly`), the hotkey is a remote/local toggle: press once to pass input through to the physical machine running Hydra, press again to re-lock to remote. The OSD reads `Input: local` / `Input: remote`.
- **Headless** (a Pi with no display), there is nothing to pass input *to*, so the hotkey keeps the meaning it has everywhere else: it confines the cursor to the current remote screen, and pressing it again lets the cursor roam between slaves. The OSD reads `Cursor lock: On` / `Cursor lock: Off`. Before this, unlocking on a headless master handed input to a local screen that did not exist and the keyboard and mouse went dead until the hotkey was pressed again.

**Relative mouse:** relative mode sends mouse deltas instead of absolute coordinates — useful for games or 3D apps that capture the cursor. Toggled per-screen; an on-screen notification confirms the current state.

## Clipboard sync

When you move the cursor to a remote machine, Hydra pushes the local clipboard to it. When you move back, the remote clipboard is pulled to the local machine. This happens automatically — no hotkey needed.

Synced content:
- **Plain text** — all platforms
- **Images (PNG)** — all platforms; Windows also handles DIB format for compatibility with legacy apps

Linux syncs both the `CLIPBOARD` selection and the X11 `PRIMARY` (middle-click) selection.

## File transfer

Copy files and folders between machines using the same muscle memory as a local copy/paste.

1. Select files in Finder (macOS) or Explorer (Windows) — including desktop selections
2. Press **Ctrl+Alt+Super+C** — Hydra copies the paths into its transfer buffer and shows a confirmation
3. Move the cursor to the target machine
4. Press **Ctrl+Alt+Super+V** — the files are transferred and placed in the folder currently open in the file manager on the target

The notification shows how many items were copied (e.g. `3 items copied`). Transfers are compressed and verified with a SHA-256 checksum. Only one transfer can be in flight at a time; a progress panel shows speed and allows cancellation. Transfers are aborted automatically if the screensaver activates or the connection drops.

**Platform support:** macOS and Windows. Linux is not supported as a source or destination.

All transfer topologies work: local → remote, remote → local, and remote → remote (via the relay).

## Screensaver sync

When the screensaver activates on the master, Hydra:
- Returns the cursor to the local screen
- Activates the screensaver on all connected slaves

When the master wakes, it deactivates the screensaver on slaves and restores the cursor to the remote screen it was on before.

Set `syncScreensaver: false` in a profile to disable this behaviour.

## Remote-only mode

Remote-only mode turns Hydra into a dedicated input forwarder: 100% of keyboard and mouse input goes to the configured remote machine(s) immediately at startup, with no edge-crossing required. This is useful for setups like a Raspberry Pi as a wireless keyboard/mouse bridge — input is forwarded to a Mac or PC over the network using the Pi's own keyboard layout.

### When to use it

- A headless Linux machine (no monitor, no display server) needs to forward input
- You want a second computer that is always controlled remotely — no toggle, no edge, just instant forwarding
- You want to use a PC keyboard layout on a Mac without installing any software on the Mac (except for Hydra 🙂)

### Configuration

Set `remoteOnly: true` and list the remote host(s). No local entry for the Pi itself is needed.

```json
{
  "name": "pi",
  "profiles": [
    {
      "mode": "Master",
      "remoteOnly": true,
      "networkConfig": "<base64 string>",
      "hosts": [
        { "name": "mac" }
      ]
    }
  ]
}
```

With multiple remote hosts, add neighbours between them so the cursor can transition across hosts:

```json
{
  "name": "pi",
  "profiles": [
    {
      "mode": "Master",
      "remoteOnly": true,
      "networkConfig": "<base64 string>",
      "hosts": [
        {
          "name": "mac",
          "neighbours": [{ "direction": "right", "name": "win" }]
        },
        { "name": "win" }
      ]
    }
  ]
}
```

### Headless Linux (no display server)

On a console-only Linux machine (no `$DISPLAY`), Hydra automatically uses the evdev input subsystem instead of X11. No Xorg or Wayland is needed.

Requirements:
- User must be in the `input` group: `sudo usermod -aG input $USER` (log out and back in for the group change to take effect)
- `libxkbcommon` installed: `sudo apt install libxkbcommon0`
- Set the keyboard layout via `XKB_DEFAULT_LAYOUT` if not `us`, e.g. `XKB_DEFAULT_LAYOUT=gb ./hydra`

> If `$DISPLAY` is set (X11 is running), Hydra uses X11 regardless of `remoteOnly`.

> If no `$DISPLAY` and `remoteOnly` is not set, Hydra exits with an error — it can't capture input without either a display server or remote-only mode.

## Networking with Styx

For machines on different networks, **Styx** is a relay server that securely tunnels Hydra connections. You can run Styx as a **standalone** server (Docker or from source) or **embedded** directly inside a Hydra process.

### Embedded Styx

If you don't want to run a separate Styx container, you can embed a Styx server directly inside a Hydra process. This is ideal for home setups where one machine acts as a hub.

**On the machine that hosts the relay** (e.g. your desktop), add `embeddedStyxServer` to your profile. Hydra will start Styx on the specified port and connect to it automatically:

```json
{
  "name": "desktop",
  "profiles": [
    {
      "mode": "Master",
      "embeddedStyxServer": { "port": 5000, "password": "my-secret" },
      "hosts": [
        { "name": "desktop", "neighbours": [{ "direction": "right", "name": "laptop" }] }
      ]
    }
  ]
}
```

On startup, Hydra logs how other machines should connect:

```
Embedded Styx relay on port 5000
Remote hosts can connect with: embeddedStyx: {"server": "http://<your-ip>:5000", "password": "<password>"}
```

**On each other machine** (master or slave), use `embeddedStyx` with your hub's IP and the same password — no need to copy a base64 blob:

```json
{
  "name": "laptop",
  "profiles": [
    {
      "mode": "Slave",
      "embeddedStyx": { "server": "http://192.168.1.10:5000", "password": "my-secret" }
    }
  ]
}
```

The `embeddedStyx` property is also an alternative to `networkConfig` for any external Styx server — just point it at the server URL and provide the password instead of copying a base64 string.

### Running standalone Styx

```bash
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 ghcr.io/pacanimal/styx:latest
```

Styx listens on port `5000` by default. Override with `LOCAL_PORT`:

```bash
docker run -e RELAY_PASSWORD=<secret> -e LOCAL_PORT=8080 -p 8080:8080 ghcr.io/pacanimal/styx:latest
```

Set `LOCAL_ONLY=true` to bind only to `127.0.0.1` and `::1` — useful when running behind a reverse proxy (HAProxy, nginx, Caddy, etc.) that terminates TLS and forwards to localhost:

```bash
docker run -e RELAY_PASSWORD=<secret> -e LOCAL_ONLY=true -p 127.0.0.1:5000:5000 ghcr.io/pacanimal/styx:latest
```

Or build from source:

```bash
docker build -f Styx/Dockerfile -t styx:local .
docker run -e RELAY_PASSWORD=<secret> -p 5000:5000 styx:local
```

### Generating a network config

Open `http://<your-styx-host>:5000` in a browser, enter the relay password, and click **Generate**. Copy the config string.

### Connecting Hydra to a standalone Styx server

Add `networkConfig` to `hydra.conf` on both machines. Use the same config string on all machines in a network.

**Master** (`hydra.conf`):

```json
{
  "name": "laptop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Master",
      "networkConfig": "<base64 string from the Styx web UI>",
      "hosts": [
        {
          "name": "laptop",
          "neighbours": [{ "direction": "right", "name": "desktop" }]
        }
      ]
    }
  ]
}
```

**Slave** (`hydra.conf`):

```json
{
  "name": "desktop",
  "logLevel": "info",
  "profiles": [
    {
      "profileName": "Home",
      "mode": "Slave",
      "networkConfig": "<same base64 string>"
    }
  ]
}
```

- Both machines must use the **same** network config string.
- Traffic between Hydra instances is end-to-end encrypted — Styx only routes opaque bytes.

## Building from source

```bash
dotnet build Hydra.sln
dotnet test Hydra.sln
```

Publish a self-contained single-file executable:

```bash
dotnet publish Hydra --runtime osx-arm64  --self-contained   # macOS Apple Silicon
dotnet publish Hydra --runtime win-x64   --self-contained   # Windows x64
dotnet publish Hydra --runtime linux-x64 --self-contained   # Linux x64
dotnet publish Hydra --runtime linux-arm64 --self-contained # Linux arm64 (e.g. Raspberry Pi)
```

Output lands in `Hydra/bin/Release/net10.0/<rid>/publish/`.
