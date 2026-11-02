param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild,
    [switch]$IncludeIpv6
)

# This check starts only a loopback server. The TLS peers below never inject native input.
$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$coreProject = Join-Path $ProjectRoot 'src\TwinDesk.Core\TwinDesk.Core.csproj'
if (-not $SkipBuild) {
    & dotnet build $coreProject --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'TwinDesk.Core build failed.' }
}
Add-Type -Path (Join-Path $ProjectRoot 'src\TwinDesk.Core\bin\Debug\net8.0\TwinDesk.Core.dll')
if (-not ('TwinDeskNativeSmokeLog' -as [type])) {
    Add-Type -TypeDefinition @'
public static class TwinDeskNativeSmokeLog
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
    public static void Add(string line) => Lines.Enqueue(line);
}
'@
}

$artifactBase = Join-Path $ProjectRoot '.artifacts\native-engine-smoke'
$testDirectory = Join-Path $artifactBase ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$null = New-Item -ItemType Directory -Path $testDirectory -Force
$reportPath = Join-Path $testDirectory 'report.json'
$report = [ordered]@{
    engineVersion = '1.27.0'
    utcTime = [DateTime]::UtcNow.ToString('O')
    operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    scope = 'Loopback native server; authenticated TLS greeting; rejection of untrusted certificate; owned process shutdown. No native client or cross-computer input test.'
    passed = $false
    cases = @()
}
$previousEnvironment = @{}
foreach ($name in @('TWINDESK_SMOKE_ROOT', 'TWINDESK_SMOKE_HOST', 'TWINDESK_SMOKE_PORT', 'TWINDESK_SMOKE_FINGERPRINT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $binary = [TwinDesk.Core.EngineDiscovery]::Find()
    if (-not $binary) { throw 'Deskflow 1.27.0 engine was not found. Set TWINDESK_ENGINE to its full path.' }
    $report.enginePath = $binary
    $hostsToTest = @('127.0.0.1')
    if ($IncludeIpv6) { $hostsToTest += '::1' }

    foreach ($loopback in $hostsToTest) {
        $label = if ($loopback -eq '::1') { 'ipv6' } else { 'ipv4' }
        $caseDirectory = Join-Path $testDirectory $label
        $serverProfile = Join-Path $caseDirectory 'server'
        $peerProfile = Join-Path $caseDirectory 'peer'
        $badProfile = Join-Path $caseDirectory 'untrusted'
        $serverIdentity = [TwinDesk.Core.IdentityStore]::new($serverProfile)
        $peerIdentity = [TwinDesk.Core.IdentityStore]::new($peerProfile)
        $badIdentity = [TwinDesk.Core.IdentityStore]::new($badProfile)
        $serverFingerprint = $serverIdentity.EnsureIdentity()
        $peerFingerprint = $peerIdentity.EnsureIdentity()
        $null = $badIdentity.EnsureIdentity()
        $serverIdentity.TrustPeer($peerFingerprint, [TwinDesk.Core.ComputerRole]::Server)
        $peerIdentity.TrustPeer($serverFingerprint, [TwinDesk.Core.ComputerRole]::Client)
        $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($loopback), 0)
        $portProbe.Start()
        $port = $portProbe.LocalEndpoint.Port
        $portProbe.Stop()
        $settings = [TwinDesk.Core.AppSettings]::new()
        $settings.LocalName = 'smoke-server'
        $settings.PeerName = 'smoke-peer'
        # Exercise the accepted [IPv6] input form; the engine normalizes it for native use.
        $settings.ListenAddress = if ($label -eq 'ipv6') { '[::1]' } else { $loopback }
        $settings.Port = $port
        $settings.EnginePath = $binary
        $engine = [TwinDesk.Core.DeskflowEngine]::new($serverProfile)
        $handler = [Delegate]::CreateDelegate([Action[string]], [TwinDeskNativeSmokeLog].GetMethod('Add'))
        $engine.add_Log($handler)
        try {
            $null = $engine.StartAsync($settings).GetAwaiter().GetResult()
            if (-not $engine.IsRunning) { throw 'Native engine did not remain running.' }
            $env:TWINDESK_SMOKE_ROOT = $caseDirectory
            $env:TWINDESK_SMOKE_HOST = $loopback
            $env:TWINDESK_SMOKE_PORT = $port.ToString()
            $env:TWINDESK_SMOKE_FINGERPRINT = $serverFingerprint.Replace(':', '').ToLowerInvariant()
            $pythonOutput = @'
import os, ssl, socket, time, json, hashlib, pathlib
root = pathlib.Path(os.environ['TWINDESK_SMOKE_ROOT'])
host = os.environ['TWINDESK_SMOKE_HOST']
port = int(os.environ['TWINDESK_SMOKE_PORT'])
expected = os.environ['TWINDESK_SMOKE_FINGERPRINT']

def connect(kind):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # The exact DER certificate is pinned below.
    context.load_cert_chain(str(root / kind / 'tls/twindesk.pem'))
    deadline = time.monotonic() + 6
    while True:
        try:
            raw = socket.create_connection((host, port), 1)
            break
        except OSError:
            if time.monotonic() > deadline:
                raise
            time.sleep(.1)
    try:
        with context.wrap_socket(raw, server_hostname='smoke-server') as connection:
            connection.settimeout(3)
            actual = hashlib.sha256(connection.getpeercert(binary_form=True)).hexdigest()
            assert actual == expected, 'Server certificate fingerprint mismatch'
            try:
                return connection.recv(64)
            except (ssl.SSLError, ConnectionResetError):
                return b''
    except ssl.SSLError:
        if kind == 'untrusted':
            return b''
        raise

accepted = connect('peer')
rejected = connect('untrusted')
result = {
    'trustedPeerReceivedGreeting': b'Barrier' in accepted,
    'untrustedPeerReceivedNoGreeting': rejected == b'',
    'serverFingerprintMatched': True,
    'greetingHex': accepted.hex()
}
print(json.dumps(result))
assert result['trustedPeerReceivedGreeting'], accepted
assert result['untrustedPeerReceivedNoGreeting'], rejected
'@ | & python -
            if ($LASTEXITCODE -ne 0) { throw "TLS smoke check failed for $label." }
            $caseReport = $pythonOutput | ConvertFrom-Json
            $null = $engine.StopAsync().GetAwaiter().GetResult()
            $caseReport | Add-Member -NotePropertyName host -NotePropertyValue $loopback
            $caseReport | Add-Member -NotePropertyName ownedProcessStopped -NotePropertyValue (-not $engine.IsRunning)
            if ($engine.IsRunning) { throw 'Owned process did not stop.' }
            $report.cases += $caseReport
        }
        finally {
            $null = $engine.StopAsync().GetAwaiter().GetResult()
            $engine.Dispose()
            $log = [TwinDeskNativeSmokeLog]::Lines.ToArray()
            [System.IO.File]::WriteAllLines((Join-Path $caseDirectory 'native.log'), $log, [System.Text.UTF8Encoding]::new($false))
            $discarded = ''
            while ([TwinDeskNativeSmokeLog]::Lines.TryDequeue([ref]$discarded)) { }
        }
    }
    $report.passed = $true
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    [System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Write-Output "Native engine smoke report: $reportPath"
}
