# Dot-sourcing defines functions only; OS reads/mutations require explicit calls.
function Test-TemplateRebootOutstanding {
    param([Parameter(Mandatory=$true)]$State, [Parameter(Mandatory=$true)][string]$CurrentBootIdentity)
    if (-not $State.RebootRequested) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$State.BootIdentity)) { throw 'Reboot state has no boot identity' }
    return [string]$State.BootIdentity -eq $CurrentBootIdentity
}

function Test-TemplateWindowsRebootPending {
    $renames = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
    return (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or
        @($renames | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0
}

function Request-TemplatePendingReboot {
    param([Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$ManifestSha256,
        [Parameter(Mandatory=$true)][string]$BootIdentity,
        [Parameter(Mandatory=$true)][bool]$Pending)
    if (-not $Pending) { return $false }
    $attempts = 0
    if (Test-Path -LiteralPath $Path) {
        $saved = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($saved.SchemaVersion -ne 1 -or $saved.ManifestSha256 -ne $ManifestSha256 -or
            [string]::IsNullOrWhiteSpace([string]$saved.BootIdentity) -or
            ($saved.Attempts -isnot [int] -and $saved.Attempts -isnot [long]) -or $saved.Attempts -lt 1 -or $saved.Attempts -gt 2) {
            throw 'Pending reboot journal identity is invalid'
        }
        if ($saved.BootIdentity -eq $BootIdentity) { return $true }
        $attempts = $saved.Attempts
    }
    if ($attempts -ge 2) { throw 'Windows reboot requirement persisted after two requested reboots' }
    @{SchemaVersion=1;ManifestSha256=$ManifestSha256;BootIdentity=$BootIdentity;Attempts=($attempts+1);Reason='WindowsPendingReboot';Utc=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding UTF8
    return $true
}

function Get-TemplateSerialDriverState {
    $devices = @(Get-CimInstance Win32_PnPEntity | Where-Object {
        $_.PNPDeviceID -match '^PCI\\VEN_1AF4&DEV_(1003|1043)(&|\\)'
    })
    $signed = @(Get-CimInstance Win32_PnPSignedDriver)
    $details = @()
    foreach ($device in $devices) {
        $drivers = @($signed | Where-Object { $_.DeviceID -ieq $device.PNPDeviceID })
        $identityValid = $false
        if ($drivers.Count -eq 1) {
            $driver = $drivers[0]
            if ($driver.InfName -match '^oem\d+\.inf$' -and $driver.DriverVersion -eq '100.93.104.24000' -and
                $driver.DriverProviderName -eq 'Red Hat, Inc.' -and $driver.IsSigned -eq $true -and
                $device.Service -ieq 'VirtioSerial') {
                $inf = Join-Path $env:WINDIR ('INF\' + $driver.InfName)
                $sys = Join-Path $env:WINDIR 'System32\drivers\vioser.sys'
                if ((Test-Path -LiteralPath $inf) -and (Test-Path -LiteralPath $sys)) {
                    $identityValid = (Get-FileHash -LiteralPath $inf).Hash -eq '4A8FCFDEEDBF05BF0B02535ACE5156D5DB5F1C229D30532F88AE477ADF82BEED' -and
                        (Get-FileHash -LiteralPath $sys).Hash -eq 'B6767019C660903B59A3BB8396C40EDA2DE3CDA51CCC121B229050AE28B619FF'
                }
            }
        }
        $details += [pscustomobject]@{DeviceId=$device.PNPDeviceID;ProblemCode=$device.ConfigManagerErrorCode;IdentityValid=$identityValid}
    }
    return [pscustomobject]@{
        Healthy=($details.Count -gt 0 -and @($details | Where-Object { -not $_.IdentityValid -or $null -eq $_.ProblemCode -or $_.ProblemCode -ne 0 }).Count -eq 0)
        Devices=$details
    }
}

function Initialize-TemplateSerialDriver {
    param([Parameter(Mandatory=$true)][string]$Root, [Parameter(Mandatory=$true)][string]$Logs,
        [Parameter(Mandatory=$true)][string]$BootIdentity)
    $journal = Join-Path $Root 'serial-driver-state.json'
    $outstanding = $false
    if (Test-Path -LiteralPath $journal) {
        $saved = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
        $outstanding = Test-TemplateRebootOutstanding -State $saved -CurrentBootIdentity $BootIdentity
    }
    $before = Get-TemplateSerialDriverState
    if ($before.Healthy) {
        $result = [pscustomobject]@{State='AlreadyCurrent';ExitCode=$null;RebootRequired=$outstanding;Observation=$before}
        $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Root 'driver-observation.json') -Encoding UTF8
        return $result
    }
    & pnputil.exe /add-driver (Join-Path $Root 'drivers\vioser.inf') /install *> (Join-Path $Logs 'driver-install.log')
    $code = $LASTEXITCODE
    if ($code -eq 3010) {
        @{RebootRequested=$true;BootIdentity=$BootIdentity} | ConvertTo-Json | Set-Content $journal -Encoding UTF8
    }
    $after = Get-TemplateSerialDriverState
    @{ExitCode=$code;Before=$before;After=$after} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Root 'driver-observation.json') -Encoding UTF8
    if ($code -notin @(0,259,3010)) { throw "Driver install failed: $code" }
    # Exit 259 may mean no matching device; require all postconditions.
    if ($code -ne 3010 -and -not $after.Healthy) { throw "Serial driver postcondition failed after pnputil: $code" }
    return [pscustomobject]@{State='InstallRequested';ExitCode=$code;RebootRequired=($outstanding -or $code -eq 3010);Observation=$after}
}

# Follow the system volume's disk ancestry: unrelated VirtIO controllers must not certify a SATA boot disk.
function Get-TemplateScsiBootDriverState {
    $partition = @(Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':') -ErrorAction Stop)
    if ($partition.Count -ne 1) { throw 'Cannot identify the system volume partition' }
    $disks = @(Get-CimInstance Win32_DiskDrive -Filter ("Index=" + $partition[0].DiskNumber) -ErrorAction Stop)
    if ($disks.Count -ne 1) { throw 'Cannot identify the system disk' }
    $id = [string]$disks[0].PNPDeviceID
    $seen = @{}
    while ($id -notmatch '^PCI\\') {
        if ([string]::IsNullOrWhiteSpace($id) -or $seen.ContainsKey($id)) { throw 'Cannot resolve system disk PCI ancestry' }
        $seen[$id] = $true
        $id = [string](Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_Parent' -ErrorAction Stop).Data
    }
    if ($id -notmatch '^PCI\\VEN_1AF4&DEV_(1004|1048)(&|\\)') {
        return [pscustomobject]@{Required=$false;Healthy=$true;DeviceId=$id;Reason='NonVirtioScsiBootDisk'}
    }
    $devices = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object { $_.PNPDeviceID -ieq $id })
    $drivers = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object { $_.DeviceID -ieq $id })
    $healthy = $false
    if ($devices.Count -eq 1 -and $drivers.Count -eq 1) {
        $device = $devices[0]; $driver = $drivers[0]
        if ($device.ConfigManagerErrorCode -eq 0 -and $null -ne $device.ConfigManagerErrorCode -and
            $device.Service -ieq 'vioscsi' -and $driver.InfName -match '^oem\d+\.inf$' -and
            $driver.DriverVersion -eq '100.93.104.24000' -and $driver.DriverProviderName -eq 'Red Hat, Inc.' -and $driver.IsSigned -eq $true) {
            $inf = Join-Path $env:WINDIR ('INF\' + $driver.InfName)
            $sys = Join-Path $env:WINDIR 'System32\drivers\vioscsi.sys'
            if ((Test-Path -LiteralPath $inf) -and (Test-Path -LiteralPath $sys)) {
                $healthy = (Get-FileHash -LiteralPath $inf).Hash -eq 'E49A966F7313DA4F6130DB00D84B57A167ADA0DE3D877C7CC584A7AED99C5BA2' -and
                    (Get-FileHash -LiteralPath $sys).Hash -eq 'BE1FD2D12FA178D44CCC40548CC160D695A356566414E88976FC33F9F6C5AFA3'
            }
        }
    }
    return [pscustomobject]@{Required=$true;Healthy=$healthy;DeviceId=$id}
}

function Assert-TemplatePreparationContext {
    param([Parameter(Mandatory=$true)]$Context)
    # InvocationContext is a launcher contract, not independent proof of Audit mode.
    if ($Context.InvocationContext -ne 'AuditUser' -or -not $Context.MarkerValid -or
        -not $Context.ManifestVerified -or -not $Context.RootMatches -or -not $Context.Elevated -or
        $Context.UserSid -notmatch '^S-1-5-21-\d+-\d+-\d+-500$') {
        throw 'Template preparation launcher, payload or administrator context is invalid'
    }
}
