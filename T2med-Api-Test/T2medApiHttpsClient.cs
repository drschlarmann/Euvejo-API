using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace T2med_Api_Test;

internal static class T2medApiHttpsClient
{
    private const string ApiPasswordHeader = "X-Euvejo-Api-Password";

    public static string CertificatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Euvejo",
        "Euvejo-Api-Test",
        "euvejo-api.cer");

    private static string LegacyCertificatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Euvejo",
        "T2med-Api-Test",
        "t2med-api.cer");

    private static string ServerCertificatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Euvejo",
        "Euvejo-Api",
        "https-certificate.cer");

    private static string LegacyServerCertificatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Euvejo",
        "T2med-Api",
        "https-certificate.cer");

    private static string? AvailableCertificatePath => File.Exists(CertificatePath)
        ? CertificatePath
        : File.Exists(LegacyCertificatePath)
            ? LegacyCertificatePath
        : File.Exists(ServerCertificatePath)
            ? ServerCertificatePath
            : File.Exists(LegacyServerCertificatePath)
                ? LegacyServerCertificatePath
            : null;

    public static HttpClient CreateHttpClient(string? certificatePath = null)
    {
        certificatePath ??= AvailableCertificatePath;
        var handler = new SocketsHttpHandler();

        if (!string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
                ValidateCertificate(certificate, validateLifetime: false);
                var pinnedPublicKey = certificate.PublicKey.ExportSubjectPublicKeyInfo();
                handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, policyErrors) =>
                    ValidatePinnedServerCertificate(pinnedPublicKey, presented, policyErrors);
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => false;
            }
        }
        else
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => false;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public static Uri CreateBaseUri(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Als API-Basisadresse ist eine vollständige HTTPS-Adresse erforderlich, zum Beispiel https://api-server:5299.");
        }

        return uri;
    }

    public static void EnsureCertificateAvailable()
    {
        if (AvailableCertificatePath is null)
        {
            throw new InvalidOperationException(
                "Für diese HTTPS-Verbindung ist noch kein Serverzertifikat hinterlegt. " +
                "Wählen Sie 'Zertifikat automatisch einrichten' oder 'CER-Datei auswählen'.");
        }
    }

    public static async Task<X509Certificate2> DiscoverServerCertificateAsync(
        Uri apiBaseUri,
        CancellationToken cancellationToken = default)
    {
        if (apiBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Die automatische Einrichtung ist nur über HTTPS möglich.");
        }

        byte[]? certificateBytes = null;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(apiBaseUri.Host, apiBaseUri.Port, cancellationToken);
        using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    certificateBytes = certificate.GetRawCertData();
                }

                return true;
            });

        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = apiBaseUri.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            cancellationToken);

        if (certificateBytes is null)
        {
            throw new AuthenticationException("Der Euvejo-API-Server hat kein HTTPS-Zertifikat bereitgestellt.");
        }

        var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        try
        {
            ValidateCertificate(certificate, validateLifetime: true);
            ValidateHostname(certificate, apiBaseUri);
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    public static Task InstallCertificateAsync(
        string sourcePath,
        Uri apiBaseUri,
        string apiPassword,
        CancellationToken cancellationToken = default)
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(sourcePath);
        return InstallCertificateAsync(certificate, apiBaseUri, apiPassword, cancellationToken);
    }

    public static async Task InstallCertificateAsync(
        X509Certificate2 certificate,
        Uri apiBaseUri,
        string apiPassword,
        CancellationToken cancellationToken = default)
    {
        ValidateCertificate(certificate, validateLifetime: true);
        ValidateHostname(certificate, apiBaseUri);

        var directory = Path.GetDirectoryName(CertificatePath)
            ?? throw new InvalidOperationException("Der Speicherort des Zertifikats konnte nicht bestimmt werden.");
        Directory.CreateDirectory(directory);
        var temporaryPath = CertificatePath + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                certificate.Export(X509ContentType.Cert),
                cancellationToken);

            using var validationClient = CreateHttpClient(temporaryPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBaseUri, "health"));
            request.Headers.TryAddWithoutValidation(ApiPasswordHeader, apiPassword);
            using var response = await validationClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException("Das API-Kennwort wurde vom Euvejo-API-Server abgelehnt.");
            }

            response.EnsureSuccessStatusCode();
            File.Move(temporaryPath, CertificatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string GetCertificateStatus()
    {
        var path = AvailableCertificatePath;
        if (path is null)
        {
            return "Kein HTTPS-Serverzertifikat hinterlegt.";
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            ValidateCertificate(certificate, validateLifetime: true);
            return $"{certificate.Subject}, gültig bis {certificate.NotAfter:dd.MM.yyyy}, Fingerabdruck {FormatThumbprint(certificate)}";
        }
        catch (Exception ex)
        {
            return "HTTPS-Zertifikat ungültig: " + ex.Message;
        }
    }

    public static string FormatThumbprint(X509Certificate2 certificate)
    {
        return string.Join(" ", Enumerable.Range(0, certificate.Thumbprint.Length / 2)
            .Select(index => certificate.Thumbprint.Substring(index * 2, 2)));
    }

    private static bool ValidatePinnedServerCertificate(
        byte[] pinnedPublicKey,
        X509Certificate? presented,
        SslPolicyErrors policyErrors)
    {
        if (presented is null
            || (policyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        {
            return false;
        }

        var serverCertificate = presented as X509Certificate2 ?? new X509Certificate2(presented);
        var disposeServerCertificate = presented is not X509Certificate2;
        try
        {
            ValidateCertificate(serverCertificate, validateLifetime: true);
            var serverPublicKey = serverCertificate.PublicKey.ExportSubjectPublicKeyInfo();
            return CryptographicOperations.FixedTimeEquals(pinnedPublicKey, serverPublicKey);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            return false;
        }
        finally
        {
            if (disposeServerCertificate)
            {
                serverCertificate.Dispose();
            }
        }
    }

    private static void ValidateHostname(X509Certificate2 certificate, Uri apiBaseUri)
    {
        if (!certificate.MatchesHostname(apiBaseUri.Host, allowWildcards: false, allowCommonName: true))
        {
            throw new AuthenticationException(
                $"Das Serverzertifikat gilt nicht für die eingetragene Adresse {apiBaseUri.Host}.");
        }
    }

    private static void ValidateCertificate(X509Certificate2 certificate, bool validateLifetime)
    {
        var now = DateTime.Now;
        if (validateLifetime && (certificate.NotBefore > now || certificate.NotAfter <= now))
        {
            throw new InvalidDataException("Das Zertifikat ist noch nicht oder nicht mehr gültig.");
        }

        if (certificate.HasPrivateKey)
        {
            throw new InvalidDataException("Bitte ausschließlich das öffentliche CER-Zertifikat auswählen.");
        }

        var isServerCertificate = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        if (!isServerCertificate)
        {
            throw new InvalidDataException("Das Zertifikat ist nicht für die Serverauthentifizierung vorgesehen.");
        }
    }
}
