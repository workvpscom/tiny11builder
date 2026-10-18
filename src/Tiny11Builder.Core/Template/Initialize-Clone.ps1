# Called only by clone specialize; do not wait for main provisioning to finish.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'Tiny11Template'
$status = Join-Path $root 'clone-initialization-status.json'
$stage = 'Verify clone payload'
try {
    if ($PSScriptRoot -ine $root) { throw 'Unexpected clone initializer location' }
    $manifest = Get-Content (Join-Path $root 'guest-files.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $root $file.Path))
        if (-not $candidate.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $candidate).Hash -ne $file.Sha256) { throw 'Clone payload integrity failed' }
    }
    $stage = 'Verify clone service'
    $conf = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\conf'
    foreach ($name in @('cloudbase-init.conf', 'cloudbase-init-unattend.conf')) {
        if ((Get-FileHash (Join-Path $conf $name)).Hash -ne (Get-FileHash (Join-Path $root $name)).Hash) { throw 'Clone configuration changed' }
    }
    $wrapper = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\bin\OpenStackService.exe'
    $service = Get-CimInstance Win32_Service -Filter "Name='cloudbase-init'"
    if ($null -eq $service -or $service.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM') -or
        $service.PathName -notlike ('"' + $wrapper + '" *') -or
        (Get-FileHash $wrapper).Hash -ne 'E141FE1CCF2ACF9DF052894D25D8DF4A219F51C0A21D9C8156C34C6CBCA11425') {
        throw 'Clone service identity mismatch'
    }
    # Prevent main plugins from racing network-unattend.
    Stop-Service cloudbase-init -ErrorAction Stop
    Set-Service cloudbase-init -StartupType Disabled
    $plugin = Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\Python\Lib\site-packages\tiny11_cloudbase.py'
    if ((Get-FileHash $plugin).Hash -ne (Get-FileHash (Join-Path $root 'tiny11_cloudbase.py')).Hash) { throw 'Clone reconciliation plugin integrity failed' }
    # Local ConfigDrive has one main owner for network, hostname, and password after Setup.
    $stage = 'Activate main service'
    Set-Service cloudbase-init -StartupType Automatic
    # StartServiceW returns after acknowledgement, not after child plugins finish.
    $controller = New-Object System.ServiceProcess.ServiceController 'cloudbase-init'
    try {
        $controller.Start()
        $controller.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))
    } finally { $controller.Dispose() }
    @{Stage='MainServiceStarted';ReconciliationOwner='CloudbaseExtension';NetworkVerified=$false;ProvisioningVerified=$false;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    exit 0
} catch {
    @{Stage=$stage;State='Failed';Error=$_.Exception.Message;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content $status -Encoding UTF8
    exit 10
}
