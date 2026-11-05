[CmdletBinding()]
param(
    [string]$GameRoot = 'D:\401SPT',
    [string]$ServerPath = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ModsPath = '',
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ServerPath)) { $ServerPath = Join-Path $GameRoot 'SPT_Runtime' }
if ([string]::IsNullOrWhiteSpace($ModsPath)) { $ModsPath = Join-Path $ServerPath 'user\mods\PBT' }
foreach ($name in @('Server', 'Client')) {
    $projectDirectory = Join-Path $PSScriptRoot "PBT.$name"
    Push-Location $projectDirectory
    try {
        & dotnet build "PBT.$name.csproj" --configuration $Configuration "-p:GameRoot=$GameRoot" "-p:ServerPath=$ServerPath"
        if ($LASTEXITCODE -ne 0) { throw "PBT.$name build failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }
}

if ($SkipDeploy) {
    Write-Host 'Both projects built. Deployment skipped.'
    return
}

$buildOutput = Join-Path $PSScriptRoot "PBT.Server\bin\$Configuration\net10.0"
$sources = [ordered]@{
    'PBT.Server.dll' = 'PBT.Server.dll'
    'wwwroot\pbt.css' = 'wwwroot\pbt.css'
    'wwwroot\icon-128.png' = 'wwwroot\icon-128.png'
}
foreach ($image in Get-ChildItem -LiteralPath (Join-Path $buildOutput 'wwwroot\maps') -Filter '*.jpg' -ErrorAction SilentlyContinue) {
    $sources["wwwroot\maps\$($image.Name)"] = "wwwroot\maps\$($image.Name)"
}

foreach ($source in $sources.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildOutput $source))) { throw "Build output is missing: $source" }
}
if (-not (Test-Path -LiteralPath $ModsPath)) { throw "PBT mod folder not found: $ModsPath" }

try {
    foreach ($source in $sources.Keys) {
        $target = Join-Path $ModsPath $sources[$source]
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $buildOutput $source) -Destination $target -Force
    }
}
catch {
    throw "Could not copy into $ModsPath. Close the SPT server and try again. $($_.Exception.Message)"
}

Write-Host "Deployed PBT.Server.dll and wwwroot to $ModsPath"
