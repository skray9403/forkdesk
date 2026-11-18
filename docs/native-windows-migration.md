# Native Windows migration

## Goal

Replace the Windows Electron application with a fully native WinUI 3/C# application.
The Windows product must not embed or launch an Electron, Chromium or HTML renderer,
and it must not instantiate or use a WebView2 control. The Windows App SDK brings
WebView2 API assemblies transitively. The app does not instantiate a WebView2 control,
include the native WebView2 loader, or bundle an Edge WebView2 browser runtime. CI
checks those native runtime boundaries; it does not claim a zero-WebView2 NuGet graph.

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
2. Core process: single-instance activation with abandoned-mutex recovery, durable
   diagnostics, optional per-user sign-in launch and shutdown are implemented.
   Tray lifecycle and notifications still need native implementation and runtime validation.
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
  Verify that neither the native WebView2 loader nor Edge browser runtime is bundled.

## Deployment

The initial target is unpackaged and self-contained so x64/ARM64 ZIPs can run
without a separate .NET Desktop Runtime or Windows App SDK runtime installation.
This increases download size and means Windows App SDK servicing requires a new
app build. Re-evaluate the deployment model when a signed installer is introduced.

## Acceptance criteria for replacing Electron

- No Electron or Chromium renderer process, no HTML/JavaScript UI, and no WebView2
  control instantiated by the Windows product.
- Native WinUI navigation, shell, tray and settings.
- Windows and Android pairing, session recovery and selected feature parity.
- No macOS, Linux or iOS peer implementation shipped in the Windows product.
- The existing Android app pairs and works without incompatible protocol changes.
- Installer upgrade, uninstall, auto-start and user-data preservation verified.
- Before/after benchmarks recorded for idle, connected and active sessions.
