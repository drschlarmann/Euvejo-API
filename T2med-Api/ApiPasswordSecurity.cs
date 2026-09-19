using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace T2med_Api;

public static class ApiPasswordSecurity
{
    public const string HeaderName = "X-Euvejo-Api-Password";
    public const string LegacyHeaderName = "X-T2med-Api-Password";
    public const string Algorithm = "PBKDF2-SHA256";
    public const int Iterations = 150_000;
    public const int MinimumLength = 6;
    private const int SaltLength = 32;
    private const int HashLength = 32;

    public static void SetPassword(JsonObject configuration, string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt, Iterations);

        var security = configuration["Security"] as JsonObject ?? new JsonObject();
        configuration["Security"] = security;
        security["ApiPassword"] = new JsonObject
        {
            ["Algorithm"] = Algorithm,
            ["Iterations"] = Iterations,
            ["Salt"] = Convert.ToBase64String(salt),
            ["Hash"] = Convert.ToBase64String(hash)
        };
    }

    public static bool HasPassword(JsonObject configuration)
    {
        return configuration["Security"]?["ApiPassword"]?["Hash"] is JsonValue;
    }

    public static PasswordVerifier LoadVerifier(Func<string, string?> getValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        var algorithm = getValue("Security:ApiPassword:Algorithm");
        var iterationsText = getValue("Security:ApiPassword:Iterations");
        var saltText = getValue("Security:ApiPassword:Salt");
        var hashText = getValue("Security:ApiPassword:Hash");

        if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal)
            || !int.TryParse(iterationsText, out var iterations)
            || iterations < 100_000
            || string.IsNullOrWhiteSpace(saltText)
            || string.IsNullOrWhiteSpace(hashText))
        {
            throw new InvalidOperationException(
                "Kein gueltiges Euvejo-API-Kennwort konfiguriert. Bitte Euvejo-Api-Config ausfuehren.");
        }

        try
        {
            var salt = Convert.FromBase64String(saltText);
            var hash = Convert.FromBase64String(hashText);
            if (salt.Length < 16 || hash.Length != HashLength)
            {
                throw new FormatException();
            }
            return new PasswordVerifier(iterations, salt, hash);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Die Euvejo-API-Kennwortkonfiguration ist beschaedigt.", exception);
        }
    }

    public static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinimumLength)
        {
            throw new ArgumentException($"Das API-Kennwort muss mindestens {MinimumLength} Zeichen lang sein.");
        }
        if (password.Length > 256)
        {
            throw new ArgumentException("Das API-Kennwort darf hoechstens 256 Zeichen lang sein.");
        }
        if (password.Contains('\r') || password.Contains('\n'))
        {
            throw new ArgumentException("Das API-Kennwort darf keinen Zeilenumbruch enthalten.");
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public sealed class PasswordVerifier
    {
        private readonly int iterations;
        private readonly byte[] salt;
        private readonly byte[] expectedHash;

        internal PasswordVerifier(int iterations, byte[] salt, byte[] expectedHash)
        {
            this.iterations = iterations;
            this.salt = salt;
            this.expectedHash = expectedHash;
        }

        public bool Verify(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length > 256)
            {
                return false;
            }
            var actualHash = Derive(candidate, salt, iterations);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
    }
}
