using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Sockets;
using T2med_Api;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
store.Open(OpenFlags.ReadWrite);
var previous = store.Certificates.Select(c => c.Thumbprint).ToHashSet();
X509Certificate2? certificate = null;
HttpsCertificateProvider? certificateProvider = null;
try
{
    var httpPort = FreePort();
    var httpsPort = FreePort();
    var builder = WebApplication.CreateBuilder();
    builder.Configuration["Urls"] = $"http://127.0.0.1:{httpPort}";
    builder.Configuration["Https:Port"] = httpsPort.ToString();
    builder.Configuration["Https:CertificateStore"] = "CurrentUser";
    certificateProvider = HttpsHosting.Configure(builder)!;
    certificate = certificateProvider.GetCertificate();
    await using var app = builder.Build();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    await app.StartAsync();
    using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        await plain.GetAsync($"http://127.0.0.1:{httpPort}/health");
        throw new Exception("HTTP was reachable although Http:Enabled is absent.");
    }
    catch (HttpRequestException)
    {
        Console.WriteLine("PASS: HTTP is disabled by default, including legacy Urls configurations.");
    }

    var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
    policy.CustomTrustStore.Add(certificate);
    using var handler = new SocketsHttpHandler();
    handler.SslOptions.CertificateChainPolicy = policy;
    using var trusted = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    using var tlsResponse = await trusted.GetAsync($"https://localhost:{httpsPort}/health");
    tlsResponse.EnsureSuccessStatusCode();
    Console.WriteLine("PASS: HTTPS 200 with explicit certificate trust AND hostname verification.");

    try
    {
        await trusted.GetAsync($"https://127.0.0.2:{httpsPort}/health");
        throw new Exception("Wrong hostname was accepted!");
    }
    catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
    {
        Console.WriteLine("PASS: Wrong hostname rejected.");
    }
    if (!previous.Contains(certificate.Thumbprint))
    {
        try
        {
            await plain.GetAsync($"https://localhost:{httpsPort}/health");
            throw new Exception("Untrusted certificate was accepted!");
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            Console.WriteLine("PASS: Untrusted certificate rejected.");
        }
    }
    using var reloaded = HttpsCertificate.LoadOrCreate(StoreLocation.CurrentUser);
    if (reloaded.Thumbprint != certificate.Thumbprint || !reloaded.HasPrivateKey)
        throw new Exception("Certificate identity changed.");
    Console.WriteLine("PASS: Certificate/private key persist across starts.");
    await app.StopAsync();

    var renewalCertificateName = "Euvejo T2med API renewal test " + Guid.NewGuid().ToString("N");
    using var renewalSource = HttpsCertificate.LoadOrCreate(
        StoreLocation.CurrentUser,
        thumbprint: null,
        renewalCertificateName,
        DateTimeOffset.Now);
    using var renewed = HttpsCertificate.LoadOrCreate(
        StoreLocation.CurrentUser,
        thumbprint: null,
        renewalCertificateName,
        renewalSource.NotAfter.AddDays(-30));
    if (renewed.Thumbprint.Equals(renewalSource.Thumbprint, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Certificate was not renewed inside the renewal window.");
    if (!renewed.PublicKey.ExportSubjectPublicKeyInfo().AsSpan()
        .SequenceEqual(renewalSource.PublicKey.ExportSubjectPublicKeyInfo()))
        throw new Exception("Certificate renewal changed the pinned public key.");
    if (renewed.NotAfter <= renewalSource.NotAfter)
        throw new Exception("Renewed certificate does not extend the validity period.");
    foreach (var testCertificate in store.Certificates.Cast<X509Certificate2>()
        .Where(item => item.FriendlyName == renewalCertificateName).ToArray())
    {
        store.Remove(testCertificate);
        testCertificate.Dispose();
    }
    Console.WriteLine("PASS: Certificate renews inside 60 days and keeps the protected key identity.");

    var httpOnlyPort = FreePort();
    var httpOnlyBuilder = WebApplication.CreateBuilder();
    httpOnlyBuilder.Configuration["Urls"] = $"http://127.0.0.1:{httpOnlyPort}";
    httpOnlyBuilder.Configuration["Http:Enabled"] = "true";
    httpOnlyBuilder.Configuration["Https:Enabled"] = "false";
    if (HttpsHosting.Configure(httpOnlyBuilder) is not null)
        throw new Exception("HTTPS disabling failed.");
    await using var httpOnly = httpOnlyBuilder.Build();
    httpOnly.MapGet("/health", () => "ok");
    await httpOnly.StartAsync();
    (await plain.GetAsync($"http://127.0.0.1:{httpOnlyPort}/health")).EnsureSuccessStatusCode();
    if (httpOnly.Urls.Any(u => u.StartsWith("https:")))
        throw new Exception("HTTPS unexpectedly enabled.");
    await httpOnly.StopAsync();
    Console.WriteLine("PASS: HTTP implementation can be explicitly re-enabled through Http:Enabled.");
}
finally
{
    if (certificate is not null && !previous.Contains(certificate.Thumbprint))
        store.Remove(certificate);
    certificateProvider?.Dispose();
}
