# TwinDesk native Windows client

This is the native Windows replacement target, built with WinUI 3 and C#.

## Rendering boundary

The application UI is WinUI/XAML. It does not use Electron, Chromium, HTML, CSS,
JavaScript or a WebView2 control to render its interface, and it does not navigate
to web content. The Windows App SDK brings WebView2 API assemblies transitively.
The app does not instantiate a WebView2 control or bundle the Edge WebView2 browser
runtime. The Windows App SDK may carry the small WebView2 loader library transitively;
that library is not a browser engine and does not render the interface. CI verifies
that the Edge browser executable is not bundled.

## Windows product scope

The native Windows edition targets Windows PC peers and Android phone peers.
macOS, Linux and iOS peer implementations are intentionally excluded from this
Windows target. The Android app and other desktop clients remain separate
repository deliverables.

## Architecture

The protocol, discovery and session services live in Core/TwinDesk.Native.Core.csproj,
a plain .NET 10 library with no WinUI dependency. The WinUI application references
that library. The test project references the core library directly, so protocol
tests do not need to load the Windows UI framework.

## Build

Requirements: Windows 10/11 build 19041 or later, .NET 10 SDK, Windows SDK and
Visual Studio Build Tools with MSBuild.

From the project directory:

    cd windows-native
    dotnet restore TwinDesk.Native.csproj -r win-x64 -p:SelfContained=true
    dotnet publish TwinDesk.Native.csproj -c Release --no-restore -r win-x64 -p:Platform=x64 --self-contained true

For Windows on ARM:

    dotnet restore TwinDesk.Native.csproj -r win-arm64 -p:SelfContained=true
    dotnet publish TwinDesk.Native.csproj -c Release --no-restore -r win-arm64 -p:Platform=ARM64 --self-contained true

The project pins the .NET 10 SDK line through global.json. It is unpackaged and
self-contained. WindowsAppSDKSelfContained=true bundles the Windows App SDK runtime,
and publish --self-contained true bundles the .NET runtime. The resulting ZIP can
be extracted and launched without separately installing the .NET 10 Desktop Runtime
or Windows App SDK runtime. The trade-off is a larger download; Windows App SDK
servicing updates must be incorporated by rebuilding the app.

## Current native functionality

- WinUI 3 lifecycle and native navigation shell.
- Single-instance process guard with abandoned-mutex recovery; a second launch activates the existing window.
- Optional per-user Windows sign-in startup registration.
- Local append-only diagnostics for app lifecycle and native service failures.
- Windows/Android protocol-v1 UDP discovery, TLS WebSocket pairing and authenticated sessions.
- Persistent Windows host identity and paired-device storage.
- Six-digit pairing confirmation and local approval flow.
- Text-only clipboard sync with independent send/receive controls and a 256 KB limit.
- Explicit peer policy: Android and Windows only.
- Separate platform-independent native core library and native-core tests.
- No Electron fallback, embedded browser surface or WebView2 control usage.

File transfer, shared folders, screen capture/viewing, remote input, audio, notifications,
camera, tray lifecycle, updates, installer integration and complete feature parity are
not yet ported. The existing Windows installer remains the supported production build
until those services have native implementations and pass parity tests.

## Build artifacts

The **Build Native Windows** workflow publishes self-contained ZIPs for both
Windows x64 and Windows ARM64. Download the matching `TwinDesk-Windows-x64` or
`TwinDesk-Windows-ARM64` artifact from a successful GitHub Actions run. Extract it
and launch `TwinDesk.exe`.

The ZIP is self-contained and does not require the .NET 10 Desktop Runtime or the
matching Windows App SDK runtime to be installed separately. This is a portable
publish output, not an installer: it does not register an uninstall entry, create
shortcuts, or install prerequisites beyond the files bundled in the ZIP.
