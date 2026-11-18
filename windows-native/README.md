# TwinDesk native Windows client

This is the native Windows replacement target, built with WinUI 3 and C#.

## Rendering boundary

There is no Electron, Chromium, WebView2, HTML, CSS or JavaScript UI dependency in this project. Every visible surface is a WinUI/XAML control. This is a migration foundation, not yet a feature-complete replacement for the installed Electron client.

## Windows product scope

The native Windows edition targets Windows PC peers and Android phone peers. macOS, Linux and iOS peer implementations are intentionally excluded from this Windows target. The Android app and other desktop clients remain separate repository deliverables.

## Architecture

The protocol, discovery and session services live in `Core/TwinDesk.Native.Core.csproj`, a plain `.NET 10` library with no WinUI dependency. The WinUI application references that library. The test project references the core library directly, so protocol tests do not need to load the Windows UI framework.

## Build

Requirements: Windows 10/11 build 19041 or later, .NET 10 SDK, Windows SDK and Visual Studio Build Tools with MSBuild.

From the project directory:

    cd windows-native
    dotnet restore TwinDesk.Native.csproj -r win-x64
    dotnet build TwinDesk.Native.csproj -c Release --no-restore -r win-x64 -p:Platform=x64 --self-contained false

For Windows on ARM:

    dotnet restore TwinDesk.Native.csproj -r win-arm64
    dotnet build TwinDesk.Native.csproj -c Release --no-restore -r win-arm64 -p:Platform=ARM64 --self-contained false

The project pins the .NET 10 SDK line through global.json. It is unpackaged and framework-dependent. WindowsPackageType=None enables the Windows App SDK bootstrap auto-initializer. The distributable installer must ensure the matching Windows App SDK runtime and .NET 10 runtime are installed. The runtime is not copied into this project output.

## Current state

- Native WinUI 3 lifecycle and navigation shell.
- Native overview, Android, feature and settings surfaces.
- Explicit peer policy: Android and Windows only.
- UI-facing INativeCoreClient contract for the native session engine.
- A separate platform-independent native core library with protocol-v1 discovery.
- No Electron fallback or embedded web renderer.

Pairing, discovery, file transfer, clipboard, screen, remote input, audio, notifications, camera, tray lifecycle, updates and installer integration are not yet ported. The existing Windows installer remains the supported production build until those services have native implementations and pass parity tests.
