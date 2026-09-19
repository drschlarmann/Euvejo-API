using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using T2med_Api;

var root = Path.Combine(Path.GetTempPath(), "euvejo-secure-config-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    const string secret = "test-only-secret-value";
    var plaintext = $$"""
        {
          "ConnectionStrings": {
            "T2medDatabase": "Host=localhost;Database=t2med;Username=t2med;Password={{secret}}"
          },
          "T2med": { "CdnRoot": "D:\\t2med\\data\\cdn" },
          "Urls": "http://0.0.0.0:5298"
        }
        """;

    var path = Path.Combine(root, "appsettings.json");
    await File.WriteAllTextAsync(path, plaintext);
    var migrated = SecureConfiguration.Load(path, migratePlaintext: true, restrictToAdministrators: false);
    Assert(migrated == plaintext, "Klartextmigration veraendert die Konfiguration.");
    var envelope = await File.ReadAllTextAsync(path);
    Assert(envelope.Contains(SecureConfiguration.Format, StringComparison.Ordinal), "Formatkennung fehlt.");
    Assert(!envelope.Contains(secret, StringComparison.Ordinal), "Kennwort steht weiterhin im Klartext auf dem Datentraeger.");
    Assert(SecureConfiguration.IsEncrypted(path), "Datei wird nicht als verschluesselt erkannt.");

    var decrypted = SecureConfiguration.Load(path, migratePlaintext: true, restrictToAdministrators: false);
    Assert(decrypted == plaintext, "DPAPI-Rundlauf liefert andere Nutzdaten.");
    Console.WriteLine("PASS: plaintext migration and DPAPI round-trip.");

    var tamperedPath = Path.Combine(root, "tampered.json");
    var node = JsonNode.Parse(envelope)!.AsObject();
    var bytes = Convert.FromBase64String(node["ciphertext"]!.GetValue<string>());
    bytes[^1] ^= 0x55;
    node["ciphertext"] = Convert.ToBase64String(bytes);
    await File.WriteAllTextAsync(tamperedPath, node.ToJsonString());
    AssertThrows<CryptographicException>(() => SecureConfiguration.Load(tamperedPath, migratePlaintext: false, restrictToAdministrators: false));
    Console.WriteLine("PASS: manipulated ciphertext is rejected.");

    var unknownPath = Path.Combine(root, "unknown.json");
    await File.WriteAllTextAsync(unknownPath, "{\"format\":\"euvejo-dpapi-v999\",\"ciphertext\":\"AA==\"}");
    AssertThrows<InvalidDataException>(() => SecureConfiguration.Load(unknownPath, migratePlaintext: false, restrictToAdministrators: false));
    Console.WriteLine("PASS: unknown envelope format is rejected.");

    var invalidPath = Path.Combine(root, "invalid.json");
    await File.WriteAllTextAsync(invalidPath, "not-json");
    AssertThrows<InvalidDataException>(() => SecureConfiguration.Load(invalidPath, migratePlaintext: true, restrictToAdministrators: false));
    Assert((await File.ReadAllTextAsync(invalidPath)) == "not-json", "Ungueltige Eingabe wurde ueberschrieben.");
    Console.WriteLine("PASS: invalid JSON fails without modifying the source.");

    const string apiPassword = "Test-Kennwort-2026!";
    var passwordConfiguration = new JsonObject();
    ApiPasswordSecurity.SetPassword(passwordConfiguration, apiPassword);
    var passwordJson = passwordConfiguration.ToJsonString();
    Assert(!passwordJson.Contains(apiPassword, StringComparison.Ordinal), "API-Kennwort wurde im Klartext gespeichert.");
    var verifier = ApiPasswordSecurity.LoadVerifier(key => GetJsonValue(passwordConfiguration, key));
    Assert(verifier.Verify(apiPassword), "Korrektes API-Kennwort wird abgelehnt.");
    Assert(!verifier.Verify("Falsches-Kennwort-2026!"), "Falsches API-Kennwort wird akzeptiert.");
    AssertThrows<InvalidOperationException>(() => ApiPasswordSecurity.LoadVerifier(_ => null));
    Console.WriteLine("PASS: API password is hashed and verified fail-closed.");

    const string minimumPassword = "Ab1!xy";
    var minimumPasswordConfiguration = new JsonObject();
    ApiPasswordSecurity.SetPassword(minimumPasswordConfiguration, minimumPassword);
    var minimumVerifier = ApiPasswordSecurity.LoadVerifier(key => GetJsonValue(minimumPasswordConfiguration, key));
    Assert(minimumVerifier.Verify(minimumPassword), "Ein API-Kennwort mit genau 6 Zeichen wird abgelehnt.");
    AssertThrows<ArgumentException>(() => ApiPasswordSecurity.ValidatePassword("Ab1!x"));
    AssertThrows<ArgumentException>(() => ApiPasswordSecurity.ValidatePassword(new string('a', 257)));
    AssertThrows<ArgumentException>(() => ApiPasswordSecurity.ValidatePassword("Ab1!xy\n"));
    Console.WriteLine("PASS: API password rules are exactly 6-256 characters without line breaks.");

    await AssertMiddlewareStatusAsync(verifier, null, StatusCodes.Status401Unauthorized, expectNext: false);
    await AssertMiddlewareStatusAsync(verifier, "Falsches-Kennwort-2026!", StatusCodes.Status401Unauthorized, expectNext: false);
    await AssertMiddlewareStatusAsync(verifier, apiPassword, StatusCodes.Status204NoContent, expectNext: true);
    await AssertMiddlewareStatusAsync(
        verifier,
        apiPassword,
        StatusCodes.Status204NoContent,
        expectNext: true,
        ApiPasswordSecurity.LegacyHeaderName);
    Console.WriteLine("PASS: API middleware accepts the Euvejo header and the legacy client header.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new Exception($"Erwartete Ausnahme {typeof(T).Name} wurde nicht ausgeloest.");
}

static string? GetJsonValue(JsonObject root, string path)
{
    JsonNode? current = root;
    foreach (var segment in path.Split(':'))
    {
        current = current?[segment];
    }
    return current?.ToString();
}

static async Task AssertMiddlewareStatusAsync(
    ApiPasswordSecurity.PasswordVerifier verifier,
    string? password,
    int expectedStatus,
    bool expectNext,
    string? headerName = null)
{
    var nextCalled = false;
    var middleware = new ApiPasswordMiddleware(
        context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        },
        verifier);
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    if (password is not null)
    {
        context.Request.Headers[headerName ?? ApiPasswordSecurity.HeaderName] = password;
    }

    await middleware.InvokeAsync(context);
    Assert(context.Response.StatusCode == expectedStatus, $"Unerwarteter Middleware-Status {context.Response.StatusCode}.");
    Assert(nextCalled == expectNext, "Middleware hat den Folgeschritt unerwartet aufgerufen oder blockiert.");
}
