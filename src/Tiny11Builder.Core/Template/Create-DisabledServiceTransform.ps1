# Biến đổi MSI database copy; không chạy installer hoặc service.
param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$temporary=$Destination+'.database.msi'
try {
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne 'FFBF80515A7311ED00800276A6AF7AAB23B41D7ACF13E79CE3D2D56844F638F7') { throw 'Unexpected MSI identity' }
    if ((Test-Path $Destination) -or (Test-Path $temporary)) { throw 'Transform destination exists' }
    Copy-Item -LiteralPath $Source -Destination $temporary
    (Get-Item $temporary).IsReadOnly=$false
    $installer=New-Object -ComObject WindowsInstaller.Installer
    $original=$installer.OpenDatabase($Source,0)
    $modified=$installer.OpenDatabase($temporary,1)
    $view=$modified.OpenView("SELECT ``Name``,``StartType`` FROM ``ServiceInstall``")
    $view.Execute()
    $record=$view.Fetch()
    if ($null -eq $record -or $record.StringData(1) -ne 'cloudbase-init' -or $record.IntegerData(2) -ne 2 -or $null -ne $view.Fetch()) { throw 'Unexpected service installation schema' }
    $view.Close()
    $view=$modified.OpenView("UPDATE ``ServiceInstall`` SET ``StartType``=4 WHERE ``Name``='cloudbase-init'")
    $view.Execute()
    $view.Close()
    $modified.Commit()
    if (-not $modified.GenerateTransform($original,$Destination)) { throw 'Transform generation failed' }
    $modified.CreateTransformSummaryInfo($original,$Destination,0,0)
    if ((Get-FileHash -LiteralPath $Source).Hash -ne 'FFBF80515A7311ED00800276A6AF7AAB23B41D7ACF13E79CE3D2D56844F638F7') { throw 'Source MSI changed' }
    exit 0
} catch { Write-Error $_; exit 10 }
finally {
    $modified=$null
    $original=$null
    $installer=$null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    if (Test-Path $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue }
}
