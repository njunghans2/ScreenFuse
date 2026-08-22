# ScreenFuse: build-vs-adopt research

**Decision date:** 22 August 2026
**Audience:** Product and engineering team
**Question:** Does a maintained, vendor-independent tool already combine physical display routing, seamless cross-computer input, clipboard, and file/text transfer across Windows, macOS, and Linux on a LAN?

## Executive answer

No exact product was found. The market has mature software KVMs and capable vendor-specific monitor KVM software, but the intersection remains open: **vendor-independent physical monitor routing + Windows/macOS/Linux + seamless cursor topology + clipboard + file transfer + reusable multi-monitor scenes**.

The closest integrated product is Dell Display and Peripheral Manager Network KVM. Dell documents mouse crossing, clipboard for text/images/files, encrypted file transfer, up to four computers, and input/KVM management, but the feature is limited to supported Dell monitors and Windows/macOS; Dell states that its manager does not work with non-Dell devices. ([Windows documentation](https://www.dell.com/support/kbdoc/en-us/000287285/dell-display-and-peripheral-manager-for-windows), [macOS documentation](https://www.dell.com/support/kbdoc/en-us/000201067/dell-display-and-peripheral-manager-for-macos), [KVM walkthrough](https://www.dell.com/support/contents/en-ed/videos/videoplayer/dell-display-and-peripheral-manager-%7C-kvm-setup/6368990665112))

The decision is therefore to build ScreenFuse, using the open-source Hydra input engine as a base and adding synchronized physical-display scenes, cross-platform DDC adapters, setup/control UX, Linux file transfer, packaging, and safety checks.

## Capability matrix

| Product | Seamless input | Clipboard | File transfer | Physical input routing | Windows/macOS/Linux | Material gap |
|---|---:|---:|---:|---:|---:|---|
| Dell DDPM Network KVM | Yes | Text/images/files | Yes | Yes, supported Dell KVM monitors | Windows + macOS | Dell hardware only; no Linux |
| EasyKVM | Hotkey handoff | Yes | Not documented as general file transfer | DDC from Windows member | Windows + macOS | Two computers/one monitor; no Linux; Mac↔Mac cannot route display |
| Hydra | Cursor edge | Text/images | Windows + macOS | No | All three (Linux X11) | No monitor routing; no Linux files/Wayland |
| Deskflow | Cursor edge | Text/images | Removed | Manual/external scripts | All three | No integrated routing or file drag/drop |
| Synergy 3 | Cursor edge | Clipboard | Windows + macOS claims in newer comparisons | Explicitly manual monitor input | All three | No display routing |
| ShareMouse | Cursor edge | Text/files | Native drag/drop | No monitor sharing | Windows + macOS | No Linux or physical routing |
| Mouse Without Borders | Cursor edge | Yes | Single file, 100 MB limit | No | Windows only | Windows only; no display routing |
| LG Dual Controller | Shared input | Yes | Yes on supported products | Tied to LG monitor/KVM workflow | Windows + macOS | Vendor hardware; no Linux |

### Evidence for closest alternatives

- Dell's current Windows documentation lists input switching, USB KVM, Network KVM, up to four computers, and encrypted file transfer; the same page says DDPM is exclusively for Dell monitors/peripherals. [Dell DDPM for Windows](https://www.dell.com/support/kbdoc/en-us/000287285/dell-display-and-peripheral-manager-for-windows)
- Dell's macOS documentation lists mouse crossing and clipboard sharing for text, images, and files, but only on supported monitors. [Dell DDPM for macOS](https://www.dell.com/support/kbdoc/en-us/000201067/dell-display-and-peripheral-manager-for-macos)
- EasyKVM switches a monitor over DDC and hands keyboard/mouse/audio over the LAN, but supports macOS 13+ and Windows 10/11; its own documentation says DDC is executed by the Windows machine and pure Mac pairs cannot switch the monitor. [EasyKVM input-switch guide](https://easykvm.app/switch-monitor-input-with-keyboard-shortcut.html), [troubleshooting](https://easykvm.app/troubleshooting.html)
- Hydra supports cursor-edge transitions, multi-monitor topologies, encrypted relays, clipboard on all three platforms, and file transfer on Windows/macOS; it explicitly says Linux display mode requires X11 and Wayland is unsupported. [Hydra repository](https://github.com/PacAnimal/hydra)
- Deskflow's own wiki describes combining external display scripts with Deskflow and therefore confirms that physical switching is not integrated. Its maintainer also states drag/drop was removed and recommends LocalSend. [Deskflow display toggling](https://github.com/deskflow/deskflow/wiki/Toggle-displays), [drag/drop discussion](https://github.com/deskflow/deskflow/discussions/8082)
- Synergy's FAQ explicitly says monitor input must be switched manually and Synergy does not support monitor input switching. [Synergy FAQ](https://help.symless.com/hc/en-us/articles/32643108754449-FAQ)
- ShareMouse documents cross-machine cursor movement, clipboard, and Mac/Windows file drag/drop, while calling out that it works “without monitor sharing.” [ShareMouse](https://www.sharemouse.com/)
- Microsoft's current Mouse Without Borders documentation lists clipboard and file transfer but limits it to four Windows computers; known limits include a single file and 100 MB. [Microsoft Learn](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders)
- LG describes Dual Controller as Windows/macOS software for a shared keyboard/mouse; supported LG product material advertises easy file transfer. [LG support](https://www.lg.com/lv/atbalsts/produkcija/lg-32GX870A-B.AEU), [LG Dual Controller guide](https://www.lg.com/content/dam/channel/wcms/it/supporto/guide-soluzioni/download/dual-controller-guide-sw.pdf)

## Technical feasibility and constraints

DDC/CI is the right primary mechanism because MCCS VCP code `0x60` selects a monitor input over the existing video link. Windows exposes this through `SetVCPFeature`; Microsoft warns that many monitors do not fully implement MCCS and recommends physical validation. [Microsoft SetVCPFeature documentation](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)

Linux has a mature implementation in `ddcutil`, which explicitly supports input-source control over `/dev/i2c-*` and provides `libddcutil`. [ddcutil documentation](https://www.ddcutil.com/)

On Apple Silicon, `m1ddc` can set input values and identify displays, but it does not support Intel Macs and some open-source paths omit M1 HDMI support. Monitor, cable, dock, and port behavior varies. [m1ddc documentation](https://github.com/waydabber/m1ddc/blob/main/README.md)

DDC must not be treated as universal. Docks can block it, firmware may omit input selection, and some monitors stop accepting commands from an inactive link. ScreenFuse therefore treats DDC failures as visible per-command results and offers coordinated all-display sleep/wake as an explicit fallback—not a silent claim of compatibility.

Wayland is a separate risk. Deskflow supports recent GNOME/KDE combinations through InputCapture/RemoteDesktop portals and libei, but documents compositor/backend limitations and clipboard gaps. This confirms that a responsible Wayland backend must use portals rather than X11-style global hooks. [Deskflow Wayland status](https://github.com/deskflow/deskflow/discussions/7499)

## Implementation decision

Building a new input-injection engine would duplicate the highest-risk part of the product. Hydra is GPL-2.0, actively released in 2026, self-contained, tested, and already implements topology, low-latency input, clipboard, file streaming, multiple masters, screen detection, and encrypted relay behavior. Reusing it shortens the path while keeping ScreenFuse source available under a compatible license. [Hydra repository and license](https://github.com/PacAnimal/hydra)

ScreenFuse adds:

1. Named desk scenes backed by complete profiles.
2. Reliable master-to-peer scene broadcasts and persistent overrides.
3. Native Windows DDC/CI, Linux `ddcutil`, and bundled macOS `m1ddc` routing.
4. Coordinated sleep/wake fallback.
5. A loopback-only scene picker/API and CLI automation.
6. Linux file URI transfer support and destination handling.
7. Windows/macOS/Linux auto-start and five release targets.
8. Native tray configuration fields, validation, tests, deployment docs, and explicit compatibility limits.
9. Zero-entry LAN pairing with commitment/reveal verification, mutual approval, and automatic desk/topology creation.

## Stop condition and confidence

Research stopped after the closest integrated vendor product, the main commercial/open-source software KVMs, current first-party feature documentation, active repositories, and platform DDC/Wayland constraints were checked. A further broad search was returning combinations of the same two categories (software KVM + separate DDC utility), not a vendor-independent all-platform product. Confidence is **high** that no maintained product found as of 22 August 2026 satisfies the complete specification; confidence can never prove nonexistence, and small unpublished/internal tools may exist.
