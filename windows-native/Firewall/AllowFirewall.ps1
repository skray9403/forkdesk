param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "TwinDesk executable not found: $Exe"
}

# Only these two inbound ports are opened, and only for the native TwinDesk executable.
# The current network profile is not changed.
$tcpRule = Get-NetFirewallRule -Name 'TwinDeskNativeTcp47800' -ErrorAction SilentlyContinue
if ($tcpRule) { $tcpRule | Remove-NetFirewallRule -ErrorAction Stop }
New-NetFirewallRule -Name 'TwinDeskNativeTcp47800' -DisplayName 'TwinDesk Native Host (TCP 47800)' -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $Exe -Protocol TCP -LocalPort 47800 | Out-Null

$udpRule = Get-NetFirewallRule -Name 'TwinDeskNativeUdp47801' -ErrorAction SilentlyContinue
if ($udpRule) { $udpRule | Remove-NetFirewallRule -ErrorAction Stop }
New-NetFirewallRule -Name 'TwinDeskNativeUdp47801' -DisplayName 'TwinDesk Native Discovery (UDP 47801)' -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $Exe -Protocol UDP -LocalPort 47801 | Out-Null
