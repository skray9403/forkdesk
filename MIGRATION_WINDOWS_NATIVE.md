# Windows Native Migration (Porting from Electron to WinUI 3 / .NET)

## 1. Executive Summary

This document provides a comprehensive technical overview, architectural rationale, and handoff guide for the migration of **Forkdesk (TwinDesk)** on Windows from the legacy **Electron / Chromium / Node.js** stack to a high-performance, fully native **WinUI 3 (Windows App SDK) + C# (.NET 10)** architecture.

* **Target Stack:** WinUI 3 with Windows App SDK 2.6.5, .NET 10 self-contained (x64 and ARM64).
* **Guarantees:** Zero Electron, zero Chromium runtime, zero Node.js daemon, and zero WebView2 browser control instantiation.
* **Core Separation:** Platform-independent core library (`TwinDesk.Native.Core`) completely decoupled from UI, enabling fast, head-less cross-platform unit testing.
* **Milestone Completed in this Session:**
  1. Merged `main` (including Android Jetpack Compose 1.13.0 & Material 3 Expressive 1.5.0 updates) into `native-windows-lifecycle`.
  2. Implemented the complete **Protocol-v1 Bidirectional File Transfer Engine** (`FileTransferService.cs`) supporting `file.offer`, `file.accept`, `file.refuse`, `file.chunk`, `file.end`, `file.done`, and `file.cancel`.
  3. Built streaming 64 KB chunking with real-time SHA-256 integrity validation and destination filename collision avoidance.
  4. Wired file transfer operations, active transfer progress, and transfer history into `INativeCoreClient`, `NativeCoreClient`, and `NativeCoreSnapshot`.
  5. Built and integrated the native **`FilesPage` (`FilesPage.xaml` / `FilesPage.xaml.cs`)** into WinUI 3 navigation with real-time progress bars, cancel buttons, and Windows Explorer folder reveal.
  6. Built and integrated the native **`AboutPage` (`AboutPage.xaml` / `AboutPage.xaml.cs`)** providing architecture verification, private working set memory diagnostics, device identity, and logs integration.
  7. Added comprehensive unit test coverage (`FileTransferServiceTests.cs`), expanding test suite to **39 passing tests**.
  8. Verified clean build (`0 Warning(s), 0 Error(s)`) against .NET 10.

---

## 2. In-Depth Analysis of the Forkdesk Windows Architecture

### A. The Legacy Windows Implementation (`windows/` & `windows-helper/`)
1. **Electron Shell Overhead:**
   - The original Windows client (`windows/`) runs on Electron. Each instance spawns multiple processes: an Electron browser process, GPU process, network service process, and utility processes (`app.html`, `appview.html`, `monitor.html`, `hud.html`, `phoneview.html`).
   - Idle private working set frequently exceeded 150–250 MB before active connections were even established.
   - Event loop contention: Heavy JSON chunk parsing and file streaming competed directly with the UI thread, causing micro-stutters during pointer motion or video frames.
2. **The C# Helper (`windows-helper/`):**
   - A companion WinForms/Win32 background helper process (`windows-helper/`) already existed to handle low-level Windows APIs (virtual display, screen capture, raw input injection, Wi-Fi Direct, Bluetooth LE, and system notification area tray icon).
   - This proved that the core Windows platform integration could be managed natively and efficiently in C#.

### B. The Native Windows Target Architecture (`windows-native/`)
To eliminate the browser runtime footprint while maintaining complete compatibility with the Android companion app, the Windows native client is structured into three clean layers:

```
+---------------------------------------------------------------+
|                      WinUI 3 Presentation                     |
|  MainWindow, OverviewPage, AndroidPage, FilesPage,            |
|  ClipboardPage, SettingsPage, AboutPage                       |
+---------------------------------------------------------------+
                               | (INativeCoreClient)
+---------------------------------------------------------------+
|                 TwinDesk.Native.Core (.NET 10)                |
|  * UDP Peer Discovery (PeerDiscoveryService)                  |
|  * TLS 1.3 WebSocket Host (TlsWebSocketServer, Port 47800)   |
|  * Pairing & Cryptography (PairingResponder, ProtocolCrypto)   |
|  * Clipboard Sync Engine (ClipboardSyncService)               |
|  * File Transfer Engine (FileTransferService)                 |
|  * Firewall Rule Elevation (NativeFirewallAccessService)      |
+---------------------------------------------------------------+
                               |
+---------------------------------------------------------------+
|                      Operating System / Wire                  |
|  * Windows App SDK / Win32 / Named Pipes                      |
|  * Android Companion (Protocol v1 over TLS WebSocket)         |
+---------------------------------------------------------------+
```

---

## 3. Whole Thinking Process & Architectural Rationale

### A. Strict Zero-Browser Rendering Boundary & Web Asset Purge
* **Decision:** Reject any use of WebView2 controls, HTML layouts, or embedded web frames in the Windows native app, and actively purge transitive web binaries from output bundles.
* **Why:** In previous iterations, developers often used WebView2 as an easy shortcut to embed web views from the Electron app. However, WebView2 still spins up Edge browser processes, consuming 80–120 MB RAM and defeating the primary purpose of migrating to a native binary. Furthermore, even when WebView2 is unused, the `Microsoft.WindowsAppSDK` NuGet package transitively bundles `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Core.Projection.dll`, `WebView2Loader.dll`, and `map.html` (~1.6 MB of dead weight).
* **Execution:**
  1. All surfaces in `windows-native/` are 100% native WinUI 3 XAML controls (`StackPanel`, `Grid`, `Border`, `ProgressBar`, `Button`, `InfoBar`, `NavigationView`).
  2. Engineered an automated MSBuild publish target (`PurgeUnusedWebAssets`) in `TwinDesk.Native.csproj` that purges `Microsoft.Web.WebView2.*`, `WebView2Loader.dll`, and `*.html` from the final published bundle.
  3. Validated via smoke testing that `TwinDesk.exe` launches and initializes its core services with 0 WebView2 DLLs and 0 web runtime processes.
  4. CI (`build-native-windows.yml`) strictly enforces zero WebView2 runtimes, zero loader DLLs, and zero HTML assets in published ZIP artifacts for both x64 and ARM64.

### B. Decoupled Core Library (`TwinDesk.Native.Core`)
* **Decision:** Keep all protocol, discovery, cryptography, and service logic in a standalone .NET class library (`TwinDesk.Native.Core.csproj`) with zero dependencies on WinUI or Windows App SDK.
* **Why:** 
  1. WinUI 3 projects cannot easily run standard `dotnet test` runners in cross-platform or headless CI without specialized test harnesses.
  2. Isolating the core allows pure unit testing of protocol handshakes, SAS code computation, WebSocket frames, clipboard sync, and file streaming directly in milliseconds.
  3. UI presentation binds strictly via `INativeCoreClient` and immutable snapshot events (`NativeCoreSnapshot`).

### C. File Transfer Engine Design (`FileTransferService.cs`)
* **Decision:** Implement protocol-v1 bidirectional file transfer directly on top of the active TLS WebSocket session using 64 KB base64 chunks and streaming SHA-256 validation.
* **Rationale:**
  1. **Compatibility:** The Android companion app natively understands `file.offer`, `file.accept`, `file.chunk`, `file.end`, `file.done`, and `file.cancel`. Implementing this exact protocol ensures seamless interoperability with existing Android phone releases without requiring wire protocol upgrades.
  2. **Memory Safety & Backpressure:** Instead of buffering whole files in memory, `FileTransferService` streams up to 64 KB blocks directly to and from disk. Incoming files write to a temporary `.part` file in `<Downloads>/TwinDesk/.tmp/` until the full transfer completes.
  3. **Integrity Verification:** Uses `System.Security.Cryptography.IncrementalHash` to update the SHA-256 digest on every block as it passes through the network. The file is only moved to the user's download directory after the hash matches the sender's `file.end` digest.
  4. **Collision Avoidance:** Automatically renames incoming duplicate files (e.g., `document (1).pdf`) rather than destructively overwriting existing user data.

---

## 4. Detailed Changes Implemented

### 1. Protocol Definitions (`windows-native/Core/ProtocolV1.cs`)
* Added file transfer message constants:
  - `FileOffer = "file.offer"`
  - `FileAccept = "file.accept"`
  - `FileRefuse = "file.refuse"`
  - `FileChunk = "file.chunk"`
  - `FileEnd = "file.end"`
  - `FileDone = "file.done"`
  - `FileCancel = "file.cancel"`
* Added limits: `FileChunkBytes = 64 KB` and `FileMaxBytes = 16 TiB`.

### 2. Core File Transfer Engine (`windows-native/Core/FileTransferService.cs`)
* Created `FileTransferService` managing active and recent transfers.
* Bidirectional capabilities:
  - **Sender Pipeline:** `StartSendFileAsync` validates file, creates unique GUID, sends `file.offer`, waits for `file.accept`, streams 64 KB base64 chunks with incremental SHA-256 hashing, sends `file.end`, and handles `file.done` confirmation.
  - **Receiver Pipeline:** `HandleFileOfferAsync` checks enabled state and size limits, prepares temp stream, sends `file.accept`, receives `file.chunk` enforcing strict sequence ordering, validates SHA-256 on `file.end`, moves file to download directory, and replies with `file.done`.
  - **Cancellation:** `CancelTransferAsync` immediately terminates ongoing file streams and sends `file.cancel` to the connected peer.

### 3. Core Client & Snapshot Integration
* **`INativeCoreClient.cs`**:
  - Added `SendFileAsync(string filePath, CancellationToken cancellationToken)`.
  - Added `CancelFileTransferAsync(string transferId, CancellationToken cancellationToken)`.
  - Added `SetFileTransferOptionsAsync(FileTransferOptions options, CancellationToken cancellationToken)`.
* **`NativeCoreSnapshot.cs`**:
  - Added `FileTransferSnapshot? Files = null` to snapshot record.
* **`NativeCoreClient.cs`**:
  - Instantiated and owned `FileTransferService`.
  - Routed incoming peer messages of types `file.*` directly to `_files.HandlePeerMessageAsync`.
  - Tied file transfer session lifetime to peer connection state via `SetSessionActiveAsync`.
  - Exposed file transfer operations and populated snapshot.

### 4. Native WinUI 3 User Interface
* **`Pages/FilesPage.xaml` & `FilesPage.xaml.cs`**:
  - Modern WinUI 3 interface with Mica styling.
  - Send File card with file path entry, "Browse…" button (invoking `Windows.Storage.Pickers.FileOpenPicker`), and "Send" button.
  - Transfer settings card with enable/disable toggle and download directory path display.
  - Active transfers card displaying dynamic `ProgressBar`, bytes transferred, percentage, status, and per-item "Cancel" button.
  - Transfer history list displaying past sent/received files with timestamp, status, and "Show in Folder" button to open the file in Windows File Explorer.
* **`Pages/AboutPage.xaml` & `AboutPage.xaml.cs`**:
  - Displays architectural pillars (WinUI 3, 0 Chromium, 0 WebView2, .NET 10 self-contained).
  - Shows host identity, machine name, and active TLS server status (TCP 47800).
  - Displays real-time process diagnostics (architecture, private working set RAM, OS description).
  - "Open Diagnostic Logs Folder" button for quick troubleshooting.
* **`MainWindow.xaml.cs`**:
  - Updated navigation dispatch so selecting `files` navigates to `FilesPage` and selecting `about` navigates to `AboutPage`.

### 5. Unit Test Suite (`windows-native/Tests/FileTransferServiceTests.cs`)
* Added 5 comprehensive test cases covering:
  1. Multi-chunk sender stream with SHA-256 digest validation and completion acknowledgment.
  2. Receiver chunk reassembly and disk persistence verification.
  3. Tampered / corrupt checksum rejection.
  4. Out-of-order chunk sequence handling and automatic cancellation.
  5. Active transfer cancellation and peer notification.

### 6. Zero-Overhead Web Asset Purge (`TwinDesk.Native.csproj` & CI Workflow)
* **MSBuild Target (`PurgeUnusedWebAssets`):**
  Added an automated post-publish filtering target to `TwinDesk.Native.csproj`:
  ```xml
  <Target Name="PurgeUnusedWebAssets" AfterTargets="ComputeFilesToPublish">
    <ItemGroup>
      <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)"
          Condition="'%(FileName)' == 'Microsoft.Web.WebView2.Core'
                  or '%(FileName)' == 'Microsoft.Web.WebView2.Core.Projection'
                  or '%(FileName)' == 'WebView2Loader'
                  or '%(Extension)' == '.html'" />
    </ItemGroup>
  </Target>
  ```
* **CI Assertion Hardening (`build-native-windows.yml`):**
  Added explicit verification steps in GitHub Actions CI to guarantee that no `WebView2Loader.dll` or `*.html` files exist in published artifacts for either x64 or ARM64.

---

## 5. Verification & Test Evidence

### A. Unit Tests
All unit tests in `TwinDesk.Native.Tests` were executed via .NET 10 SDK:

```powershell
dotnet test Tests/TwinDesk.Native.Tests.csproj --configuration Release
```

**Result:**
```
Passed!  - Failed: 0, Passed: 39, Skipped: 0, Total: 39, Duration: 3 s - TwinDesk.Native.Tests.dll (net10.0)
```

### B. Application Compilation
The complete WinUI 3 native desktop project was built using:

```powershell
dotnet build TwinDesk.Native.csproj --configuration Release --no-restore -r win-x64 -p:Platform=x64
```

**Result:**
```
TwinDesk.Native.Core -> F:\forkdesk\windows-native\Core\bin\x64\Release\net10.0\TwinDesk.Native.Core.dll
TwinDesk.Native -> F:\forkdesk\windows-native\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\TwinDesk.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:02:40.02
```

---

## 6. Handoff & Maintenance Guide

For future maintainers continuing the Windows native port:

### 1. Build & Run Commands
Ensure commands are executed with package caches and SDK directed to `F:\`:
```powershell
$env:PATH = "F:\dotnet;" + $env:PATH
$env:DOTNET_ROOT = "F:\dotnet"
$env:NUGET_PACKAGES = "F:\.nuget\packages"
$env:DOTNET_CLI_HOME = "F:\.dotnet"

cd F:\forkdesk\windows-native

# Run core tests:
dotnet test Tests/TwinDesk.Native.Tests.csproj --configuration Release

# Build WinUI 3 application:
dotnet build TwinDesk.Native.csproj --configuration Release -r win-x64 -p:Platform=x64

# Publish self-contained executable:
dotnet publish TwinDesk.Native.csproj --configuration Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts/win-x64
```

### 2. Next Stages on the Roadmap (`docs/native-windows-migration.md`)
* **Stage 4 Continuation (Notifications Relay):**
  - Implement `NotificationRelayService.cs` in `Core/` to receive Android notifications over `notif.post` and dispatch them using Windows native `AppNotification` / Win32 Toast APIs.
  - Create native `NotificationsPage.xaml` for viewing and clearing mirrored phone notifications.
* **Stage 5 (Interactive Features):**
  - Connect `windows-helper/src/Capture.cs` and `windows-helper/src/Inject.cs` into `windows-native/` to enable screen sharing and remote mouse/keyboard control.
* **Stage 6 (Packaging & Installer Architecture):**
  - Designed and implemented a pure native NSIS installer generator (`windows-native/installer/installer.nsi`) and embedded PE application icon (`installer/icon.ico`) into `TwinDesk.exe`.
  - Configured GitHub Actions CI (`build-native-windows.yml`) to automatically compile and publish native single-file installers (`TwinDesk-Native-Setup-x64.exe` and `TwinDesk-Native-Setup-arm64.exe`) with automatic firewall rule configuration and 64-bit uninstaller integration.

---

## 7. Native Executable & Installer Packaging (GitHub CI Pipeline)

### A. Rationale & Problem Statement
* Previously, the GitHub Actions CI workflow `build-native-windows.yml` published output bundles solely as nested ZIP archives (`TwinDesk-windows-x64.zip` uploaded into GitHub artifact `TwinDesk-Windows-x64.zip`).
* This required users to download an archive containing another archive, manually extract 150+ loose assemblies (`coreclr.dll`, WinUI 3 projections, WinAppSDK bootstrappers), and run without desktop integration, start menu shortcuts, or automatic firewall elevation.
* Meanwhile, the legacy Electron workflow (`build-windows.yml`) produced a single `TwinDesk-Setup-*.exe` via NSIS.
* To achieve feature parity and provide a production-ready zero-overhead distribution, a native installer pipeline was engineered for the WinUI 3 / .NET 10 app.

### B. Architecture of the Native Installer (`windows-native/installer/installer.nsi`)
1. **Zero-Web Overhead:** The installer is compiled using Nullsoft Scriptable Install System (NSIS) with solid LZMA compression (`SetCompressor /SOLID lzma`). It does not invoke Electron, Node.js, or web scripts.
2. **Elevated Network Configuration:** Automatically adds inbound firewall rules for `TwinDesk.exe` and TCP port 47800 (used for Android companion TLS discovery & control) so regular users on private and public networks never face blocked connections.
3. **Application Icon Embedding:** Added `<ApplicationIcon>installer/icon.ico</ApplicationIcon>` in `TwinDesk.Native.csproj` so the executable PE header and Taskbar icon show the official branded TwinDesk icon rather than a generic Windows app icon.
4. **Clean Uninstall & Update Lifecycle:**
   - Detects and gracefully terminates running `TwinDesk.exe` processes before installation or uninstallation (`taskkill /F /T /IM TwinDesk.exe`).
   - Writes 64-bit registry entries under `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinDesk` for Windows "Installed Apps" / Control Panel integration.
   - Cleans up firewall rules, Start Menu shortcuts, and installation directory on removal.

### C. CI Pipeline Artifacts Produced
Upon push or `workflow_dispatch` to `main` or `native-windows-*`, the GitHub Actions runner automatically builds and attaches the following downloadable artifacts:
1. `TwinDesk-Native-Setup-x64`: Single-file installer executable (`TwinDesk-Native-Setup-x64.exe`).
2. `TwinDesk-Native-Setup-ARM64`: Single-file installer executable (`TwinDesk-Native-Setup-arm64.exe`).
3. `TwinDesk-x64-exe`: Standalone unpackaged executable (`TwinDesk.exe`).
4. `TwinDesk-ARM64-exe`: Standalone unpackaged executable (`TwinDesk.exe`).
5. `TwinDesk-Windows-x64` / `TwinDesk-Windows-ARM64`: Portable complete runtime zip archives.
