param(
    [string]$Version = '0.1.0',
    [string]$PublishedDirectory,
    [string]$OutputDirectory,
    [string]$WixPath
)

# Official references:
# https://www.nuget.org/packages/wix/5.0.2
# https://docs.firegiant.com/wix/schema/wxs/packagescopetype/
# https://docs.firegiant.com/wix/schema/wxs/component/
# https://learn.microsoft.com/en-us/windows/win32/msi/installer-opendatabase
# This script builds and reads an MSI. It never installs, uninstalls, or stops TwinDesk.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Build-WindowsInstaller.ps1 requires Windows and PowerShell 7.' }
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$' -or [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw 'Version must be a three-part MSI version: major 0-255, minor 0-255, build 0-65535.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishedDirectory)) { $PublishedDirectory = Join-Path $projectRoot "artifacts\packages\TwinDesk-$Version-win-x64" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'artifacts\installers' }
$PublishedDirectory = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publishBoundary = $PublishedDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($OutputDirectory.Equals($PublishedDirectory, [StringComparison]::OrdinalIgnoreCase) -or $OutputDirectory.StartsWith($publishBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must not be inside PublishedDirectory.'
}
$toolDirectory = Join-Path $projectRoot '.artifacts\tools\wix'
$wix = if ([string]::IsNullOrWhiteSpace($WixPath)) { Join-Path $toolDirectory 'wix.exe' } else { [IO.Path]::GetFullPath($WixPath) }
$buildDirectory = Join-Path $projectRoot ('.artifacts\installer\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$harvestPath = Join-Path $buildDirectory 'PublishedPayload.wxs'
$sourcePath = Join-Path $projectRoot 'installer\TwinDesk.wxs'
$iconPath = Join-Path $projectRoot 'src\TwinDesk.App\Assets\tray.ico'
$msiPath = Join-Path $OutputDirectory "TwinDesk-$Version-win-x64.msi"
$verificationPath = Join-Path $OutputDirectory "TwinDesk-$Version-win-x64-verification.json"
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StableSuffix([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant()))).ToLowerInvariant().Substring(0, 32)
}

function Get-DirectoryId([string]$RelativeDirectory) {
    if ([string]::IsNullOrEmpty($RelativeDirectory)) { return 'INSTALLFOLDER' }
    return 'dir_' + (Get-StableSuffix ('directory/' + $RelativeDirectory.Replace('\', '/')))
}

function Release-ComObject($Value) {
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value) | Out-Null
    }
}

function Get-MsiRows($Database, [string]$Sql, [string[]]$Columns) {
    $view = $Database.OpenView($Sql)
    try {
        $view.Execute() | Out-Null
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Count; $index++) {
                    $row[$Columns[$index]] = $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($index + 1))
                }
                [pscustomobject]$row
            }
            finally { Release-ComObject $record }
        }
    }
    finally { $view.Close() | Out-Null; Release-ComObject $view }
}

foreach ($required in @('TwinDesk.App.exe', 'TwinDesk.App.dll', 'TwinDesk.Core.dll', 'TwinDesk.App.deps.json', 'TwinDesk.App.runtimeconfig.json', 'README.md', '双机测试.md', 'THIRD-PARTY-NOTICES.md', 'engine\deskflow-1.27.0-win-x64-portable\deskflow-core.exe', 'engine\deskflow-1.27.0-win-x64-portable\settings\Deskflow.conf')) {
    Assert-Condition (Test-Path -LiteralPath (Join-Path $PublishedDirectory $required) -PathType Leaf) "The existing Windows publish directory is incomplete: $required"
}
foreach ($entry in Get-ChildItem -LiteralPath $PublishedDirectory -Force -Recurse) {
    Assert-Condition (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Payload must not contain reparse points: $($entry.Name)"
}
$payload = @(
    foreach ($file in Get-ChildItem -LiteralPath $PublishedDirectory -Force -Recurse -File | Sort-Object FullName) {
        $relativePath = [IO.Path]::GetRelativePath($PublishedDirectory, $file.FullName)
        Assert-Condition ($file.Name -notmatch '^(?i:settings\.json|twindesk\.pem|trusted-.*)$' -and $file.Extension -notin @('.pem', '.pfx', '.p12', '.key')) "Private profile material must not enter the installer: $relativePath"
        $suffix = Get-StableSuffix ('file/' + $relativePath.Replace('\', '/'))
        [pscustomobject]@{
            relativePath = $relativePath
            source = $file.FullName
            size = $file.Length
            sha256 = Get-Sha256 $file.FullName
            fileId = 'fil_' + $suffix
            componentId = 'cmp_' + $suffix
            componentGuid = [Guid]::ParseExact($suffix, 'N').ToString('D').ToUpperInvariant()
            directoryId = Get-DirectoryId ([IO.Path]::GetDirectoryName($relativePath))
        }
    }
)
Assert-Condition ($payload.Count -gt 0) 'No published files were found.'
New-Item -ItemType Directory -Path $toolDirectory, $buildDirectory, $OutputDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath $wix -PathType Leaf)) {
    if (![string]::IsNullOrWhiteSpace($WixPath)) { throw "The specified WixPath does not exist: $wix" }
    & dotnet tool install wix --version 5.0.2 --tool-path $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw "WiX 5.0.2 tool installation failed: $LASTEXITCODE" }
}
$actualWixVersion = (& $wix --version | Out-String).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $actualWixVersion -match '^5\.0\.2(?:\+|$)') "Expected the locally installed WiX 5.0.2 tool, found: $actualWixVersion"

$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Indent = $true
$xmlSettings.Encoding = $utf8
$writer = [Xml.XmlWriter]::Create($harvestPath, $xmlSettings)
$namespace = 'http://wixtoolset.org/schemas/v4/wxs'
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', $namespace)
    $writer.WriteStartElement('Fragment', $namespace)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $payload) {
        $directory = [IO.Path]::GetDirectoryName($item.relativePath)
        while (![string]::IsNullOrEmpty($directory)) {
            $directories.Add($directory) | Out-Null
            $directory = [IO.Path]::GetDirectoryName($directory)
        }
    }
    foreach ($directory in $directories | Sort-Object) {
        $writer.WriteStartElement('DirectoryRef', $namespace)
        $writer.WriteAttributeString('Id', (Get-DirectoryId ([IO.Path]::GetDirectoryName($directory))))
        $writer.WriteStartElement('Directory', $namespace)
        $writer.WriteAttributeString('Id', (Get-DirectoryId $directory))
        $writer.WriteAttributeString('Name', [IO.Path]::GetFileName($directory))
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteStartElement('ComponentGroup', $namespace)
    $writer.WriteAttributeString('Id', 'PublishedPayload')
    $removeFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $payload) {
        $writer.WriteStartElement('Component', $namespace)
        $writer.WriteAttributeString('Id', $item.componentId)
        $writer.WriteAttributeString('Guid', $item.componentGuid)
        $writer.WriteAttributeString('Directory', $item.directoryId)
        $writer.WriteAttributeString('Bitness', 'always64')
        $writer.WriteStartElement('File', $namespace)
        $writer.WriteAttributeString('Id', $item.fileId)
        $writer.WriteAttributeString('Source', $item.source)
        $writer.WriteAttributeString('Name', [IO.Path]::GetFileName($item.relativePath))
        $writer.WriteAttributeString('KeyPath', 'no')
        $writer.WriteEndElement()
        # User-profile components need HKCU registry keypaths instead of file keypaths (ICE38).
        $writer.WriteStartElement('RegistryValue', $namespace)
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\TwinDesk\Installer\Files')
        $writer.WriteAttributeString('Name', $item.componentId)
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        if ($removeFolders.Add($item.directoryId)) {
            $writer.WriteStartElement('RemoveFolder', $namespace)
            $writer.WriteAttributeString('Id', 'remove_' + $item.directoryId)
            $writer.WriteAttributeString('Directory', $item.directoryId)
            $writer.WriteAttributeString('On', 'uninstall')
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    }
    # Also remove empty intermediate payload directories that have no direct files.
    foreach ($directory in $directories | Sort-Object) {
        $directoryId = Get-DirectoryId $directory
        if (!$removeFolders.Add($directoryId)) { continue }
        $suffix = Get-StableSuffix ('cleanup/' + $directory.Replace('\', '/'))
        $writer.WriteStartElement('Component', $namespace)
        $writer.WriteAttributeString('Id', 'cleanup_' + $suffix)
        $writer.WriteAttributeString('Guid', [Guid]::ParseExact($suffix, 'N').ToString('D'))
        $writer.WriteAttributeString('Directory', $directoryId)
        $writer.WriteAttributeString('Bitness', 'always64')
        $writer.WriteStartElement('RemoveFolder', $namespace)
        $writer.WriteAttributeString('Id', 'remove_' + $directoryId)
        $writer.WriteAttributeString('On', 'uninstall')
        $writer.WriteEndElement()
        $writer.WriteStartElement('RegistryValue', $namespace)
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\TwinDesk\Installer\Directories')
        $writer.WriteAttributeString('Name', $directoryId)
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally { $writer.Dispose() }

Write-Host "Building per-user x64 MSI from $($payload.Count) existing published files with WiX $actualWixVersion."
& $wix build -arch x64 -wx -d "ProductVersion=$Version" -d "ApplicationIcon=$iconPath" -intermediateFolder $buildDirectory -o $msiPath $sourcePath $harvestPath
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed: $LASTEXITCODE" }

$installer = $null
$database = $null
$summary = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($msiPath, 0) # msiOpenDatabaseModeReadOnly
    $properties = @{}
    foreach ($row in Get-MsiRows $database 'SELECT `Property`, `Value` FROM `Property`' @('Property', 'Value')) { $properties[$row.Property] = $row.Value }
    $fileRows = @(Get-MsiRows $database 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' @('File', 'Component', 'FileName', 'FileSize'))
    $directoryRows = @(Get-MsiRows $database 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' @('Directory', 'Parent', 'DefaultDir'))
    $shortcutRows = @(Get-MsiRows $database 'SELECT `Shortcut`, `Directory_`, `Target`, `WkDir` FROM `Shortcut`' @('Shortcut', 'Directory', 'Target', 'WorkingDirectory'))
    $registryRows = @(Get-MsiRows $database 'SELECT `Root`, `Key`, `Name` FROM `Registry`' @('Root', 'Key', 'Name'))
    $removeRows = @(Get-MsiRows $database 'SELECT `FileName`, `DirProperty` FROM `RemoveFile`' @('FileName', 'Directory'))
    $mediaRows = @(Get-MsiRows $database 'SELECT `Cabinet` FROM `Media`' @('Cabinet'))
    Assert-Condition ($fileRows.Count -eq $payload.Count) "MSI File table count differs from payload: $($fileRows.Count) / $($payload.Count)"
    Assert-Condition ($properties.ProductVersion -eq $Version) 'MSI ProductVersion is incorrect.'
    Assert-Condition ($properties.UpgradeCode -eq '{3711AF1C-2788-4B20-96D7-041CB388E2E7}') 'MSI UpgradeCode is incorrect.'
    Assert-Condition (!$properties.ContainsKey('ALLUSERS') -or [string]::IsNullOrEmpty($properties.ALLUSERS)) 'MSI must not request machine-wide installation.'
    $summary = $database.SummaryInformation(0)
    $wordCount = [int]$summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(15))
    Assert-Condition (($wordCount -band 8) -eq 8) 'MSI SummaryInformation must mark this as a package that does not require elevation.'
    $installDirectory = @($directoryRows | Where-Object Directory -eq 'INSTALLFOLDER')
    $programsDirectory = @($directoryRows | Where-Object Directory -eq 'TWINDESKPROGRAMSFOLDER')
    Assert-Condition ($installDirectory.Count -eq 1 -and $installDirectory[0].Parent -eq 'TWINDESKPROGRAMSFOLDER' -and $installDirectory[0].DefaultDir.Split('|')[-1] -eq 'TwinDesk') 'MSI install directory is incorrect.'
    Assert-Condition ($programsDirectory.Count -eq 1 -and $programsDirectory[0].Parent -eq 'LocalAppDataFolder' -and $programsDirectory[0].DefaultDir.Split('|')[-1] -eq 'Programs') 'MSI must install under LocalAppData/Programs.'
    Assert-Condition (@($registryRows | Where-Object { $_.Root -ne '1' -or !$_.Key.StartsWith('Software\TwinDesk\Installer') }).Count -eq 0) 'Unexpected registry writes outside the per-user installer marker keys.'
    Assert-Condition ($shortcutRows.Count -eq 1 -and $shortcutRows[0].Directory -eq 'ApplicationProgramsFolder' -and $shortcutRows[0].Target -eq '[INSTALLFOLDER]TwinDesk.App.exe' -and $shortcutRows[0].WorkingDirectory -eq 'INSTALLFOLDER') 'MSI Start menu shortcut is incorrect.'
    Assert-Condition (@($removeRows | Where-Object { ![string]::IsNullOrEmpty($_.FileName) }).Count -eq 0) 'MSI contains broad or additional file deletion rules.'
    Assert-Condition (@($directoryRows | Where-Object { $_.Parent -eq 'LocalAppDataFolder' -and $_.Directory -ne 'TWINDESKPROGRAMSFOLDER' }).Count -eq 0) 'MSI must not author the separate LocalAppData/TwinDesk user profile.'
    Assert-Condition ($mediaRows.Count -gt 0 -and @($mediaRows | Where-Object { !$_.Cabinet.StartsWith('#') }).Count -eq 0) 'MSI must embed all payload cabinets.'
    $filesById = @{}
    foreach ($row in $fileRows) { $filesById[$row.File] = $row }
    foreach ($item in $payload) {
        Assert-Condition ($filesById.ContainsKey($item.fileId)) "Payload file is absent from the MSI File table: $($item.relativePath)"
        $row = $filesById[$item.fileId]
        Assert-Condition ([long]$row.FileSize -eq $item.size -and $row.FileName.Split('|')[-1] -eq [IO.Path]::GetFileName($item.relativePath) -and $row.Component -eq $item.componentId) "MSI file metadata differs from its published payload: $($item.relativePath)"
    }
    $msiMetadata = [ordered]@{
        productCode = $properties.ProductCode
        upgradeCode = $properties.UpgradeCode
        productVersion = $properties.ProductVersion
        perUser = $true
        requiresElevation = $false
        installDirectory = '[LocalAppDataFolder]Programs\TwinDesk'
        fileCount = $fileRows.Count
        startMenuShortcutCount = $shortcutRows.Count
        embeddedCabinetCount = $mediaRows.Count
        userProfileIsOutsideInstaller = $true
    }
}
finally {
    Release-ComObject $summary
    Release-ComObject $database
    Release-ComObject $installer
}

# Export the actual embedded payload without msiexec, elevation, or any installation action.
$extractDirectory = Join-Path $buildDirectory 'extracted'
$decompiledPath = Join-Path $buildDirectory 'verified-package.wxs'
& $wix msi decompile $msiPath -x $extractDirectory -o $decompiledPath
if ($LASTEXITCODE -ne 0) { throw "WiX MSI payload extraction failed: $LASTEXITCODE" }
[xml]$decompiled = Get-Content -LiteralPath $decompiledPath -Raw
$exportedSources = @{}
foreach ($node in $decompiled.SelectNodes('//*[local-name()="File"]')) { $exportedSources[$node.GetAttribute('Id')] = $node.GetAttribute('Source') }
$extractedFiles = @(Get-ChildItem -LiteralPath $extractDirectory -Recurse -File)
foreach ($item in $payload) {
    Assert-Condition ($exportedSources.ContainsKey($item.fileId)) "MSI decompilation did not export the file: $($item.relativePath)"
    $source = $exportedSources[$item.fileId]
    $candidates = [Collections.Generic.List[string]]::new()
    if ([IO.Path]::IsPathRooted($source)) { $candidates.Add($source) }
    else {
        $candidates.Add((Join-Path $buildDirectory $source))
        $candidates.Add((Join-Path $extractDirectory $source))
    }
    $extracted = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
    if ($extracted.Count -eq 0) { $extracted = @($extractedFiles | Where-Object Name -eq $item.fileId | Select-Object -ExpandProperty FullName) }
    Assert-Condition ($extracted.Count -eq 1) "Could not uniquely locate the exported MSI file: $($item.relativePath)"
    $expectedHash = $item.sha256
    Assert-Condition ((Get-Sha256 $extracted[0]) -eq $expectedHash) "Embedded MSI payload SHA-256 differs from publish output: $($item.relativePath)"
    Assert-Condition ((Get-Sha256 $item.source) -eq $expectedHash) "Published output changed while building the installer: $($item.relativePath)"
}
$report = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    passed = $true
    wixVersion = $actualWixVersion
    installerPath = $msiPath
    installerSha256 = Get-Sha256 $msiPath
    installerBytes = (Get-Item -LiteralPath $msiPath).Length
    publishedDirectory = $PublishedDirectory
    verification = 'WindowsInstaller.Installer.OpenDatabase read-only table inspection and WiX msi decompile extraction; all embedded files compared by SHA-256.'
    installedOrUninstalled = $false
    metadata = $msiMetadata
    payloadFilesVerified = $payload.Count
    payload = @($payload | Select-Object relativePath, size, sha256)
}
[IO.File]::WriteAllText($verificationPath, ($report | ConvertTo-Json -Depth 8), $utf8)
Write-Host "PASS MSI: $msiPath"
Write-Host "PASS $($payload.Count) embedded files match published payload by SHA-256."
Write-Host "Verification report: $verificationPath"
Get-Item -LiteralPath $msiPath | Select-Object FullName, Length
