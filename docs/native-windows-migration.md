# Native Windows migration

## Goal

Replace the Windows Electron application with a fully native WinUI 3/C# application. The production Windows executable must not embed or launch Chromium, Electron, WebView2 or an HTML renderer.

## Windows product scope

The native Windows edition supports Windows PC peers and Android phone peers. It does not ship macOS, Linux or iOS peer implementations. This is a Windows-product scope decision, not a repository-wide deletion: Android source, protocol definitions, tests and other desktop clients remain separate deliverables.

## Architecture

- Presentation: WinUI 3/XAML in windows-native/. No web content or browser runtime.
- Windows integration: reuse and evolve windows-helper for tray, notifications, capture, input, Bluetooth/Wi-Fi Direct and shell integration.
- Session engine: port the Windows/Android discovery, pairing, identity, authenticated transport, file, clipboard and media services behind INativeCoreClient. Do not recreate Electron APIs as a compatibility layer.
- Protocol: retain the wire protocol and cryptographic/authentication behavior required by Android and Windows peers. Drop macOS/Linux/iOS-specific host implementations from the Windows target only after references and tests have been migrated.
- Installer: replace Electron NSIS/MSIX/portable packaging only after native feature parity and upgrade behavior are verified.

## Migration sequence

1. Foundation (this change): add a buildable native application target, native navigation and page shells, a Windows/Android peer policy, and a UI-to-core service boundary. Keep the current installer untouched.
2. Core process: implement native lifecycle, single-instance behavior, logging, settings, auto-start, tray, notifications and crash-safe shutdown.
3. Android/Windows connectivity: port discovery, QR/manual pairing, identity storage, authentication, TLS/session management, reconnection and Android ADB/USB paths. Validate against the existing Android app.
4. Everyday continuity: port clipboard, files, shared folders, phone status and notifications.
5. Interactive features: port remote input, screen capture/viewing, audio, camera and related Windows APIs.
6. Feature completion: port updates, settings, calls, app launching, virtual devices and remaining selected modules. Remove the legacy Electron entry point from the Windows installer only after parity.
7. Footprint verification: compare tray-idle, connected-idle and active-session private bytes, working set, CPU time, cold start and installer footprint on the same PC.

## Guardrails

- Never silently fall back to an embedded web renderer.
- Never report a feature as available until its native backend is connected.
- Keep unsupported peer platforms out of Windows pairing lists and reject them at the native handshake boundary.
- Preserve Android protocol compatibility and existing pairing identity/settings where feasible.
- Do not delete macOS/Linux/iOS folders or workflows from the repository as part of this Windows-only migration.
- Keep the Electron installer as a temporary rollback path until feature parity and the upgrade path pass acceptance testing.

## Deployment

The initial target is unpackaged and framework-dependent to avoid copying a private Windows App SDK runtime into every installation. The installer must ensure the matching Windows App SDK and .NET 10 runtimes are present. Microsoft's deployment documentation identifies framework-dependent deployment as smaller and more serviceable; self-contained deployment adds runtime files and disk usage. Re-evaluate after measuring installer output and actual app memory.

## Acceptance criteria for replacing Electron

- No Electron, Chromium or WebView2 process or package dependency in the Windows product.
- Native WinUI navigation, shell, tray and settings.
- Windows and Android pairing, session recovery and selected feature parity.
- No macOS, Linux or iOS peer implementation shipped in the Windows product.
- The existing Android app pairs and works without incompatible protocol changes.
- Installer upgrade, uninstall, auto-start and user-data preservation verified.
- Before/after benchmarks recorded for idle, connected and active sessions.
