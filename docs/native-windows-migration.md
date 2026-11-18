# Native Windows migration

## Goal

Replace the Windows Electron application with a fully native WinUI 3/C# application.
The Windows product must not embed or launch an Electron, Chromium or HTML renderer,
and it must not instantiate or use a WebView2 control. The Windows App SDK WinUI
package depends transitively on the WebView2 SDK package; runtime assets are excluded
from this app's published output when they are not needed. A zero-WebView2 package
graph remains an explicit constraint rather than an unverified claim.

## Windows product scope

The native Windows edition supports Windows PC peers and Android phone peers. It
does not ship macOS, Linux or iOS peer implementations. This is a Windows-product
scope decision, not a repository-wide deletion: Android source, protocol definitions,
tests and other desktop clients remain separate deliverables.

## Architecture

- Presentation: WinUI 3/XAML in windows-native/. No web content or browser runtime.
- Core: plain .NET library in windows-native/Core/, independent of WinUI so protocol
  and discovery tests run cross-platform.
- Windows integration: reuse and evolve windows-helper for tray, notifications,
  capture, input, Bluetooth/Wi-Fi Direct and shell integration.
- Session engine: port the Windows/Android discovery, pairing, identity, authenticated
  transport, file, clipboard and media services behind INativeCoreClient. Do not
  recreate Electron APIs as a compatibility layer.
- Protocol: retain the wire protocol and cryptographic/authentication behavior
  required by Android and Windows peers. Drop macOS/Linux/iOS-specific host
  implementations from the Windows target only after references and tests have
  been migrated.
- Installer: replace Electron NSIS/MSIX/portable packaging only after native feature
  parity and upgrade behavior are verified.

## Migration sequence

1. Foundation: buildable WinUI application target, native navigation and page shells,
   Windows/Android peer policy, and UI-to-core service boundary. Keep the current
   installer untouched.
2. Core process: single-instance activation, durable diagnostics, and optional
   per-user sign-in launch are implemented. Tray lifecycle, notifications and
   crash-safe shutdown still need Windows runtime validation.
3. Android/Windows connectivity: native discovery, pairing approval, identity
   storage, authentication, TLS/session management and Android compatibility tests.
   USB and wireless-debugging paths remain pending.
4. Everyday continuity: native text clipboard synchronization is implemented.
   File transfer, shared folders, phone status and notifications remain pending.
5. Interactive features: port remote input, screen capture/viewing, audio, camera
   and related Windows APIs.
6. Feature completion: port updates, settings, calls, app launching, virtual devices
   and remaining selected modules. Remove the legacy Electron entry point from the
   Windows installer only after parity.
7. Footprint verification: compare tray-idle, connected-idle and active-session
   private bytes, working set, CPU time, cold start and installer footprint on the
   same PC.

## Guardrails

- Never silently fall back to an embedded web renderer.
- Never report a feature as available until its native backend is connected.
- Keep unsupported peer platforms out of Windows pairing lists and reject them at
  the native handshake boundary.
- Preserve Android protocol compatibility and existing pairing identity/settings
  where feasible.
- Do not delete macOS/Linux/iOS folders or workflows from the repository as part of
  this Windows-only migration.
- Keep the Electron installer as a temporary rollback path until feature parity and
  the upgrade path pass acceptance testing.
- Do not instantiate the WinUI WebView2 control or start a browser-rendering process.
  Verify the published dependency graph so unused WebView2 runtime assets stay out
  of the output.

## Deployment

The initial target is unpackaged and framework-dependent to avoid copying a private
Windows App SDK runtime into every installation. The installer must ensure the
matching Windows App SDK and .NET 10 runtimes are present. Microsoft's deployment
documentation identifies framework-dependent deployment as smaller and more
serviceable; self-contained deployment adds runtime files and disk usage.
Re-evaluate after measuring installer output and actual app memory.

## Acceptance criteria for replacing Electron

- No Electron or Chromium renderer process, no HTML/JavaScript UI, and no WebView2
  control instantiated by the Windows product.
- Native WinUI navigation, shell, tray and settings.
- Windows and Android pairing, session recovery and selected feature parity.
- No macOS, Linux or iOS peer implementation shipped in the Windows product.
- The existing Android app pairs and works without incompatible protocol changes.
- Installer upgrade, uninstall, auto-start and user-data preservation verified.
- Before/after benchmarks recorded for idle, connected and active sessions.
