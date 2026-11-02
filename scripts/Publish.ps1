param(
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packages = Join-Path $projectRoot 'artifacts\packages'
$packageName = "TwinDesk-0.1.0-$Runtime"
$destination = Join-Path $packages $packageName
New-Item -ItemType Directory -Path $packages -Force | Out-Null

& dotnet publish (Join-Path $projectRoot 'src\TwinDesk.App\TwinDesk.App.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $destination
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

foreach ($name in @('README.md', '双机测试.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $destination -Force
}

if ($Runtime -eq 'win-x64') {
    $version = '1.27.0'
    $engineFolder = "deskflow-$version-win-x64-portable"
    $engineSource = Join-Path $projectRoot "engine\$engineFolder"
    $archivePath = Join-Path $projectRoot "engine\$engineFolder.zip"
    $expectedHash = '8d19004cc3625623489d96580c1b58fd13ee78185689907fd4478595f93fab9a'
    New-Item -ItemType Directory -Path (Join-Path $projectRoot 'engine') -Force | Out-Null
    if (!(Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        Invoke-WebRequest -Uri "https://github.com/deskflow/deskflow/releases/download/v$version/$engineFolder.zip" -OutFile $archivePath
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) { throw 'Deskflow package SHA-256 mismatch.' }
    # Extract the verified archive again so local engine edits are never silently bundled.
    Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $projectRoot 'engine') -Force
    if (!(Test-Path -LiteralPath (Join-Path $engineSource 'deskflow-core.exe') -PathType Leaf)) { throw 'Verified engine archive has an unexpected layout.' }
    $engineDestination = Join-Path $destination 'engine'
    New-Item -ItemType Directory -Path $engineDestination -Force | Out-Null
    Copy-Item -LiteralPath $engineSource -Destination $engineDestination -Recurse -Force
}

$zipPath = Join-Path $packages "$packageName.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $zipPath -Force
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
