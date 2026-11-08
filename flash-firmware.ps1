param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^COM[0-9]+$')]
    [string]$Port,
    [string]$PythonPath = 'python',
    [string]$FirmwareDirectory = (Join-Path $PSScriptRoot 'Firmware'),
    [string]$BackupDirectory = (Join-Path $PSScriptRoot 'backups')
)

$ErrorActionPreference = 'Stop'
$firmwarePath = (Resolve-Path -LiteralPath $FirmwareDirectory).Path
$requiredFiles = @(
    'bootloader.bin', 'partition-table.bin', 'ota_data_initial.bin',
    'xiaozhi.bin', 'generated_assets.bin'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $firmwarePath $name))) {
        throw "Missing firmware file $name"
    }
}
& $PythonPath -m esptool version
if ($LASTEXITCODE -ne 0) { throw 'esptool is required. Install it with python -m pip install esptool.' }

New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
$backupPath = Join-Path $BackupDirectory ('passport-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bin')
if (Test-Path -LiteralPath $backupPath) { throw 'The backup filename is already in use.' }
Write-Host "Backing up the current 8 MB device image to $backupPath"
& $PythonPath -m esptool --chip esp32c3 --port $Port --baud 460800 `
    read-flash 0x0 0x800000 $backupPath
if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $backupPath).Length -ne 0x800000) {
    throw 'Device backup failed. Flashing was stopped.'
}
Get-FileHash -LiteralPath $backupPath -Algorithm SHA256 | Format-List

# Flash the generated segments at their declared offsets. This preserves the
# existing NVS and PHY partitions between 0x9000 and 0xcfff, and at 0xf000.
Write-Host 'Installing Passport Dictation firmware'
& $PythonPath -m esptool --chip esp32c3 --port $Port --baud 460800 `
    write-flash --flash-mode dio --flash-size 8MB --flash-freq 80m `
    0x0 (Join-Path $firmwarePath 'bootloader.bin') `
    0x8000 (Join-Path $firmwarePath 'partition-table.bin') `
    0xd000 (Join-Path $firmwarePath 'ota_data_initial.bin') `
    0x20000 (Join-Path $firmwarePath 'xiaozhi.bin') `
    0x600000 (Join-Path $firmwarePath 'generated_assets.bin')
if ($LASTEXITCODE -ne 0) { throw "Firmware installation failed. Keep the backup at $backupPath" }
Write-Host "Firmware installed. The previous device image is saved at $backupPath"
