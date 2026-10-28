[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must have three numeric components.' }
$packageRoot = Join-Path $projectRoot "artifacts/Harmony-Profiles-$version"
$archive = "$packageRoot.zip"
if ((Test-Path -LiteralPath $packageRoot) -or (Test-Path -LiteralPath $archive)) {
    throw 'Release output already exists. Existing artifacts were preserved.'
}

$files = [ordered]@{
    "src/Harmony-Profiles.Client/bin/$Configuration/netstandard2.1/Harmony-Profiles.Client.dll" = 'BepInEx/plugins/Harmony-Profiles/Harmony-Profiles.Client.dll'
    "src/Harmony-Profiles.Server/bin/$Configuration/net10.0/Harmony-Profiles.Server.dll" = 'SPT_Runtime/user/mods/Harmony-Profiles/Harmony-Profiles.Server.dll'
    'src/Harmony-Profiles.Server/wwwroot/harmony-profiles.css' = 'SPT_Runtime/user/mods/Harmony-Profiles/wwwroot/harmony-profiles.css'
}
foreach ($source in $files.Keys) {
    $path = Join-Path $projectRoot $source
    if (-not (Test-Path -LiteralPath $path)) { throw "Build output is missing: $source" }
    if ([IO.Path]::GetExtension($path) -eq '.dll' -and
        [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString(3) -ne $version) {
        throw "Build output version differs from release: $source"
    }
}
foreach ($source in $files.Keys) {
    $target = Join-Path $packageRoot $files[$source]
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $source) -Destination $target
}
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name.Length -gt 0 })
    if ($entries.Count -ne $files.Count) { throw 'Unexpected packaged file count.' }
    foreach ($entry in $entries) {
        if ($entry.FullName -notin $files.Values) { throw "Unexpected packaged file: $($entry.FullName)" }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $hash.Dispose() }
        $expected = (Get-FileHash -LiteralPath (Join-Path $packageRoot $entry.FullName) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Archive content differs from staged file: $($entry.FullName)" }
    }
}
finally { $zip.Dispose() }
Write-Host "Packaged Harmony-Profiles ${version}: two DLLs and required CSS only."
Write-Host $archive
