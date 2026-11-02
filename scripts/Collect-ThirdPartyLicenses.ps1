#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedDirectory,
    [string]$AssetsFile = (Join-Path $PSScriptRoot '..\src\TwinDesk.App\obj\project.assets.json'),
    [string]$Runtime,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '..\.artifacts\license-cache')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PublishedDirectory = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$AssetsFile = (Resolve-Path -LiteralPath $AssetsFile).Path
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$licenseDirectory = Join-Path $PublishedDirectory 'licenses'
$null = New-Item -ItemType Directory -Path $licenseDirectory, $CacheDirectory -Force
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Copy-Material([string]$Source, [string]$RelativePath, [string]$SourceUrl, [string]$SourceKind) {
    $destination = [IO.Path]::GetFullPath((Join-Path $licenseDirectory $RelativePath))
    $boundary = [IO.Path]::GetFullPath($licenseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $destination.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "License material destination escapes the licenses directory: $RelativePath"
    }
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($PublishedDirectory, $destination).Replace('\', '/')
        source = $SourceUrl
        sourceKind = $SourceKind
        sha256 = Get-Sha256 $destination
        bytes = (Get-Item -LiteralPath $destination).Length
    }
}

function Get-CachedDownload([string]$Url) {
    $urlBytes = [Text.Encoding]::UTF8.GetBytes($Url)
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($urlBytes)).ToLowerInvariant()
    $cachePath = Join-Path $CacheDirectory ($key + '.txt')
    $recordPath = Join-Path $CacheDirectory ($key + '.json')
    if ((Test-Path -LiteralPath $cachePath -PathType Leaf) -and (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -AsHashtable
        if ($record['url'] -ne $Url -or $record['sha256'] -ne (Get-Sha256 $cachePath)) {
            throw "Cached upstream license content failed its recorded SHA-256 check: $Url"
        }
        return $cachePath
    }
    $temporary = Join-Path $CacheDirectory ($key + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temporary -TimeoutSec 45
        if ((Get-Item -LiteralPath $temporary).Length -eq 0) { throw "Downloaded license is empty: $Url" }
        Move-Item -LiteralPath $temporary -Destination $cachePath -Force
        $record = [ordered]@{ url = $Url; sha256 = Get-Sha256 $cachePath }
        [IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json), $utf8)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    return $cachePath
}

function Get-XmlText($Parent, [string]$Name) {
    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) { return '' }
    return [string]$node.InnerText
}

function Get-UpstreamLicenseUrl([string]$Id, [string]$RepositoryCommit) {
    if ($Id -eq 'MicroCom.Runtime') {
        # Its 0.11.0 nupkg has no source commit. This pinned upstream notice matches its copyright metadata.
        return 'https://raw.githubusercontent.com/kekekeks/MicroCom/cd2fa3c89525fbe7b46bd13f9ec8c77bd829e815/LICENSE'
    }
    if ($RepositoryCommit -notmatch '\A[0-9a-fA-F]{40}\z') { throw "No pinned source commit for package without a license text: $Id" }
    if ($Id -eq 'Avalonia.BuildServices') {
        return "https://raw.githubusercontent.com/AvaloniaUI/Avalonia.BuildServices/$RepositoryCommit/LICENSE"
    }
    if ($Id -eq 'Avalonia' -or $Id.StartsWith('Avalonia.', [StringComparison]::Ordinal)) {
        return "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/$RepositoryCommit/licence.md"
    }
    if ($Id -eq 'Tmds.DBus.Protocol') {
        return "https://raw.githubusercontent.com/tmds/Tmds.DBus/$RepositoryCommit/COPYING"
    }
    throw "Package has no local license text and no verified pinned fallback: $Id"
}

$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$packageFolders = @($assets['packageFolders'].Keys)
$depsFiles = @(Get-ChildItem -LiteralPath $PublishedDirectory -Filter '*.deps.json' -File)
if ($depsFiles.Count -ne 1) { throw 'Expected exactly one application .deps.json in the published directory.' }
$deps = Get-Content -LiteralPath $depsFiles[0].FullName -Raw | ConvertFrom-Json -AsHashtable
$targetName = [string]$deps['runtimeTarget']['name']
$inferredRuntime = ($targetName -split '/')[-1]
if ($inferredRuntime -notmatch '\A(?:win|linux|osx)[a-z0-9.-]*\z') { throw "Cannot determine the published runtime: $targetName" }
if ($Runtime -and $Runtime -ne $inferredRuntime) { throw "Requested runtime $Runtime differs from published runtime $inferredRuntime." }
$Runtime = $inferredRuntime
$target = $deps['targets'][$targetName]
if ($null -eq $target) { throw "Dependency target is missing: $targetName" }

# The published dependency graph determines the selected RID assets. Build-only and other-RID packages are recorded as excluded.
$components = [Collections.Generic.List[object]]::new()
$excluded = [Collections.Generic.List[object]]::new()
foreach ($key in @($target.Keys | Sort-Object)) {
    $library = $deps['libraries'][$key]
    $kind = [string]$library['type']
    if ($kind -notin @('package', 'runtimepack')) { continue }
    $dependency = $target[$key]
    $selectedAssets = [Collections.Generic.List[string]]::new()
    foreach ($group in @('runtime', 'native', 'resources')) {
        if ($dependency.ContainsKey($group)) {
            foreach ($asset in $dependency[$group].Keys) {
                if ([IO.Path]::GetFileName($asset) -ne '_._') { $selectedAssets.Add([string]$asset) }
            }
        }
    }
    if ($dependency.ContainsKey('runtimeTargets')) {
        foreach ($asset in $dependency['runtimeTargets'].Keys) {
            if ($dependency['runtimeTargets'][$asset]['rid'] -eq $Runtime -and [IO.Path]::GetFileName($asset) -ne '_._') {
                $selectedAssets.Add([string]$asset)
            }
        }
    }
    if ($selectedAssets.Count -eq 0) {
        $excluded.Add([ordered]@{ package = $key; reason = 'No runtime, native, or resource asset for the published RID.' })
        continue
    }

    $parts = $key.Split('/')
    if ($parts.Length -ne 2) { throw "Unexpected package identity: $key" }
    $id = $parts[0]
    if ($kind -eq 'runtimepack') { $id = $id -replace '\Aruntimepack\.', '' }
    $version = $parts[1]
    if ($id -notmatch '\A[A-Za-z0-9_.+-]+\z' -or $version -notmatch '\A[A-Za-z0-9_.+-]+\z') {
        throw "Unexpected NuGet package name or version: $key"
    }
    $assetLibrary = $assets['libraries']["$id/$version"]
    $packageRelative = if ($assetLibrary -and $assetLibrary['path']) { [string]$assetLibrary['path'] } else { "$($id.ToLowerInvariant())/$($version.ToLowerInvariant())" }
    $packagePath = $null
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder $packageRelative
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = [IO.Path]::GetFullPath($candidate); break }
    }
    if (-not $packagePath) { throw "Published package is unavailable in the restored NuGet folders: $id/$version" }
    $nuspecs = @(Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File)
    if ($nuspecs.Count -ne 1) { throw "Expected one nuspec for $id/$version" }
    $xml = [xml](Get-Content -LiteralPath $nuspecs[0].FullName -Raw)
    $metadata = $xml.DocumentElement.SelectSingleNode("*[local-name()='metadata']")
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { '' }
    $licenseValue = if ($licenseNode) { [string]$licenseNode.InnerText } else { '' }
    $repositoryUrl = if ($repositoryNode) { $repositoryNode.GetAttribute('url') } else { '' }
    $repositoryCommit = if ($repositoryNode) { $repositoryNode.GetAttribute('commit') } else { '' }
    $lowerId = $id.ToLowerInvariant()
    $lowerVersion = $version.ToLowerInvariant()
    $packageUrl = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
    $relativeBase = "nuget/$id/$version"
    $materials = [Collections.Generic.List[object]]::new()
    $materials.Add((Copy-Material $nuspecs[0].FullName "$relativeBase/metadata.nuspec" ($packageUrl + '#' + $nuspecs[0].Name) 'NuGet package metadata'))
    $localTexts = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object {
        $_.Name -match '\A(?:licen[sc]e(?:[._-].*)?|copying(?:[._-].*)?|notice(?:[._-].*)?|third[._-]party[._-]notices?(?:[._-].*)?|copyright(?:[._-].*)?)\z'
    })
    if ($licenseType -eq 'file') {
        $declared = [IO.Path]::GetFullPath((Join-Path $packagePath $licenseValue))
        $boundary = $packagePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $declared.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $declared -PathType Leaf)) {
            throw "The package-declared license file is missing or outside its package: $id/$version"
        }
        if ($declared -notin $localTexts.FullName) { $localTexts += Get-Item -LiteralPath $declared }
    }
    foreach ($text in $localTexts) {
        $relative = [IO.Path]::GetRelativePath($packagePath, $text.FullName).Replace('\', '/')
        $materials.Add((Copy-Material $text.FullName "$relativeBase/$relative" ($packageUrl + '#' + $relative) 'NuGet package file'))
    }
    $hasLicenseText = @($localTexts | Where-Object { $_.Name -match '\A(?:licen[sc]e|copying)' }).Count -gt 0 -or $licenseType -eq 'file'
    $upstreamNoticeNote = ''
    if (-not $hasLicenseText) {
        if ($licenseType -ne 'expression' -or $licenseValue -ne 'MIT') {
            throw "Published package lacks a locally available license text: $id/$version ($licenseValue)"
        }
        $url = Get-UpstreamLicenseUrl $id $repositoryCommit
        $cached = Get-CachedDownload $url
        $materials.Add((Copy-Material $cached "$relativeBase/UPSTREAM-LICENSE.txt" $url 'Pinned upstream license file'))
        if ($id -eq 'MicroCom.Runtime') {
            $upstreamNoticeNote = 'The nupkg specifies MIT and Copyright 2021 Nikita Tsukanov without a repository commit. The accompanying upstream notice is pinned independently; it is not asserted to identify the package build commit.'
        }
    }
    $sha512File = Join-Path $packagePath "$lowerId.$lowerVersion.nupkg.sha512"
    $packageSha512 = if (Test-Path -LiteralPath $sha512File -PathType Leaf) { (Get-Content -LiteralPath $sha512File -Raw).Trim() } else { '' }
    $components.Add([ordered]@{
        id = $id; version = $version; kind = $kind
        licenseType = $licenseType; license = $licenseValue
        copyright = Get-XmlText $metadata 'copyright'
        repositoryUrl = $repositoryUrl; repositoryCommit = $repositoryCommit
        packageUrl = $packageUrl; packageSha512 = $packageSha512
        publishedAssets = @($selectedAssets.ToArray())
        noticeSourceNote = $upstreamNoticeNote
        materials = @($materials.ToArray())
    })
}

$engineDirectory = Join-Path $PublishedDirectory 'engine'
if (Test-Path -LiteralPath $engineDirectory -PathType Container) {
    $engines = @(Get-ChildItem -LiteralPath $engineDirectory -Filter 'deskflow-core.exe' -File -Recurse)
    if ($engines.Count -ne 1) { throw 'Expected exactly one bundled Deskflow native engine.' }
    $engineRoot = $engines[0].DirectoryName
    $engineVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($engines[0].FullName).FileVersion -split '[^0-9.]')[0]
    $versionParts = $engineVersion.Split('.')
    if ($versionParts.Length -lt 3) { throw "Cannot determine the bundled Deskflow version: $engineVersion" }
    $engineVersion = ($versionParts[0..2] -join '.')
    $engineMaterials = [Collections.Generic.List[object]]::new()
    foreach ($name in @('LICENSE', 'LICENSE_EXCEPTION')) {
        $path = Join-Path $engineRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The official Deskflow package license is missing: $name" }
        $url = "https://raw.githubusercontent.com/deskflow/deskflow/v$engineVersion/$name"
        $engineMaterials.Add((Copy-Material $path "deskflow/$engineVersion/$name" $url 'Unmodified bundled Deskflow file'))
    }
    $components.Add([ordered]@{
        id = 'Deskflow'; version = $engineVersion; kind = 'bundled-engine'
        license = 'GPL-2.0-only WITH LicenseRef-OpenSSL-Exception'
        sourceArchive = "https://github.com/deskflow/deskflow/archive/refs/tags/v$engineVersion.tar.gz"
        packageUrl = "https://github.com/deskflow/deskflow/releases/download/v$engineVersion/deskflow-$engineVersion-win-x64-portable.zip"
        materials = @($engineMaterials.ToArray())
    })
    $qtCore = Join-Path $engineRoot 'Qt6Core.dll'
    if (Test-Path -LiteralPath $qtCore -PathType Leaf) {
        $qtParts = ([Diagnostics.FileVersionInfo]::GetVersionInfo($qtCore).FileVersion -split '[^0-9.]')[0].Split('.')
        if ($qtParts.Length -lt 3) { throw 'Cannot determine the bundled Qt version.' }
        $qtVersion = $qtParts[0..2] -join '.'
        $qtMaterials = [Collections.Generic.List[object]]::new()
        foreach ($name in @('LGPL-3.0-only.txt', 'GPL-3.0-only.txt')) {
            $url = "https://raw.githubusercontent.com/qt/qtbase/v$qtVersion/LICENSES/$name"
            $qtMaterials.Add((Copy-Material (Get-CachedDownload $url) "deskflow-dependencies/Qt/$qtVersion/$name" $url 'Pinned upstream license file'))
        }
        $components.Add([ordered]@{
            id = 'Qt (bundled with Deskflow)'; version = $qtVersion; kind = 'engine-dependency'
            license = 'Upstream LGPL-3.0-only and GPL-3.0-only license texts'
            versionEvidence = 'Qt6Core.dll Windows file version metadata'
            sourceArchive = "https://github.com/qt/qtbase/archive/refs/tags/v$qtVersion.tar.gz"
            scopeNote = 'Supplemental upstream license texts; not a claim to enumerate every third-party notice embedded in the Qt build.'
            materials = @($qtMaterials.ToArray())
        })
    }
    $opensslLibrary = Join-Path $engineRoot 'libcrypto-3-x64.dll'
    if (Test-Path -LiteralPath $opensslLibrary -PathType Leaf) {
        $opensslVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($opensslLibrary).FileVersion -split '[^0-9.]')[0]
        if ($opensslVersion -notmatch '\A\d+\.\d+\.\d+\z') { throw "Cannot determine the bundled OpenSSL version: $opensslVersion" }
        $url = "https://raw.githubusercontent.com/openssl/openssl/openssl-$opensslVersion/LICENSE.txt"
        $material = Copy-Material (Get-CachedDownload $url) "deskflow-dependencies/OpenSSL/$opensslVersion/LICENSE.txt" $url 'Pinned upstream license file'
        $components.Add([ordered]@{
            id = 'OpenSSL (bundled with Deskflow)'; version = $opensslVersion; kind = 'engine-dependency'
            license = 'Apache-2.0'; versionEvidence = 'libcrypto-3-x64.dll Windows file version metadata'
            sourceArchive = "https://github.com/openssl/openssl/archive/refs/tags/openssl-$opensslVersion.tar.gz"
            materials = @($material)
        })
    }
}

$runtimeComponents = @($components.ToArray() | Where-Object { $_['kind'] -eq 'runtimepack' })
foreach ($component in $runtimeComponents) {
    $materialNames = @($component['materials'] | ForEach-Object { [IO.Path]::GetFileName($_['path']) })
    if (@($materialNames | Where-Object { $_ -imatch '\ALICENSE(?:\..*)?\z' }).Count -eq 0 -or
        @($materialNames | Where-Object { $_ -imatch 'THIRD.PARTY.NOTICES' }).Count -eq 0) {
        throw "Self-contained runtime license/notice material is incomplete: $($component['id'])/$($component['version'])"
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    runtime = $Runtime
    dependencyTarget = $targetName
    selection = 'Components with runtime, native, or resource assets in the published dependency target; other-RID and build-only packages are excluded explicitly.'
    nugetPackageCount = @($components.ToArray() | Where-Object { $_['kind'] -eq 'package' }).Count
    runtimePackCount = $runtimeComponents.Count
    components = @($components.ToArray())
    excludedNugetPackages = @($excluded.ToArray())
}
$manifestPath = Join-Path $licenseDirectory 'manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 15), $utf8)
$index = [Text.StringBuilder]::new()
$null = $index.AppendLine('Third-party materials included with this package')
$null = $index.AppendLine("Runtime: $Runtime")
$null = $index.AppendLine('The manifest records package metadata, original license declarations, material source URLs, and SHA-256 checksums.')
foreach ($component in $components) {
    $null = $index.AppendLine('')
    $null = $index.AppendLine("$($component['id']) $($component['version'])")
    $null = $index.AppendLine("License declaration: $($component['license'])")
    foreach ($material in $component['materials']) { $null = $index.AppendLine("  $($material['path'])") }
}
[IO.File]::WriteAllText((Join-Path $licenseDirectory 'INDEX.txt'), $index.ToString(), $utf8)
[PSCustomObject]@{
    ManifestPath = $manifestPath
    Runtime = $Runtime
    NuGetPackages = $manifest['nugetPackageCount']
    RuntimePacks = $manifest['runtimePackCount']
    Components = $components.Count
    MaterialFiles = @($components.ToArray() | ForEach-Object { $_['materials'] }).Count
}
