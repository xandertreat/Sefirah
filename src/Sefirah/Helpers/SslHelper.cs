using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCoreServer;

namespace Sefirah.Helpers;

/// <summary>
/// Manages SSL/TLS certificates and provides SslContext.
/// </summary>
public static class SslHelper
{
    // NetCoreServer keeps the session's SslStream private; read it so the cert is tied to this session.
    private static readonly System.Reflection.FieldInfo? SessionStreamField = typeof(SslSession).GetField("_sslStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.FieldInfo? ClientStreamField = typeof(SslClient).GetField("_sslStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static byte[] DevicePublicKeyEncoded => _devicePublicKeyEncoded ??= GetPublicKeyEncoded(GetOrCreateCertificate());
    private static byte[]? _devicePublicKeyEncoded;

    public static string DevicePublicKeyString => _devicePublicKeyString ??= GetPublicKeyStringBase64(DevicePublicKeyEncoded);
    private static string? _devicePublicKeyString;

    private static string GetPublicKeyStringBase64(byte[] encodedPublicKey)
    {
        return Convert.ToBase64String(encodedPublicKey);
    }

    private static byte[] GetPublicKeyEncoded(X509Certificate2 c)
    {
        using var ecdsa = c.GetECDsaPublicKey();
        if (ecdsa is not null) return ecdsa.ExportSubjectPublicKeyInfo();
        return [];
    }

    public static string GetVerificationCode(string theirPublicKeyBase64)
    {
        byte[] theirEncoded = Convert.FromBase64String(theirPublicKeyBase64); 

        if (theirEncoded.Length == 0) return "00000000";

        var concat = SortedConcatUnsigned(DevicePublicKeyEncoded, theirEncoded);
        return FormatVerificationCode(SHA256.HashData(concat));
    }

    /// <summary>concat in deterministic order (unsigned byte comparison). When a &lt; b returns b+a, else a+b.</summary>
    private static byte[] SortedConcatUnsigned(byte[] a, byte[] b)
    {
        return CompareUnsigned(a, b) < 0 ? [.. b, .. a] : [.. a, .. b];
    }

    private static int CompareUnsigned(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var u = a[i] & 0xFF;
            var v = b[i] & 0xFF;
            if (u != v) return u.CompareTo(v);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string FormatVerificationCode(byte[] hash)
    {
        var hex = Convert.ToHexString(hash).AsSpan(0, Math.Min(8, hash.Length * 2));
        return hex.ToString().ToUpperInvariant();
    }

    /// <summary>Returns the raw cert this connection's peer presented in its TLS handshake, only if its public key equals <paramref name="claimedPublicKey"/>.</summary>
    public static byte[]? GetPeerCertificate(SslSession session, string? claimedPublicKey) => GetPeerCertificate(SessionStreamField?.GetValue(session), claimedPublicKey);

    public static byte[]? GetPeerCertificate(SslClient client, string? claimedPublicKey) => GetPeerCertificate(ClientStreamField?.GetValue(client), claimedPublicKey);

    private static byte[]? GetPeerCertificate(object? stream, string? claimedPublicKey)
    {
        if (string.IsNullOrEmpty(claimedPublicKey) || stream is not SslStream { RemoteCertificate: { } remote }) return null;
        using var c = new X509Certificate2(remote);
        return GetPublicKeyStringBase64(GetPublicKeyEncoded(c)) == claimedPublicKey ? c.RawData : null;
    }

    private static string CertificateFileName { get; } = "Sefirah.pfx";

    private static void RestrictToOwner(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restrict permissions: {ex.Message}");
        }
    }

    private static X509Certificate2 CreateECDSACertificate()
    {
        // Create ECDSA with NIST P-256 curve
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(true);

        var subjectName = new X500DistinguishedName("CN=SefirahCastle");
        CertificateRequest certRequest = new(subjectName, ecdsa, HashAlgorithmName.SHA256);

        // Add certificate extensions
        certRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        certRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));

        // Create self-signed certificate valid for 10 years
        X509Certificate2 certificate = certRequest.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(10));

        // Ensure the certificate is exportable
        byte[] exportedData = certificate.Export(X509ContentType.Pfx);
        certificate = X509CertificateLoader.LoadPkcs12(exportedData, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        string certPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, CertificateFileName);

        using (var fs = new FileStream(certPath, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            fs.Write(exportedData);
        RestrictToOwner(certPath);

        return certificate;
    }

    public static X509Certificate2 GetOrCreateCertificate()
    {
        string certPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, CertificateFileName);

        if (File.Exists(certPath))
        {
            try
            {
                RestrictToOwner(certPath);
                return X509CertificateLoader.LoadPkcs12FromFile(certPath, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load certificate: {ex.Message}");
            }
        }

        return CreateECDSACertificate();
    }

    public static SslContext GetSslContext()
    {
        return new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, GetOrCreateCertificate(), AcceptAnyCertificate)
        {
            ClientCertificateRequired = true
        };
    }

    /// <summary>SslContext which validates peer cert against expected cert.</summary>
    public static SslContext CreateSslContext(byte[] expectedPeerCert)
    {
        return new SslContext(SslProtocols.Tls12 | SslProtocols.Tls13, GetOrCreateCertificate(), Validate)
        {
            ClientCertificateRequired = true
        };

        bool Validate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            if (cert is null) return false;
            using var c = new X509Certificate2(cert);
            var raw = c.RawData;
            return raw.Length == expectedPeerCert.Length && raw.AsSpan().SequenceEqual(expectedPeerCert);
        }
    }

    /// <summary>Accepts any peer cert; callers authorize it afterwards against the connection's own cert.</summary>
    private static bool AcceptAnyCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors) => cert is not null;
}
