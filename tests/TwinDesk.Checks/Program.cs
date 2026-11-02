using System.Text;
using System.Security.Cryptography.X509Certificates;
using TwinDesk.Core;

var passed = 0;
var failed = 0;
var tempRoot = Path.Combine(Path.GetTempPath(), "TwinDesk-checks-" + Guid.NewGuid().ToString("N"));
try
{
    Check("Default configuration is valid", () => Equal(0, new AppSettings().Validate().Count));
    foreach (var (position, outbound, inbound) in new[]
    {
        (ScreenPosition.Left, "left", "right"), (ScreenPosition.Right, "right", "left"),
        (ScreenPosition.Above, "up", "down"), (ScreenPosition.Below, "down", "up")
    })
        Check($"Layout {position} has reciprocal links", () =>
        {
            var config = ServerConfig.Generate(Valid() with { Position = position });
            Contains($"{outbound} = receiver", config);
            Contains($"{inbound} = sender", config);
        });
    Check("Hotkey-only mode has no edge links", () =>
    {
        var config = ServerConfig.Generate(Valid() with { EdgeSwitching = false });
        Contains("section: links" + Environment.NewLine + "end", config);
        Contains("keystroke(Control+Alt+1) = switchToScreen(sender)", config);
        Contains("keystroke(Control+Alt+2) = switchToScreen(receiver)", config);
        Contains("keystroke(Control+Alt+Escape) = switchToScreen(sender)", config);
    });
    Check("Edge-only mode retains emergency return but disables regular hotkeys", () =>
    {
        var config = ServerConfig.Generate(Valid() with { HotkeySwitching = false });
        Assert(!config.Contains("keystroke(Control+Alt+1)"), "Regular return hotkey should be absent");
        Assert(!config.Contains("keystroke(Control+Alt+2)"), "Regular peer hotkey should be absent");
        Contains("keystroke(Control+Alt+Escape) = switchToScreen(sender)", config);
        Contains("right = receiver", config);
    });
    Check("Layout does not include ignored legacy server options", () =>
    {
        var config = ServerConfig.Generate(Valid());
        Assert(!config.Contains("section: screens"));
        Assert(!config.Contains("clipboardSharing"));
        Assert(!config.Contains("switchDelay"));
        Assert(!config.Contains("heartbeat"));
    });
    foreach (var invalidName in new[] { "", "computer\nend", "pc:peer", "pc#comment", "电脑", "a b", "---", new string('a', 129) })
        Check($"Reject invalid screen name {Escape(invalidName)}", () => Assert((Valid() with { LocalName = invalidName }).Validate().Count > 0));
    Check("Reject screen names differing only in case", () => Assert((Valid() with { PeerName = "SENDER" }).Validate().Count > 0));
    Check("Reject missing switching modes", () => Assert((Valid() with { EdgeSwitching = false, HotkeySwitching = false }).Validate().Count > 0));
    foreach (var port in new[] { -1, 0, 65536 })
        Check($"Reject port {port}", () => Assert((Valid() with { Port = port }).Validate().Count > 0));
    foreach (var delay in new[] { -1, 5001 })
        Check($"Reject delay {delay}", () => Assert((Valid() with { SwitchDelayMs = delay }).Validate().Count > 0));
    Check("Reject undefined enum values", () =>
    {
        Assert((Valid() with { Role = (ComputerRole)123 }).Validate().Count > 0);
        Assert((Valid() with { Network = (NetworkMode)123 }).Validate().Count > 0);
        Assert((Valid() with { Position = (ScreenPosition)123 }).Validate().Count > 0);
    });
    Check("Receiver requires a sender address", () => Assert((Valid() with { Role = ComputerRole.Client }).Validate().Count > 0));
    foreach (var host in new[] { "192.168.1.20", "pc-1.local", "localhost", "example.org.", "2001:db8::1", "[2001:db8::1]", "::1" })
        Check($"Accept host {host}", () => Equal(0, (Valid() with { Role = ComputerRole.Client, PeerHost = host }).Validate().Count));
    foreach (var host in new[] { "host:24800", "https://example.org", "user@host", "host\n--disable-crypto", "a b", "host;calc", "-host", "a..b", "999.999.999.999", "[::1]:24800" })
        Check($"Reject host {Escape(host)}", () => Assert((Valid() with { Role = ComputerRole.Client, PeerHost = host }).Validate().Count > 0));
    Check("IPv6 endpoint brackets are added exactly once", () =>
    {
        Equal("[2001:db8::1]:24800", ServerConfig.FormatEndpoint("2001:db8::1", 24800));
        Equal("[::1]:24801", ServerConfig.FormatEndpoint("[::1]", 24801));
        Equal("pc.local:24800", ServerConfig.FormatEndpoint("pc.local", 24800));
        Throws<ArgumentException>(() => ServerConfig.FormatEndpoint("bad\narg", 24800));
        Throws<ArgumentOutOfRangeException>(() => ServerConfig.FormatEndpoint("pc", 0));
    });
    Check("Listen address requires a literal IP", () =>
    {
        Equal(0, (Valid() with { ListenAddress = "::" }).Validate().Count);
        Equal(0, (Valid() with { ListenAddress = "[::1]" }).Validate().Count);
        Assert((Valid() with { ListenAddress = "localhost" }).Validate().Count > 0);
        Assert((Valid() with { ListenAddress = "0.0.0.0:24800" }).Validate().Count > 0);
    });
    Check("Machine-name sanitization produces a valid name", () =>
    {
        Equal("a-b", AppSettings.SanitizeComputerName("a b"));
        Equal("this-computer", AppSettings.SanitizeComputerName("电脑"));
        Equal(0, (Valid() with { LocalName = AppSettings.SanitizeComputerName(new string('x', 500)) }).Validate().Count);
    });
    Check("Settings save and load round-trip", () =>
    {
        var store = new SettingsStore(Path.Combine(tempRoot, "roundtrip"));
        Equal(new AppSettings(), store.Load());
        var settings = Valid() with { Role = ComputerRole.Client, PeerHost = "[::1]", Network = NetworkMode.Internet, Position = ScreenPosition.Above, EnginePath = "C:\\Program Files\\Deskflow\\deskflow-core.exe" };
        store.Save(settings);
        Equal(settings, store.Load());
        var bytes = File.ReadAllBytes(store.SettingsPath);
        Assert(!bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "JSON must not have a UTF-8 BOM");
        store.Save(settings with { Port = 25000 });
        Equal(25000, store.Load().Port);
        Equal(1, Directory.GetFiles(store.DirectoryPath).Length);
    });
    Check("Rejected save preserves previous file", () =>
    {
        var store = new SettingsStore(Path.Combine(tempRoot, "invalid-save"));
        store.Save(Valid());
        var before = File.ReadAllBytes(store.SettingsPath);
        Throws<ArgumentException>(() => store.Save(Valid() with { LocalName = "break\nend" }));
        Assert(before.SequenceEqual(File.ReadAllBytes(store.SettingsPath)));
    });
    foreach (var malformed in new[] { "{", "null", "{\"Port\":\"not-a-number\"}", "{\"Role\":123}", "{\"Port\":0}", "{\"LocalName\":null}", "{\"PeerHost\":null}", "{\"EnginePath\":null}" })
        Check($"Corrupt settings fail explicitly: {malformed}", () =>
        {
            var store = new SettingsStore(Path.Combine(tempRoot, "corrupt-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.SettingsPath, malformed, new UTF8Encoding(false));
            Throws<InvalidDataException>(() => store.Load());
            Equal(malformed, File.ReadAllText(store.SettingsPath));
        });
    Check("Server generation refuses invalid settings", () => Throws<ArgumentException>(() => ServerConfig.Generate(Valid() with { PeerName = "peer:bad" })));

    var samplePair = new PairingCode(1, "my-pc", new string('A', 64), "192.168.1.2", 24800, "Windows");
    Check("Pairing code round-trips all public device fields", () => Equal(samplePair, PairingCode.Decode(samplePair.Encode())));
    foreach (var length in new[] { 64, 128 })
        Check($"Pairing and settings both accept {length}-character computer name", () =>
        {
            var name = new string('p', length);
            Equal(0, (Valid() with { LocalName = name }).Validate().Count);
            Equal(name, PairingCode.Decode((samplePair with { ComputerName = name }).Encode()).ComputerName);
        });
    Check("Pairing rejects null without crashing with NullReferenceException", () => Throws<FormatException>(() => PairingCode.Decode(null!)));
    foreach (var code in new[] { "", "TD1.not-base64!", "TD2.e30", "TD1." + new string('A', 4096) })
        Check($"Pairing rejects malformed data of length {code.Length}", () => Throws<FormatException>(() => PairingCode.Decode(code)));
    foreach (var invalid in new[]
    {
        samplePair with { Version = 2 }, samplePair with { ComputerName = "bad\nname" },
        samplePair with { ComputerName = new string('a', 129) }, samplePair with { Fingerprint = "AA:BB" },
        samplePair with { Fingerprint = new string('G', 64) }, samplePair with { Address = "host;calc" },
        samplePair with { Port = 0 }
    })
        Check($"Pairing validates changed payload {invalid.Version}/{invalid.ComputerName.Length}/{invalid.Fingerprint.Length}/{invalid.Port}",
            () => Throws<FormatException>(() => PairingCode.Decode(invalid.Encode())));

    Check("Identity is stable across store instances and contains a readable private key", () =>
    {
        var directory = Path.Combine(tempRoot, "identity-stable");
        var first = new IdentityStore(directory);
        var fingerprint = first.EnsureIdentity();
        var before = File.ReadAllBytes(first.CertificatePath);
        Equal(fingerprint, first.EnsureIdentity());
        Equal(fingerprint, new IdentityStore(directory).EnsureIdentity());
        Assert(before.SequenceEqual(File.ReadAllBytes(first.CertificatePath)));
        using var certificate = X509Certificate2.CreateFromPemFile(first.CertificatePath, first.CertificatePath);
        Assert(certificate.HasPrivateKey);
        Equal(fingerprint, first.GetLocalFingerprint());
        var other = new IdentityStore(Path.Combine(tempRoot, "identity-other"));
        Assert(!fingerprint.Equals(other.EnsureIdentity(), StringComparison.Ordinal));
    });
    Check("Corrupt identity is reported and never replaced", () =>
    {
        var identity = new IdentityStore(Path.Combine(tempRoot, "identity-corrupt"));
        Directory.CreateDirectory(identity.TlsDirectory);
        const string corrupt = "Existing invalid identity: do not replace";
        File.WriteAllText(identity.CertificatePath, corrupt);
        Throws<InvalidOperationException>(() => identity.EnsureIdentity());
        Equal(corrupt, File.ReadAllText(identity.CertificatePath));
    });
    Check("Trusted identities are separate by local role and use the native fingerprint format", () =>
    {
        var identity = new IdentityStore(Path.Combine(tempRoot, "identity-trust"));
        var serverFingerprint = new string('A', 64);
        var clientFingerprint = new string('B', 64);
        identity.TrustPeer(serverFingerprint, ComputerRole.Server);
        Equal(IdentityStore.NormalizeFingerprint(serverFingerprint), identity.GetTrustedPeer(ComputerRole.Server));
        Assert(identity.GetTrustedPeer(ComputerRole.Client) is null);
        Equal("v2:sha256:" + serverFingerprint.ToLowerInvariant() + "\n", File.ReadAllText(Path.Combine(identity.TlsDirectory, "trusted-clients")));
        identity.TrustPeer(clientFingerprint, ComputerRole.Client);
        Equal(IdentityStore.NormalizeFingerprint(clientFingerprint), identity.GetTrustedPeer(ComputerRole.Client));
        Equal("v2:sha256:" + clientFingerprint.ToLowerInvariant() + "\n", File.ReadAllText(Path.Combine(identity.TlsDirectory, "trusted-servers")));
        Equal(IdentityStore.NormalizeFingerprint(serverFingerprint), identity.GetTrustedPeer(ComputerRole.Server));
        var replacement = new string('C', 64);
        identity.TrustPeer(replacement, ComputerRole.Server);
        Equal(IdentityStore.NormalizeFingerprint(replacement), identity.GetTrustedPeer(ComputerRole.Server));
        Equal(1, File.ReadAllLines(Path.Combine(identity.TlsDirectory, "trusted-clients")).Length);
    });
    Check("Trust rejects incomplete and non-hex fingerprints before writing", () =>
    {
        var identity = new IdentityStore(Path.Combine(tempRoot, "identity-reject"));
        Throws<ArgumentException>(() => identity.TrustPeer("AA:BB", ComputerRole.Server));
        Throws<ArgumentException>(() => identity.TrustPeer(new string('G', 64), ComputerRole.Server));
        Assert(!Directory.Exists(identity.TlsDirectory));
    });
    Check("Main INI defines both computers and requires mutual TLS fingerprint checking", () =>
    {
        var directory = Path.Combine(tempRoot, "engine-config");
        var ini = DeskflowEngine.GenerateSettings(Valid() with { Port = 24900 }, directory).Replace("\r\n", "\n");
        Contains("[computer_sender]\nname=\"sender\"", ini);
        Contains("[computer_receiver]\nname=\"receiver\"", ini);
        Contains("[security]\ntlsEnabled=true", ini);
        Contains("checkPeerFingerprints=true", ini);
        Contains("port=24900", ini);
        Contains("externalConfig=true", ini);
        Contains("externalConfigFile=\"" + Path.Combine(directory, "layout.conf").Replace('\\', '/') + "\"", ini);
        Contains("certificate=\"" + new IdentityStore(directory).CertificatePath.Replace('\\', '/') + "\"", ini);
        Contains("enableClipboard=false", ini);
        Contains("enableHeartbeat=true", ini);
        Contains("heartbeat=5000", ini);
    });
    Check("Clipboard and switch delay are represented in the active native INI", () =>
    {
        var directory = Path.Combine(tempRoot, "engine-options");
        var ini = DeskflowEngine.GenerateSettings(Valid() with { ClipboardSharing = true, SwitchDelayMs = 600 }, directory);
        Contains("enableClipboard=true", ini);
        Contains("enableSwitchDelay=true", ini);
        Contains("switchDelay=600", ini);
        var zero = DeskflowEngine.GenerateSettings(Valid() with { SwitchDelayMs = 0 }, directory);
        Contains("enableSwitchDelay=false", zero);
        Contains("switchDelay=0", zero);
    });
    Check("Native INI passes IPv6 without brackets when the port is separate", () =>
    {
        var directory = Path.Combine(tempRoot, "engine-ipv6");
        var server = DeskflowEngine.GenerateSettings(Valid() with { ListenAddress = "[::1]" }, directory);
        Contains("interface=\"::1\"", server);
        var client = DeskflowEngine.GenerateSettings(Valid() with { Role = ComputerRole.Client, PeerHost = "[2001:db8::1]", Port = 24900 }, directory);
        Contains("remoteHost=\"2001:db8::1\"", client);
        Contains("port=24900", client);
        Contains("interface=\"\"", client);
    });
}
finally
{
    // This path is created by this check process and never comes from settings or user input.
    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
}
Console.WriteLine($"Checks: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

AppSettings Valid() => new() { LocalName = "sender", PeerName = "receiver" };
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Contains(string expected, string actual) => Assert(actual.Contains(expected, StringComparison.Ordinal), $"Missing: {expected}");
void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected exception {typeof(T).Name}");
}
string Escape(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
