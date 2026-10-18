# Dot-sourcing defines functions only; OS reads/mutations require explicit calls.
function Test-TemplateRebootOutstanding {
    param([Parameter(Mandatory=$true)]$State, [Parameter(Mandatory=$true)][string]$CurrentBootIdentity)
    if (-not $State.RebootRequested) { return $false }
    if ([string]::IsNullOrWhiteSpace([string]$State.BootIdentity)) { throw 'Reboot state has no boot identity' }
    return [string]$State.BootIdentity -eq $CurrentBootIdentity
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

function Assert-TemplatePreparationContext {
    param([Parameter(Mandatory=$true)]$Context)
    # InvocationContext is a launcher contract, not independent proof of Audit mode.
    if ($Context.InvocationContext -ne 'AuditUser' -or -not $Context.MarkerValid -or
        -not $Context.ManifestVerified -or -not $Context.RootMatches -or -not $Context.Elevated -or
        $Context.UserSid -notmatch '^S-1-5-21-\d+-\d+-\d+-500$') {
        throw 'Template preparation launcher, payload or administrator context is invalid'
    }
}
