# Windows lightweight shell and flyout

## What changes in this stage

TwinDesk already has a native Windows helper (`windows-helper`): it runs on an STA WinForms message loop and owns the low-level Windows work. The tray is now hosted by that existing process through `NotifyIcon` and `ContextMenuStrip`. The main and Mesh clients continue to own discovery, pairing, connection state, and feature actions; they send a compact, data-only menu model over the helper's existing JSON-line pipe. Clicks return as action IDs and are dispatched back into the existing JavaScript actions.

The menu is a Windows system-rendered shortcut menu. It opens with either left or right click on the notification-area icon. It reuses the existing shared tray menu model, so normal and Mesh modes keep their respective actions, toggles, and connection behavior.

For returning users, the full renderer is no longer created automatically at app startup. It remains lazy and is opened by **Open TwinDesk…**, an incoming pairing request, a deep link, or another existing navigation action. First-run launches still show the full window so onboarding and initial pairing are not hidden. Closing the window continues to leave the background connection services running.

## Why this implementation

- It adds no process: the C# helper already owns a WinForms message loop.
- It adds no framework or package dependency: `NotifyIcon` and `ContextMenuStrip` are in the WinForms assemblies already referenced by `windows-helper/build.cmd`.
- It avoids a second renderer for a tray flyout and does not keep a hidden Chromium page alive.
- It preserves the existing JS protocol, pairing, session, and feature implementations rather than porting them to a second stack.
- The Electron tray remains as a fallback if the native helper is missing or Windows blocks it.

The main Electron window is still Electron when opened. This stage reduces **idle resident UI cost**, not the installed package size; Electron is still required for the complete feature UI.

## Idle helper reductions

The native helper is also tuned for its long-lived tray state:

- Its periodic capture, secure-desktop, and cursor-recovery tick runs every 250 ms while idle (4 Hz), and returns to the existing 100 ms cadence during active capture. Input hooks and raw-input delivery remain event-driven; this timer change does not throttle mouse events.
- The cursor crash-recovery guard is not started at application startup. It is started immediately before the first mouse capture that may hide the Windows system cursor, and the helper waits for the guard's ready signal before hiding it. Keyboard-only capture does not start the guard. Full KVM's separate agent retains its existing unguarded behavior.
- These are reductions in idle polling and idle process count, not measured benchmark results. Verify on the same Windows machine with the same paired/idle state before claiming a numeric CPU or memory saving.

## On-demand Windows startup services

The returning user's tray-only path keeps discovery, pairing, the HTTPS peer host, Android/ADB connectivity, clipboard, remote input, file transfer, and the native helper available. Those are part of the always-ready connection layer and are not disabled by this optimization.

Optional services now follow their existing settings:

- The Chats service is created only when Messages/Chats is enabled.
- The Windows audio service and its hidden audio-window IPC are created only while sending or receiving PC audio is enabled. A lightweight listener remembers the peer's audio policy while the service is unloaded, so enabling audio after pairing still uses the peer's current policy.
- The incoming SSH server status probe is skipped unless **Allow the other computer to open a terminal here** is enabled or an existing key needs cleanup. The setup action loads it explicitly.
- The virtual-display driver and Windows Firewall PowerShell checks are deferred until the main workspace is opened. The tray's explicit screen-sharing and Extend actions still request the display check on demand.

This changes when optional services are initialized; it does not remove their features or the Android companion support. It also does not remove macOS/Linux peer interoperability: those are not packaged as macOS/Linux runtime executables inside the Windows installer in the first place.

The changes are expected to reduce unnecessary startup work and process launches. No RAM/CPU or launch-time percentage is claimed until the same Windows machine is measured before and after.

## Full native UI direction

A complete native rewrite is a separate project, not a safe one-commit swap. The current Windows UI contains feature pages and IPC surfaces for pairing, phone integration, screen sharing, remote input, files, clipboard, audio, notifications, camera, and Mesh. Replacing only the window framework without porting those contracts would make a smaller but incomplete client.

The current least-risk path is:

1. Keep protocol, transport, discovery, crypto, and feature orchestration shared.
2. Continue moving shell and latency-sensitive Windows operations into the existing C# / Win32 helper.
3. Port the high-traffic shell surfaces to native Win32 controls first; retain a lazy feature workspace until each page has a native equivalent.
4. Measure idle and connected process working sets, CPU time, install footprint, and feature parity on the same machine before removing Electron.

WinUI 3 is a viable modern native UI option, but it introduces Windows App SDK deployment/bootstrap choices. A self-contained deployment carries extra framework files; framework-dependent deployment relies on the installed runtime. WebView2 shares the Evergreen runtime with Edge on supported systems, but it is still a web renderer. Neither is an automatic footprint win over a Win32/WinForms shell for a small flyout.

## Verification

The Windows CI build compiles both AnyCPU helper variants and the x64/ARM64 installers. On a Windows device, verify:

1. A returning user starts with the tray icon and no main renderer in Task Manager.
2. Left click and right click both open the system menu.
3. Status and discovered devices refresh without reopening the menu.
4. Checkbox actions update settings and stay in sync after a state change.
5. Connect, disconnect, pairing-code, incoming invite, and **Open TwinDesk…** still open the correct page.
6. Mesh mode exposes the Mesh-specific menu and preserves each action.
7. If the helper is unavailable, the Electron tray fallback remains usable.

For an apples-to-apples resource comparison, capture idle CPU time and private working set for all TwinDesk processes after 2 minutes at the tray, then repeat with the main window open. Record the same numbers with the previous build; do not infer memory savings from source changes alone.
