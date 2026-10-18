# Policy chỉ thuộc profile opt-in, không thay RTC hoặc cưỡng ép chỉnh clock.
#Requires -RunAsAdministrator
param([switch]$VerifyOnly, [switch]$ActivateRdp)
$ErrorActionPreference = 'Stop'
$terminal = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$rdp = Join-Path $terminal 'WinStations\RDP-Tcp'
$ruleName = 'Tiny11TemplateRdp'
if (-not $VerifyOnly -and $ActivateRdp) {
    Set-ItemProperty $terminal -Name fDenyTSConnections -Value 0
    Set-ItemProperty $rdp -Name UserAuthentication -Value 1
    # User đã duyệt mọi nguồn; không tắt firewall hoặc nhúng địa chỉ lab.
    Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True
    $existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existing) { Remove-NetFirewallRule -Name $ruleName }
    New-NetFirewallRule -Name $ruleName -DisplayName 'Tiny11 Template RDP' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -LocalPort 3389 -RemoteAddress Any | Out-Null
    Set-Service TermService -StartupType Automatic
    Start-Service TermService
}
if (-not $VerifyOnly) {
    # Giữ NTP peer hiện hành; chỉ bật Windows service, không w32tm/resync.
    $parameters = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters'
    if ([string]::IsNullOrWhiteSpace($parameters.NtpServer)) { throw 'Windows time peer policy missing' }
    Set-Service W32Time -StartupType Automatic
    Start-Service W32Time
}
if ($ActivateRdp) {
if ((Get-ItemProperty $terminal).fDenyTSConnections -ne 0 -or
    (Get-ItemProperty $rdp).UserAuthentication -ne 1) { throw 'RDP authentication policy mismatch' }
if (@(Get-NetFirewallProfile | Where-Object { -not $_.Enabled }).Count -gt 0) { throw 'Windows Firewall must remain enabled' }
$rule = Get-NetFirewallRule -Name $ruleName
$port = $rule | Get-NetFirewallPortFilter
$address = $rule | Get-NetFirewallAddressFilter
if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or
    $port.Protocol -ne 'TCP' -or $port.LocalPort -ne '3389' -or $address.RemoteAddress -ne 'Any') { throw 'RDP firewall policy mismatch' }
}
$services = @('W32Time')
if ($ActivateRdp) { $services += 'TermService' }
foreach ($name in $services) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($service.StartMode -ne 'Auto' -or $service.State -ne 'Running') { throw 'Guest policy service unhealthy' }
}
