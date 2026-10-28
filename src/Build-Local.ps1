[CmdletBinding()]
param(
    [string]$GameRoot = 'D:\401SPT',
    [string]$ServerPath = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ServerPath)) { $ServerPath = Join-Path $GameRoot 'SPT_Runtime' }
foreach ($name in @('Server', 'Client')) {
    $projectDirectory = Join-Path $PSScriptRoot "Harmony-Profiles.$name"
    Push-Location $projectDirectory
    try {
        & dotnet build "Harmony-Profiles.$name.csproj" --configuration $Configuration "-p:GameRoot=$GameRoot" "-p:ServerPath=$ServerPath"
        if ($LASTEXITCODE -ne 0) { throw "Harmony-Profiles.$name build failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }
}
Write-Host 'Both projects built. Nothing was deployed or changed in the game installation.'