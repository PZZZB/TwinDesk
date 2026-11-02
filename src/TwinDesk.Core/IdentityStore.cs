using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TwinDesk.Core;

/// <summary>Stores this computer's private identity and the one explicitly paired peer.</summary>
public sealed class IdentityStore
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _sync = new();

    public IdentityStore(string profileDir)
    {
        ProfileDirectory = Path.GetFullPath(profileDir);
    }

    public string ProfileDirectory { get; }
    public string TlsDirectory => Path.Combine(ProfileDirectory, "tls");
    public string CertificatePath => Path.Combine(TlsDirectory, "twindesk.pem");

    /// <summary>Creates an RSA identity once. An existing identity is never silently replaced.</summary>
    public string EnsureIdentity()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(TlsDirectory);
            if (!File.Exists(CertificatePath))
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=TwinDesk", rsa, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddYears(3));
                var pem = rsa.ExportRSAPrivateKeyPem() + "\n" + certificate.ExportCertificatePem() + "\n";
                WriteAtomic(CertificatePath, pem, privateFile: true, overwrite: false);
            }

            using var existing = LoadIdentity();
            if (existing.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("本机配对证书已过期，请备份配置后重新配对。");
            return DisplayFingerprint(existing.GetCertHash(HashAlgorithmName.SHA256));
        }
    }

    public string? GetLocalFingerprint()
    {
        if (!File.Exists(CertificatePath)) return null;
        using var certificate = LoadIdentity();
        return DisplayFingerprint(certificate.GetCertHash(HashAlgorithmName.SHA256));
    }

    /// <param name="localRole">The role of this computer, not the peer.</param>
    public void TrustPeer(string fingerprint, ComputerRole localRole)
    {
        var normalized = NormalizeFingerprint(fingerprint);
        Directory.CreateDirectory(TlsDirectory);
        var compact = normalized.Replace(":", "").ToLowerInvariant();
        WriteAtomic(TrustPath(localRole), $"v2:sha256:{compact}\n", privateFile: false);
    }

    public string? GetTrustedPeer(ComputerRole localRole)
    {
        var path = TrustPath(localRole);
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path, Utf8))
        {
            if (!line.StartsWith("v2:sha256:", StringComparison.OrdinalIgnoreCase)) continue;
            try { return NormalizeFingerprint(line); }
            catch (ArgumentException) { /* A corrupt entry must not become a trusted identity. */ }
        }
        return null;
    }

    /// <summary>Accepts SHA-256 as 64 hex digits, colon-separated bytes, or a Deskflow database line.</summary>
    public static string NormalizeFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var value = fingerprint.Trim();
        if (value.StartsWith("v2:sha256:", StringComparison.OrdinalIgnoreCase)) value = value[10..];
        value = value.Replace(":", "").Replace(" ", "").Replace("\t", "");
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("配对指纹必须是完整的 SHA-256 指纹（64 位十六进制）。", nameof(fingerprint));
        return DisplayFingerprint(Convert.FromHexString(value));
    }

    private string TrustPath(ComputerRole localRole) => localRole switch
    {
        ComputerRole.Server => Path.Combine(TlsDirectory, "trusted-clients"),
        ComputerRole.Client => Path.Combine(TlsDirectory, "trusted-servers"),
        _ => throw new ArgumentOutOfRangeException(nameof(localRole))
    };

    private X509Certificate2 LoadIdentity()
    {
        try
        {
            var certificate = X509Certificate2.CreateFromPemFile(CertificatePath, CertificatePath);
            using var key = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || key is null || key.KeySize < 2048)
            {
                certificate.Dispose();
                throw new CryptographicException("Identity has no usable RSA private key.");
            }
            return certificate;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("本机配对证书无法读取。为避免改变已配对身份，程序没有覆盖该文件。", exception);
        }
    }

    private static string DisplayFingerprint(byte[] bytes) => string.Join(":", bytes.Select(b => b.ToString("X2")));

    internal static void WriteAtomic(string path, string contents, bool privateFile = false, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (privateFile && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, Utf8)) writer.Write(contents);
            try { File.Move(temporary, path, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(path)) { /* Another instance created the identity first. */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
