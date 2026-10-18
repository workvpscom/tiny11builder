# Prepare the guest in Audit mode; invoked only by the template profile.
#Requires -RunAsAdministrator
param([string]$InvocationContext = '')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Join-Path $env:ProgramData 'Tiny11Template'
$logs = Join-Path $root 'logs'
$status = Join-Path $root 'preparation-status.json'
$stage = 'Verify preparation context'
$context = $null
$contextValidated = $false
try {
    if ($PSScriptRoot -ine $root) { throw 'Unexpected bootstrap location' }
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    & icacls.exe $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot secure template payload directory' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $setupState = Get-ItemProperty 'HKLM:\SYSTEM\Setup'
    $context = @{InvocationContext=$InvocationContext;ImageState=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State' -Name ImageState).ImageState;UserSid=$identity.User.Value;Elevated=([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);RootMatches=($PSScriptRoot -ieq $root);MarkerValid=$false;ManifestVerified=$false;OOBEInProgress=$setupState.OOBEInProgress;SystemSetupInProgress=$setupState.SystemSetupInProgress}
    $context | ConvertTo-Json | Set-Content (Join-Path $root 'preparation-context.json') -Encoding UTF8
    $bootIdentity = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
    $manifestPath = Join-Path $root 'guest-files.json'
    $manifestHash = (Get-FileHash $manifestPath).Hash
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $root $file.Path))
        if (-not $candidate.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path outside guest payload' }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Payload changed: $($file.Path)" }
    }
    $marker = Get-Content (Join-Path $root 'preparation-profile.json') -Raw | ConvertFrom-Json
    $context.MarkerValid = $marker.SchemaVersion -eq 1 -and $marker.Profile -eq 'ProxmoxCloudbaseInit' -and $marker.Lifecycle -eq 'AuditUser'
    $context.ManifestVerified = $true
    . (Join-Path $root 'Template-State.ps1')
    Assert-TemplatePreparationContext -Context $context
    $contextValidated = $true
    # Quarantine only after validating launcher, payload, and identity.
    $existingCloudbase = Get-Service cloudbase-init -ErrorAction SilentlyContinue
    if ($null -ne $existingCloudbase) {
        Stop-Service cloudbase-init -ErrorAction Stop
        Set-Service cloudbase-init -StartupType Disabled
    }
    if (Test-Path (Join-Path $root 'prepared.json')) {
        $prepared = Get-Content (Join-Path $root 'prepared.json') -Raw | ConvertFrom-Json
        if ($prepared.ManifestSha256 -ne $manifestHash) { throw 'Prepared state belongs to a different manifest' }
        if ((Get-Service QEMU-GA).Status -ne 'Running') { throw 'Prepared state has unhealthy QGA' }
        $installed = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\conf\cloudbase-init.conf'
        if (-not (Test-Path $installed) -or (Get-FileHash $installed).Hash -ne (Get-FileHash (Join-Path $root 'cloudbase-init.conf')).Hash) {
            throw 'Prepared configuration changed'
        }
        if (Test-TemplateRebootOutstanding -State $prepared -CurrentBootIdentity $bootIdentity) { exit 1 }
        if (-not (Get-TemplateSerialDriverState).Healthy) { throw 'Prepared state has unhealthy or unexpected serial driver' }
        $installedPlugin = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\Python\Lib\site-packages\tiny11_cloudbase.py'
        if ((Get-FileHash $installedPlugin).Hash -ne (Get-FileHash (Join-Path $root 'tiny11_cloudbase.py')).Hash) { throw 'Prepared plugin changed' }
        & (Join-Path $root 'Configure-GuestPolicy.ps1') -VerifyOnly
        exit 0
    }
    $catalog = Get-AuthenticodeSignature -LiteralPath (Join-Path $root 'drivers\vioser.cat')
    if ($catalog.Status -ne 'Valid' -or $catalog.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') { throw 'Serial driver catalog is not trusted' }
    $cloudbase = Join-Path $root 'CloudbaseInitSetup_1_1_9_x64.msi'
    $signature = Get-AuthenticodeSignature -LiteralPath $cloudbase
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Cloudbase Solutions Srl(?:,|$)') { throw 'Cloudbase signature is not trusted' }
    $reboot = $false
    $stage = 'Install serial driver'
    $driverState = Initialize-TemplateSerialDriver -Root $root -Logs $logs -BootIdentity $bootIdentity
    $reboot = $driverState.RebootRequired
    if (Test-Path (Join-Path $root 'installer-in-progress.json')) { throw 'Previous MSI status unknown; diagnose before retry' }
    foreach ($package in @(
        @{Name='QGA'; File='qemu-ga-x86_64.msi'; Properties=''},
        @{Name='Cloudbase'; File='CloudbaseInitSetup_1_1_9_x64.msi'; Properties=('USERNAME=Administrator INJECTMETADATAPASSWORD=1 RUN_SERVICE_AS_LOCAL_SYSTEM=1 TRANSFORMS="' + (Join-Path $root 'CloudbaseDisabled.mst') + '"')}
    )) {
        $stage = 'Install ' + $package.Name
        $packageHash = (Get-FileHash (Join-Path $root $package.File)).Hash
        $packageStatePath = Join-Path $root ($package.Name + '-installed.json')
        $msiApi = New-Object -ComObject WindowsInstaller.Installer
        $msiDb = $msiApi.OpenDatabase((Join-Path $root $package.File), 0)
        $msiIdentity = @{}
        foreach ($property in @('ProductCode', 'ProductVersion')) {
            $view = $msiDb.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$property'")
            $view.Execute()
            $record = $view.Fetch()
            if ($null -eq $record) { throw 'MSI identity missing' }
            $msiIdentity[$property] = $record.StringData(1)
            $view.Close()
        }
        $msiDb = $null
        $serviceName = if ($package.Name -eq 'QGA') { 'QEMU-GA' } else { 'cloudbase-init' }
        if (Test-Path $packageStatePath) {
            $saved = Get-Content $packageStatePath -Raw | ConvertFrom-Json
            if ($saved.Sha256 -ne $packageHash -or $saved.ProductCode -ne $msiIdentity.ProductCode -or
                $saved.ProductVersion -ne $msiIdentity.ProductVersion) { throw 'Package journal belongs to different identity' }
            if ($msiApi.ProductState($msiIdentity.ProductCode) -ne 5 -or
                $msiApi.ProductInfo($msiIdentity.ProductCode, 'VersionString') -ne $msiIdentity.ProductVersion) { throw 'Installed MSI identity mismatch' }
            $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
            $expectedPath = if ($package.Name -eq 'QGA') { Join-Path $env:ProgramFiles 'Qemu-ga\qemu-ga.exe' } else { Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\bin\OpenStackService.exe' }
            if ($null -eq $service -or $service.PathName -notlike ('*' + $expectedPath + '*') -or
                -not (Test-Path $expectedPath) -or $service.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) { throw 'Installed service identity mismatch' }
            $reboot = $reboot -or ([bool]$saved.RebootRequired -and $saved.BootIdentity -eq $bootIdentity)
            continue
        }
        $arguments = '/i "' + (Join-Path $root $package.File) + '" /qn /norestart REBOOT=ReallySuppress ' + $package.Properties + ' /l*v "' + (Join-Path $logs ($package.Name + '-install.log')) + '"'
        if (Test-Path (Join-Path $root 'installer-in-progress.json')) { throw 'Previous MSI status unknown; diagnose before retry' }
        @{Package=$package.Name;State='Starting'} | ConvertTo-Json | Set-Content (Join-Path $root 'installer-in-progress.json') -Encoding UTF8
        $process = Start-Process msiexec.exe -ArgumentList $arguments -PassThru
        $null = $process.Handle
        @{Package=$package.Name;Pid=$process.Id;State='Running'} | ConvertTo-Json | Set-Content (Join-Path $root 'installer-in-progress.json') -Encoding UTF8
        if (-not $process.WaitForExit(600000)) { throw 'MSI timeout; retain guest and diagnose installer before retry' }
        $process.WaitForExit()
        $code = $process.ExitCode
        if ($null -eq $code -or $code -notin @(0,3010)) { throw "$($package.Name) installation failed: $code" }
        $reboot = $reboot -or $code -eq 3010
        @{Name=$package.Name;Sha256=$packageHash;ProductCode=$msiIdentity.ProductCode;ProductVersion=$msiIdentity.ProductVersion;ExitCode=$code;RebootRequired=($code -eq 3010);BootIdentity=$bootIdentity;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $packageStatePath -Encoding UTF8
        Remove-Item -LiteralPath (Join-Path $root 'installer-in-progress.json')
    }
    $stage = 'Configure guest policy'
    & (Join-Path $root 'Configure-GuestPolicy.ps1')
    $stage = 'Configure agents'
    Stop-Service cloudbase-init -ErrorAction Stop
    Set-Service cloudbase-init -StartupType Disabled
    $conf = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\conf'
    Copy-Item (Join-Path $root 'cloudbase-init.conf') (Join-Path $conf 'cloudbase-init.conf') -Force
    Copy-Item (Join-Path $root 'cloudbase-init-unattend.conf') (Join-Path $conf 'cloudbase-init-unattend.conf') -Force
    $pythonRoot = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\Python'
    $plugin = Join-Path $pythonRoot 'Lib\site-packages\tiny11_cloudbase.py'
    Copy-Item (Join-Path $root 'tiny11_cloudbase.py') $plugin -Force
    if ((Get-FileHash $plugin).Hash -ne (Get-FileHash (Join-Path $root 'tiny11_cloudbase.py')).Hash) { throw 'Installed reconciliation plugin integrity failed' }
    $stage = 'Import reconciliation plugin'
    $import = Start-Process (Join-Path $pythonRoot 'python.exe') -ArgumentList '-c "import tiny11_cloudbase; assert tiny11_cloudbase.ProxmoxReconcilePlugin"' -PassThru -NoNewWindow
    $null = $import.Handle
    if (-not $import.WaitForExit(60000)) {
        $import.Kill()
        throw 'Cloudbase reconciliation plugin import timed out after 60 seconds'
    }
    if ($import.ExitCode -ne 0) { throw 'Cloudbase reconciliation plugin import failed' }
    if (Test-Path (Join-Path $root 'day2-state')) { throw 'Template has consumed clone reconciliation state' }
    Set-Service QEMU-GA -StartupType Automatic
    Start-Service QEMU-GA
    $admin = Get-CimInstance Win32_UserAccount -Filter 'LocalAccount=True' | Where-Object {$_.SID -match '-500$'}
    if ($null -eq $admin -or $admin.Name -ne 'Administrator') { throw 'Built-in Administrator identity mismatch' }
    @{Stage='Installed';ManifestSha256=$manifestHash;RebootRequested=$reboot;BootIdentity=$bootIdentity;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $root 'prepared.json') -Encoding UTF8
    @{Stage='Prepared';Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    # WillReboot=OnRequest: exit 1 requests a Setup reboot before the next command.
    if ($reboot) { exit 1 }
    exit 0
} catch {
    if ($contextValidated) {
        Set-Service cloudbase-init -StartupType Disabled -ErrorAction SilentlyContinue
        Stop-Service cloudbase-init -ErrorAction SilentlyContinue
    }
    @{Stage=$stage;State='Failed';Error=$_.Exception.Message;Context=$context;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    # Do not use 1/2 for errors: they are reboot request codes.
    exit 10
}
