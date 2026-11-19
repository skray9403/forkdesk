; TwinDesk Native Windows Installer
; High-performance, zero-overhead native WinUI 3 + .NET 10 client
; Copyright (C) 2026 TwinDesk

!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!insertmacro GetSize

!ifndef ARCH
  !define ARCH "x64"
!endif

!ifndef OUTFILE
  !define OUTFILE "artifacts\TwinDesk-Native-Setup-${ARCH}.exe"
!endif

!ifndef SOURCE_DIR
  !define SOURCE_DIR "artifacts\win-${ARCH}"
!endif

!ifndef ICON_PATH
  !define ICON_PATH "installer\icon.ico"
!endif

!ifndef LICENSE_PATH
  !define LICENSE_PATH "..\LICENSE"
!endif

!define APP_NAME "TwinDesk"
!define APP_PUBLISHER "TwinDesk"
!define APP_VERSION "3.3.0"
!define APP_EXE "TwinDesk.exe"

Name "${APP_NAME}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel admin

; Solid LZMA compression for minimal download footprint
SetCompressor /SOLID lzma
SetCompressorDictSize 64

; Interface configuration
!define MUI_ABORTWARNING
!define MUI_ICON "${ICON_PATH}"
!define MUI_UNICON "${ICON_PATH}"

; Installer pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_PATH}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

; Uninstaller pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; Version Information
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (C) 2026 TwinDesk"
VIAddVersionKey "FileDescription" "${APP_NAME} Native Windows Installer (${ARCH})"
VIAddVersionKey "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey "ProductVersion" "${APP_VERSION}.0"

Section "TwinDesk Core" SecCore
  SectionIn RO
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}

  ; Terminate running instances
  nsExec::Exec 'taskkill /F /T /IM ${APP_EXE}'
  Sleep 500

  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"

  ; Register Firewall rules for peer connectivity
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_NAME}" program="$INSTDIR\${APP_EXE}"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${APP_NAME}" dir=in action=allow program="$INSTDIR\${APP_EXE}" profile=any enable=yes'
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_NAME} (TCP 47800)"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${APP_NAME} (TCP 47800)" dir=in action=allow protocol=TCP localport=47800 program="$INSTDIR\${APP_EXE}" profile=any enable=yes'

  ; Create Shortcuts
  SetShellVarContext all
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  ; Create Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Registry configuration
  WriteRegStr HKLM "Software\${APP_NAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME} (Native Windows)"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}

  ; Terminate running instance
  nsExec::Exec 'taskkill /F /T /IM ${APP_EXE}'
  Sleep 500

  ; Remove firewall rules
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_NAME}" program="$INSTDIR\${APP_EXE}"'
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_NAME} (TCP 47800)"'

  ; Remove shortcuts
  SetShellVarContext all
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  ; Remove registry
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
  DeleteRegKey HKLM "Software\${APP_NAME}"

  ; Remove files and installation folder
  RMDir /r "$INSTDIR"
SectionEnd
