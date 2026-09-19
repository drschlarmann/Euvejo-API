using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace T2med_Api;

internal static class HttpsCertificate
{
    private const string CertificateName = "Euvejo API HTTPS";
    private const string LegacyCertificateName = "Euvejo T2med API HTTPS";
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(60);
    private static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(365 * 2);

    public static X509Certificate2 LoadOrCreate(StoreLocation location, string? thumbprint = null)
        => LoadOrCreate(location, thumbprint, CertificateName, DateTimeOffset.Now);

    internal static X509Certificate2 LoadOrCreate(
        StoreLocation location,
        string? thumbprint,
        string certificateName,
        DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Das API-HTTPS-Zertifikat benoetigt den Windows-Zertifikatspeicher.");

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        var certificates = store.Certificates.Cast<X509Certificate2>();
        var certificate = string.IsNullOrWhiteSpace(thumbprint)
            ? certificates.Where(c =>
                    (c.FriendlyName == certificateName || c.FriendlyName == LegacyCertificateName)
                    && c.HasPrivateKey)
                .OrderByDescending(c => c.NotAfter).FirstOrDefault()
            : certificates.FirstOrDefault(c => c.Thumbprint.Equals(thumbprint.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (certificate is not null)
        {
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("Das API-HTTPS-Zertifikat ist ungueltig. Bitte im Zertifikatspeicher erneuern.");

            if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                if (certificate.NotAfter <= now.LocalDateTime || certificate.NotBefore > now.LocalDateTime)
                    throw new InvalidOperationException("Das konfigurierte API-HTTPS-Zertifikat ist ungueltig. Bitte erneuern oder den Fingerabdruck entfernen.");
                return certificate;
            }

            if (certificate.NotBefore > now.LocalDateTime || certificate.NotAfter <= now.Add(RenewalWindow).LocalDateTime)
            {
                using (certificate)
                    return RenewCertificate(store, certificate, certificateName, now);
            }
            return certificate;
        }
        if (!string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException("Das konfigurierte API-HTTPS-Zertifikat wurde nicht gefunden.");

        using var rsa = RSA.Create(3072);
        using var generated = CreateCertificate(rsa, now);

        // Persist a non-exportable private key in Windows, never in the installation or repository.
        var pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            var flags = X509KeyStorageFlags.PersistKeySet | (location == StoreLocation.LocalMachine
                ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);
            certificate = X509CertificateLoader.LoadPkcs12(pfx, null, flags);
            certificate.FriendlyName = certificateName;
            store.Add(certificate);
            return certificate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static X509Certificate2 RenewCertificate(
        X509Store store,
        X509Certificate2 certificate,
        string certificateName,
        DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Das API-HTTPS-Zertifikat benoetigt den Windows-Zertifikatspeicher.");

        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Der private Schluessel des API-HTTPS-Zertifikats ist nicht verfuegbar.");
        var renewed = CreateCertificate(rsa, now);
        try
        {
            renewed.FriendlyName = certificateName;
            store.Add(renewed);
            return renewed;
        }
        catch
        {
            renewed.Dispose();
            throw;
        }
    }

    private static X509Certificate2 CreateCertificate(RSA rsa, DateTimeOffset now)
    {
        var request = new CertificateRequest($"CN={Dns.GetHostName()}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Dns.GetHostName());
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        var host = Dns.GetHostEntry(Dns.GetHostName());
        if (!host.HostName.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
            san.AddDnsName(host.HostName);
        foreach (var address in host.AddressList.Where(a => !IPAddress.IsLoopback(a)))
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(CertificateLifetime));
    }
}
