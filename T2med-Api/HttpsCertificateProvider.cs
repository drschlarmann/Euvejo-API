using System.Security.Cryptography.X509Certificates;

namespace T2med_Api;

internal sealed class HttpsCertificateProvider : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private readonly object syncRoot = new();
    private readonly StoreLocation storeLocation;
    private readonly string? configuredThumbprint;
    private readonly string? publicCertificatePath;
    private readonly List<X509Certificate2> certificates = [];
    private DateTimeOffset nextCheck;

    public HttpsCertificateProvider(
        StoreLocation storeLocation,
        string? configuredThumbprint,
        string? publicCertificatePath)
    {
        this.storeLocation = storeLocation;
        this.configuredThumbprint = configuredThumbprint;
        this.publicCertificatePath = publicCertificatePath;
        var certificate = HttpsCertificate.LoadOrCreate(storeLocation, configuredThumbprint);
        certificates.Add(certificate);
        ExportPublicCertificate(certificate);
        nextCheck = DateTimeOffset.UtcNow.Add(CheckInterval);
    }

    public X509Certificate2 GetCertificate()
    {
        lock (syncRoot)
        {
            var current = certificates[^1];
            if (DateTimeOffset.UtcNow < nextCheck)
                return current;

            nextCheck = DateTimeOffset.UtcNow.Add(CheckInterval);
            var candidate = HttpsCertificate.LoadOrCreate(storeLocation, configuredThumbprint);
            if (candidate.Thumbprint.Equals(current.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                return current;
            }

            certificates.Add(candidate);
            ExportPublicCertificate(candidate);
            return candidate;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
            certificates.Clear();
        }
    }

    private void ExportPublicCertificate(X509Certificate2 certificate)
    {
        if (string.IsNullOrWhiteSpace(publicCertificatePath))
            return;

        var directory = Path.GetDirectoryName(publicCertificatePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = publicCertificatePath + ".tmp";
        File.WriteAllBytes(temporaryPath, certificate.Export(X509ContentType.Cert));
        File.Move(temporaryPath, publicCertificatePath, overwrite: true);
    }
}
