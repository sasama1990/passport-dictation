param(
    [Parameter(Mandatory = $true)]
    [string]$IdfPath,
    [Parameter(Mandatory = $true)]
    [string]$IdfToolsPath,
    [Parameter(Mandatory = $true)]
    [string]$IdfPythonEnvironment
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:IDF_PATH = $IdfPath
$env:IDF_TOOLS_PATH = $IdfToolsPath
$env:IDF_PYTHON_ENV_PATH = $IdfPythonEnvironment
$env:PYTHONIOENCODING = 'utf-8'
$pythonPath = Join-Path $IdfPythonEnvironment 'Scripts\python.exe'
$originalPath = $env:PATH
$exports = & $pythonPath (Join-Path $IdfPath 'tools\idf_tools.py') export --format key-value
if ($LASTEXITCODE -ne 0) { throw 'ESP-IDF environment export failed.' }
foreach ($line in $exports) {
    if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $variableName = $Matches[1]
        $variableValue = $Matches[2].Replace('%PATH%', $originalPath)
        [Environment]::SetEnvironmentVariable($variableName, $variableValue, 'Process')
    }
}

# Give native build tools a short project path. The mapping only points at this
# independent project and is released after the build completes.
$driveLetter = @('V', 'W', 'X', 'Y', 'Z') | Where-Object { -not (Test-Path ($_.ToString() + ':\')) } | Select-Object -First 1
if (-not $driveLetter) { throw 'No temporary drive letter is available.' }
$driveName = $driveLetter + ':'
& subst.exe $driveName $projectRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not create the temporary build path.' }
$originalDirectory = Get-Location
try {
    Set-Location ($driveName + '\firmware')
    & $pythonPath scripts\build.py folotoy/ai-passport --name passport-dictation --language zh-CN
    if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }
}
finally {
    Set-Location $originalDirectory
    & subst.exe $driveName /d
    $env:PATH = $originalPath
}
