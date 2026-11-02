param(
    [switch]$SkipInstallationProbe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$packageDirectory = Join-Path $artifactRoot 'packages'
$reportPath = Join-Path $artifactRoot 'package-checks.json'
$version = '0.1.0'
$engineVersion = '1.27.0'
$engineDirectoryName = "deskflow-$engineVersion-win-x64-portable"
$engineArchivePath = Join-Path $projectRoot "engine\$engineDirectoryName.zip"
$engineArchiveSha256 = '8d19004cc3625623489d96580c1b58fd13ee78185689907fd4478595f93fab9a'
$runtimes = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
$results = [Collections.Generic.List[object]]::new()
$globalErrors = [Collections.Generic.List[string]]::new()

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-EntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-EntryText([IO.Compression.ZipArchiveEntry]$Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.Encoding]::UTF8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Invoke-InstallationProbe([string]$Executable, [string]$ExpectedEnginePath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # Running from the project makes an accidental fallback to the development engine observable.
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.Environment.Remove('TWINDESK_ENGINE') | Out-Null
    $startInfo.ArgumentList.Add('--check-installation')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        Assert-Condition ($process.Start()) 'Could not start the packaged installation probe.'
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The packaged installation probe did not exit within 15 seconds.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
        $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
        Assert-Condition ($process.ExitCode -eq 0) "Installation probe failed with exit code $($process.ExitCode). $stderr"
        $metadata = $stdout | ConvertFrom-Json -AsHashtable
        Assert-Condition ($null -ne $metadata -and $metadata.ContainsKey('enginePath')) 'Installation probe did not return enginePath in JSON stdout.'
        Assert-Condition ($metadata.platform -eq 'Windows') 'Installation probe returned an unexpected platform.'
        Assert-Condition ($metadata.supportedEngineVersion -eq $engineVersion) 'Installation probe returned an unexpected engine version.'
        Assert-Condition (![string]::IsNullOrWhiteSpace($metadata.enginePath)) 'Installation probe returned an empty engine path.'
        $actualEngine = [IO.Path]::GetFullPath($metadata.enginePath)
        $expectedEngine = [IO.Path]::GetFullPath($ExpectedEnginePath)
        Assert-Condition ($actualEngine.Equals($expectedEngine, [StringComparison]::OrdinalIgnoreCase)) "The package selected an engine outside its own bundle: $actualEngine"
        Assert-Condition (Test-Path -LiteralPath $actualEngine -PathType Leaf) 'The discovered bundled engine does not exist.'
        return [ordered]@{
            state = 'passed'
            exitCode = $process.ExitCode
            platform = $metadata.platform
            enginePath = $actualEngine
            supportedEngineVersion = $metadata.supportedEngineVersion
            standardError = $stderr
        }
    }
    finally { $process.Dispose() }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
if (!(Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    $globalErrors.Add("Package directory does not exist: $packageDirectory")
}
else {
    $allZipNames = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.zip' -File | Select-Object -ExpandProperty Name)
    $expectedZipNames = @($runtimes | ForEach-Object { "TwinDesk-$version-$_.zip" })
    if ($allZipNames.Count -ne $runtimes.Count) { $globalErrors.Add("Expected exactly five ZIP files; found $($allZipNames.Count).") }
    foreach ($name in $allZipNames) {
        if ($name -notin $expectedZipNames) { $globalErrors.Add("Unexpected package ZIP: $name") }
    }
}

foreach ($runtime in $runtimes) {
    $packageName = "TwinDesk-$version-$runtime"
    $zipPath = Join-Path $packageDirectory "$packageName.zip"
    $publishedDirectory = Join-Path $packageDirectory $packageName
    $checks = [Collections.Generic.List[string]]::new()
    $entryHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $result = [ordered]@{
        runtime = $runtime
        archive = $zipPath
        status = 'failed'
        checks = $checks
        installationProbe = [ordered]@{ state = 'not-run'; reason = 'Only the Windows package can execute on this verification host.' }
    }
    $archive = $null
    $extractionDirectory = $null
    try {
        Assert-Condition (Test-Path -LiteralPath $zipPath -PathType Leaf) "Missing package ZIP: $zipPath"
        $result.archiveSha256 = Get-FileSha256 $zipPath
        $result.archiveBytes = (Get-Item -LiteralPath $zipPath).Length
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        $entries = [Collections.Generic.Dictionary[string,IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            Assert-Condition (!$name.StartsWith('/') -and !$name.Contains(':') -and ($name.Split('/') -notcontains '..')) "Unsafe ZIP entry: $name"
            Assert-Condition ($name.StartsWith("$packageName/", [StringComparison]::Ordinal)) "ZIP entry has an unexpected top-level folder: $name"
            if ($name.EndsWith('/')) { continue }
            Assert-Condition (!$entries.ContainsKey($name)) "Duplicate ZIP entry: $name"
            $entries.Add($name, $entry)
            $leaf = $name.Split('/')[-1]
            Assert-Condition ($leaf -notmatch '^(?i:settings\.json|twindesk\.pem|trusted-.*)$') "Private settings or identity found in ZIP: $name"
        }
        $checks.Add('Archive entries are confined to the package folder and contain no private settings, certificate, or trust files.')

        $appHost = if ($runtime -eq 'win-x64') { 'TwinDesk.App.exe' } else { 'TwinDesk.App' }
        foreach ($required in @($appHost, 'TwinDesk.App.dll', 'TwinDesk.Core.dll', 'TwinDesk.App.runtimeconfig.json', 'TwinDesk.App.deps.json', 'README.md', '双机测试.md', 'THIRD-PARTY-NOTICES.md')) {
            $key = "$packageName/$required"
            Assert-Condition ($entries.ContainsKey($key) -and $entries[$key].Length -gt 0) "Missing or empty required package file: $required"
        }
        foreach ($metadataFile in @('TwinDesk.App.runtimeconfig.json', 'TwinDesk.App.deps.json')) {
            $metadata = (Read-EntryText $entries["$packageName/$metadataFile"]) | ConvertFrom-Json -AsHashtable
            Assert-Condition ($null -ne $metadata) "Invalid package JSON: $metadataFile"
        }
        $checks.Add('Apphost, application assemblies, runtime metadata, README, two-computer test guide, and notices are present.')

        Assert-Condition (Test-Path -LiteralPath $publishedDirectory -PathType Container) "Missing corresponding publish directory: $publishedDirectory"
        $dllCount = 0
        foreach ($name in $entries.Keys) {
            if (!$name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $name.Substring($packageName.Length + 1)
            $publishedFile = Join-Path $publishedDirectory $relative
            Assert-Condition (Test-Path -LiteralPath $publishedFile -PathType Leaf) "Packaged DLL is missing from the publish directory: $relative"
            $hash = Get-EntryHash $entries[$name]
            Assert-Condition ($hash -eq (Get-FileSha256 $publishedFile)) "ZIP DLL differs from the publish directory: $relative"
            $entryHashes.Add($name, $hash)
            $dllCount++
        }
        Assert-Condition ($dllCount -gt 0) 'No managed or native DLLs were found in the package.'
        $result.dllsComparedWithPublishDirectory = $dllCount
        $result.applicationAssemblySha256 = $entryHashes["$packageName/TwinDesk.App.dll"]
        $result.coreAssemblySha256 = $entryHashes["$packageName/TwinDesk.Core.dll"]
        $checks.Add("All $dllCount DLL hashes match their corresponding publish directory files.")

        if ($runtime -eq 'win-x64') {
            $enginePrefix = "$packageName/engine/$engineDirectoryName/"
            foreach ($required in @('deskflow-core.exe', 'deskflow.exe', 'Qt6Core.dll', 'Qt6Gui.dll', 'Qt6Network.dll', 'Qt6Widgets.dll', 'libcrypto-3-x64.dll', 'libssl-3-x64.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'plugins/platforms/qwindows.dll', 'settings/Deskflow.conf', 'LICENSE', 'LICENSE_EXCEPTION')) {
                Assert-Condition ($entries.ContainsKey("$enginePrefix$required")) "Missing Windows portable engine file: $required"
            }
            Assert-Condition (Test-Path -LiteralPath $engineArchivePath -PathType Leaf) 'The verified upstream engine archive is missing.'
            Assert-Condition ((Get-FileSha256 $engineArchivePath) -eq $engineArchiveSha256) 'The upstream engine archive SHA-256 is incorrect.'
            $upstream = [IO.Compression.ZipFile]::OpenRead($engineArchivePath)
            $upstreamCount = 0
            try {
                foreach ($entry in $upstream.Entries) {
                    $upstreamName = $entry.FullName.Replace('\', '/')
                    if ($upstreamName.EndsWith('/')) { continue }
                    $packageEntryName = "$packageName/engine/$upstreamName"
                    Assert-Condition ($entries.ContainsKey($packageEntryName)) "The portable engine bundle is incomplete: $upstreamName"
                    Assert-Condition ((Get-EntryHash $entry) -eq (Get-EntryHash $entries[$packageEntryName])) "The bundled engine differs from the verified upstream file: $upstreamName"
                    $upstreamCount++
                }
            }
            finally { $upstream.Dispose() }
            $result.upstreamEngineFilesCompared = $upstreamCount
            $checks.Add("All $upstreamCount upstream portable-engine files are present and match the verified official ZIP, including licenses and the portable marker.")

            if ($SkipInstallationProbe) {
                $result.installationProbe = [ordered]@{ state = 'skipped'; reason = '-SkipInstallationProbe requested archive-only verification.' }
            }
            else {
                Assert-Condition ($IsWindows) 'The Windows installation probe requires a Windows verification host; use -SkipInstallationProbe elsewhere.'
                $extractionDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot ("package-probe-" + [Guid]::NewGuid().ToString('N'))))
                [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractionDirectory)
                foreach ($name in $entryHashes.Keys) {
                    $extractedFile = Join-Path $extractionDirectory $name
                    Assert-Condition ((Get-FileSha256 $extractedFile) -eq $entryHashes[$name]) "Extracted DLL differs from its ZIP entry: $name"
                }
                $checks.Add("All $dllCount Windows DLL hashes also match a fresh extraction of the ZIP.")
                $extractedPackage = Join-Path $extractionDirectory $packageName
                $result.installationProbe = Invoke-InstallationProbe (Join-Path $extractedPackage $appHost) (Join-Path $extractedPackage "engine\$engineDirectoryName\deskflow-core.exe")
                $checks.Add('The freshly extracted Windows app returned valid installation JSON and selected its own bundled engine while running from the project directory.')
            }
        }
        $result.status = 'passed'
        Write-Host "PASS $runtime ($($checks.Count) checks)"
    }
    catch {
        $result.error = $_.Exception.Message
        Write-Host "FAIL ${runtime}: $($result.error)"
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $extractionDirectory -and (Test-Path -LiteralPath $extractionDirectory)) {
            # Only delete the unique directory this run created under artifacts after checking its absolute boundary.
            $artifactBoundary = [IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (!$extractionDirectory.StartsWith($artifactBoundary, [StringComparison]::OrdinalIgnoreCase) -or ![IO.Path]::GetFileName($extractionDirectory).StartsWith('package-probe-')) {
                throw "Refusing to remove an unexpected extraction directory: $extractionDirectory"
            }
            Remove-Item -LiteralPath $extractionDirectory -Recurse -Force
        }
        $results.Add($result)
    }
}

$failedPackages = @($results | Where-Object { $_.status -ne 'passed' }).Count
$report = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = $(if ($SkipInstallationProbe) { 'archive-only' } else { 'archives-and-windows-installation' })
    packageCount = $results.Count
    allPassed = ($failedPackages -eq 0 -and $globalErrors.Count -eq 0)
    globalErrors = $globalErrors
    packages = $results
}
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Host "Package report: $reportPath"
if (!$report.allPassed) { throw "Package verification failed: $failedPackages package(s), $($globalErrors.Count) archive-set error(s)." }
