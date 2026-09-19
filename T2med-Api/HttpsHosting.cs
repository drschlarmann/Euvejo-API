using System.Security.Cryptography.X509Certificates;

namespace T2med_Api;

internal static class HttpsHosting
{
    public static HttpsCertificateProvider? Configure(WebApplicationBuilder builder)
    {
        var configuredUrls = builder.Configuration["Urls"];
        var urls = new List<string>();
        if (builder.Configuration.GetValue("Http:Enabled", false))
        {
            var port = builder.Configuration.GetValue<int?>("Http:Port")
                ?? GetConfiguredPort(configuredUrls, Uri.UriSchemeHttp)
                ?? 5298;
            ValidatePort(port, "HTTP");
            urls.Add(BuildUrl(configuredUrls, Uri.UriSchemeHttp, port));
        }

        HttpsCertificateProvider? certificateProvider = null;
        if (builder.Configuration.GetValue("Https:Enabled", true))
        {
            var port = builder.Configuration.GetValue("Https:Port", 5299);
            ValidatePort(port, "HTTPS");
            var location = builder.Configuration.GetValue("Https:CertificateStore", StoreLocation.LocalMachine);
            var publicCertificatePath = builder.Configuration["Https:PublicCertificatePath"];
            if (string.IsNullOrWhiteSpace(publicCertificatePath) && location == StoreLocation.LocalMachine)
            {
                publicCertificatePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Euvejo",
                    "Euvejo-Api",
                    "https-certificate.cer");
            }
            certificateProvider = new HttpsCertificateProvider(
                location,
                builder.Configuration["Https:CertificateThumbprint"],
                publicCertificatePath);
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
                https.ServerCertificateSelector = (_, _) => certificateProvider.GetCertificate()));
            urls.Add(BuildUrl(configuredUrls, Uri.UriSchemeHttps, port));
        }

        if (urls.Count == 0)
            throw new InvalidOperationException("HTTP und HTTPS sind deaktiviert. Mindestens ein Protokoll muss aktiviert sein.");

        builder.WebHost.UseUrls([.. urls]);
        return certificateProvider;
    }

    private static int? GetConfiguredPort(string? configuredUrls, string scheme)
    {
        return FindConfiguredUrl(configuredUrls, scheme)?.Port;
    }

    private static string BuildUrl(string? configuredUrls, string scheme, int port)
    {
        var configured = FindConfiguredUrl(configuredUrls, scheme);
        if (configured is null)
            return $"{scheme}://0.0.0.0:{port}";

        var builder = new UriBuilder(configured) { Port = port, Path = "", Query = "", Fragment = "" };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static Uri? FindConfiguredUrl(string? configuredUrls, string scheme)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls))
            return null;

        foreach (var candidate in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
                return uri;
        }
        return null;
    }

    private static void ValidatePort(int port, string protocol)
    {
        if (port < 1 || port > 65535)
            throw new InvalidOperationException($"Ungueltiger {protocol}-Port.");
    }
}
