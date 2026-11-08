param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'release\Windows'),
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$portableSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if ($DotnetPath -eq 'dotnet' -and (Test-Path -LiteralPath $portableSdk)) {
    $DotnetPath = $portableSdk
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$buildDirectory = Join-Path $PSScriptRoot '.tools\windows-build\'
& $DotnetPath publish (Join-Path $PSScriptRoot 'windows\PassportDictation.csproj') `
    -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo `
    "-p:BaseOutputPath=$buildDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }
