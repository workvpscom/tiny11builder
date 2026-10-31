# Finalize only an Audit-mode template guest, never the build host.
#Requires -RunAsAdministrator
param([string]$InvocationContext = '')
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'Tiny11Template'
$status = Join-Path $root 'finalization-status.json'
$contextValidated = $false
try {
    if ($PSScriptRoot -ine $root) { throw 'Unexpected finalizer location' }
    if (-not (Test-Path (Join-Path $root 'prepared.json'))) { throw 'Preparation has not completed' }
    $manifestPath = Join-Path $root 'guest-files.json'
    $prepared = Get-Content (Join-Path $root 'prepared.json') -Raw | ConvertFrom-Json
    if ($prepared.ManifestSha256 -ne (Get-FileHash $manifestPath).Hash) { throw 'Prepared manifest identity changed' }
    $bootIdentity = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $root $file.Path))
        if (-not $candidate.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash $candidate).Hash -ne $file.Sha256) { throw 'Finalization payload integrity failed' }
    }
    # Verify payload bytes before executing helpers.
    . (Join-Path $root 'Template-State.ps1')
    if (Test-TemplateRebootOutstanding -State $prepared -CurrentBootIdentity $bootIdentity) { throw 'Installer reboot requirement has not been satisfied' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $marker = Get-Content (Join-Path $root 'preparation-profile.json') -Raw | ConvertFrom-Json
    $context = @{InvocationContext=$InvocationContext;ImageState=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State' -Name ImageState).ImageState;UserSid=$identity.User.Value;Elevated=([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);RootMatches=($PSScriptRoot -ieq $root);MarkerValid=($marker.SchemaVersion -eq 1 -and $marker.Profile -eq 'ProxmoxCloudbaseInit' -and $marker.Lifecycle -eq 'AuditUser');ManifestVerified=$true}
    $context | ConvertTo-Json | Set-Content (Join-Path $root 'finalization-context.json') -Encoding UTF8
    Assert-TemplatePreparationContext -Context $context
    $contextValidated = $true
    if ((Get-Service QEMU-GA).Status -ne 'Running') { throw 'QGA not running' }
    if (-not (Get-TemplateSerialDriverState).Healthy) { throw 'Serial driver identity or health check failed' }
    $storage = Get-TemplateScsiBootDriverState
    $storage | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'scsi-boot-driver-observation.json') -Encoding UTF8
    if (-not $storage.Healthy) { throw 'SCSI boot driver identity or health check failed' }
    if (Request-TemplatePendingReboot -Path (Join-Path $root 'pending-reboot.json') -ManifestSha256 $prepared.ManifestSha256 -BootIdentity $bootIdentity -Pending (Test-TemplateWindowsRebootPending)) {
        @{Stage='RebootRequested';Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
        # Keep the Audit pass resumable rather than returning a terminal installation failure.
        exit 2
    }
    $admin = Get-CimInstance Win32_UserAccount -Filter 'LocalAccount=True' | Where-Object {$_.SID -match '-500$'}
    if ($null -eq $admin -or $admin.Name -ne 'Administrator') { throw 'Built-in Administrator mismatch' }
    $conf = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\conf'
    if (-not (Test-Path (Join-Path $conf 'cloudbase-init.conf')) -or
        (Get-FileHash (Join-Path $conf 'cloudbase-init.conf')).Hash -ne (Get-FileHash (Join-Path $root 'cloudbase-init.conf')).Hash) { throw 'Cloudbase config missing or changed' }
    if ((Get-FileHash (Join-Path $conf 'cloudbase-init-unattend.conf')).Hash -ne
        (Get-FileHash (Join-Path $root 'cloudbase-init-unattend.conf')).Hash) { throw 'Cloudbase unattend configuration changed' }
    $cloudbaseService = Get-CimInstance Win32_Service -Filter "Name='cloudbase-init'"
    if ($null -eq $cloudbaseService -or $cloudbaseService.StartMode -ne 'Disabled' -or $cloudbaseService.State -ne 'Stopped' -or
        $cloudbaseService.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) { throw 'Cloudbase preparation service identity/state mismatch' }
    $plugin = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\Python\Lib\site-packages\tiny11_cloudbase.py'
    if ((Get-FileHash $plugin).Hash -ne (Get-FileHash (Join-Path $root 'tiny11_cloudbase.py')).Hash) { throw 'Installed reconciliation plugin changed' }
    if (Test-Path (Join-Path $root 'day2-state')) { throw 'Template has consumed clone reconciliation state' }
    & (Join-Path $root 'Configure-GuestPolicy.ps1') -VerifyOnly
    $answer = Join-Path $root 'template-unattend.xml'
    if (-not (Test-Path $answer)) { throw 'Final answer file missing' }
    if (@(Get-Process sysprep -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Another Sysprep process is running; retain guest for diagnosis' }
    # Generate a per-VM password without persisting/logging it; clones use ConfigDrive credentials.
    $random = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $secret = ConvertTo-SecureString ([Convert]::ToBase64String($random) + 'aA1!') -AsPlainText -Force
    Set-LocalUser -SID $admin.SID -Password $secret
    $secret = $null
    [Array]::Clear($random, 0, $random.Length)
    # Keep the service Disabled through Sysprep; clone specialize switches it to Auto.
    @{Stage='SysprepStarting';Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    $sysprep = Join-Path $env:WINDIR 'System32\Sysprep\Sysprep.exe'
    $process = Start-Process $sysprep -ArgumentList "/generalize /oobe /shutdown /quiet /unattend:$answer" -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(600000)) { throw 'Sysprep timeout; inspect logs before recovery' }
    $process.WaitForExit()
    if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0) { throw "Sysprep failed: $($process.ExitCode)" }
    @{Stage='SysprepReturned';Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    exit 0
} catch {
    # Prevent diagnostic reboots after failure from consuming preparation metadata.
    if ($contextValidated) { Set-Service cloudbase-init -StartupType Disabled -ErrorAction SilentlyContinue }
    @{Stage='Failed';Error=$_.Exception.Message;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    exit 10
}
