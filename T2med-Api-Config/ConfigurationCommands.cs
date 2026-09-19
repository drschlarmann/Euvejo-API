using System.Text.Json;
using System.Text.Json.Nodes;
using T2med_Api;

namespace T2med_Api_Config;

internal static class ConfigurationCommands
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0].Equals("--encrypt", StringComparison.OrdinalIgnoreCase))
            {
                var restrictAccess = SecureConfiguration.IsInstalledLocation(args[1]);
                SecureConfiguration.Load(args[1], migratePlaintext: true, restrictToAdministrators: restrictAccess);
                return 0;
            }

            if (args.Length == 3 && args[0].Equals("--set-cdn-root", StringComparison.OrdinalIgnoreCase))
            {
                var restrictAccess = SecureConfiguration.IsInstalledLocation(args[1]);
                var json = SecureConfiguration.Load(
                    args[1],
                    migratePlaintext: false,
                    restrictToAdministrators: restrictAccess);
                var root = JsonNode.Parse(json)?.AsObject()
                    ?? throw new InvalidDataException("Die Konfiguration ist leer.");
                var t2med = root["T2med"] as JsonObject ?? new JsonObject();
                root["T2med"] = t2med;
                t2med["CdnRoot"] = Path.GetFullPath(args[2]);
                SecureConfiguration.EncryptAndWrite(
                    args[1],
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    restrictToAdministrators: restrictAccess);
                return 0;
            }

            if (args.Length == 3 && args[0].Equals("--set-api-password", StringComparison.OrdinalIgnoreCase))
            {
                var restrictAccess = SecureConfiguration.IsInstalledLocation(args[1]);
                var json = SecureConfiguration.Load(
                    args[1],
                    migratePlaintext: false,
                    restrictToAdministrators: restrictAccess);
                var root = JsonNode.Parse(json)?.AsObject()
                    ?? throw new InvalidDataException("Die Konfiguration ist leer.");
                ApiPasswordSecurity.SetPassword(root, args[2]);
                SecureConfiguration.EncryptAndWrite(
                    args[1],
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    restrictToAdministrators: restrictAccess);
                return 0;
            }

            throw new ArgumentException("Unbekannter Kommandozeilenaufruf.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
