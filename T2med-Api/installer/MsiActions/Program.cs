using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.Security.Cryptography.X509Certificates;
using T2med_Api;

namespace T2med_Api_MsiActions;

internal static class Program
{
    private const string DefaultT2medPath = @"D:\t2med";
    private const string LegacyConfigurationPath = @"C:\Program Files\t2med-api\appsettings.json";
    private const int ApiPort = 5298;
    private const string FirewallRuleName = "Euvejo-API Port 5298";
    private const string LegacyFirewallRuleName = "T2med API Port 5298";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1)
            {
                throw new InvalidOperationException("Ungueltiger MSI-Helper-Aufruf.");
            }

            if (args[0].Equals("configure", StringComparison.OrdinalIgnoreCase))
            {
                var appSettingsPath = args.Length > 1 ? args[1].Trim('"') : "";
                var t2medPath = args.Length > 2 ? args[2].Trim('"') : DefaultT2medPath;
                var apiPassword = args.Length > 3 ? args[3] : "";

                if (string.IsNullOrWhiteSpace(t2medPath))
                {
                    t2medPath = DefaultT2medPath;
                }

                ConfigureAppSettings(appSettingsPath, t2medPath, apiPassword);
                ConfigureHttpFirewall(appSettingsPath);
                ConfigureHttps(appSettingsPath);
            }
            else if (args[0].Equals("start-service", StringComparison.OrdinalIgnoreCase))
            {
                var serviceName = args.Length > 1 ? args[1].Trim('"') : "euvejo-api";
                StartService(serviceName);
            }
            else if (args[0].Equals("configure-https", StringComparison.OrdinalIgnoreCase))
            {
                ConfigureHttps(args[1].Trim('"'));
            }
            else if (args[0].Equals("configure-network", StringComparison.OrdinalIgnoreCase))
            {
                var appSettingsPath = args[1].Trim('"');
                ConfigureHttpFirewall(appSettingsPath);
                ConfigureHttps(appSettingsPath);
            }
            else
            {
                throw new InvalidOperationException("Ungueltiger MSI-Helper-Modus: " + args[0]);
            }

            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Die MSI-Nachkonfiguration der Euvejo-API ist fehlgeschlagen:\r\n\r\n" + ex,
                "Euvejo-API Installation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void ConfigureAppSettings(string appSettingsPath, string t2medPath, string apiPassword)
    {
        if (string.IsNullOrWhiteSpace(appSettingsPath) || !File.Exists(appSettingsPath))
        {
            throw new FileNotFoundException("appsettings.json wurde nicht gefunden.", appSettingsPath);
        }

        if (!SecureConfiguration.IsEncrypted(appSettingsPath)
            && File.Exists(LegacyConfigurationPath)
            && SecureConfiguration.IsEncrypted(LegacyConfigurationPath))
        {
            File.Copy(LegacyConfigurationPath, appSettingsPath, overwrite: true);
        }

        var json = SecureConfiguration.Load(appSettingsPath, migratePlaintext: false);
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        var t2med = root["T2med"] as JsonObject;
        if (t2med is null)
        {
            t2med = new JsonObject();
            root["T2med"] = t2med;
        }

        t2med["CdnRoot"] = Path.Combine(t2medPath, "data", "cdn");

        var http = root["Http"] as JsonObject ?? new JsonObject();
        root["Http"] = http;
        var httpPort = http["Port"]?.GetValue<int?>()
            ?? ExtractHttpPort(root["Urls"]?.GetValue<string>())
            ?? ApiPort;
        http["Enabled"] = http["Enabled"]?.GetValue<bool?>() ?? false;
        http["Port"] = httpPort;
        root["Urls"] = http["Enabled"]!.GetValue<bool>() ? $"http://0.0.0.0:{httpPort}" : "";

        if (!string.IsNullOrEmpty(apiPassword))
        {
            ApiPasswordSecurity.SetPassword(root, apiPassword);
        }
        else if (!ApiPasswordSecurity.HasPassword(root))
        {
            throw new InvalidOperationException(
                "Es wurde kein Euvejo-API-Kennwort festgelegt. Das Kennwort muss mindestens 6 Zeichen lang sein.");
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        SecureConfiguration.EncryptAndWrite(appSettingsPath, root.ToJsonString(options));
    }

    private static void ConfigureHttpFirewall(string appSettingsPath)
    {
        var root = JsonNode.Parse(SecureConfiguration.Load(appSettingsPath, migratePlaintext: false))!.AsObject();
        var enabled = root["Http"]?["Enabled"]?.GetValue<bool>() ?? false;
        var port = root["Http"]?["Port"]?.GetValue<int>()
            ?? ExtractHttpPort(root["Urls"]?.GetValue<string>())
            ?? ApiPort;
        if (port < 1 || port > 65535)
            throw new InvalidOperationException("Ungueltiger HTTP-Port.");

        RunNetsh($"advfirewall firewall delete rule name=\"{LegacyFirewallRuleName}\"", ignoreFailure: true);
        RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", ignoreFailure: true);
        if (enabled)
        {
            RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=domain,private");
        }
    }

    private static int? ExtractHttpPort(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
            return null;

        foreach (var candidate in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return uri.Port;
        }
        return null;
    }

    private static void ConfigureHttps(string appSettingsPath)
    {
        var root = JsonNode.Parse(SecureConfiguration.Load(appSettingsPath, migratePlaintext: false))!.AsObject();
        var https = root["Https"];
        if (https?["Enabled"]?.GetValue<bool>() == false)
            return;
        var port = https?["Port"]?.GetValue<int>() ?? 5299;
        if (port < 1 || port > 65535)
            throw new InvalidOperationException("Ungueltiger HTTPS-Port.");
        using var certificate = HttpsCertificate.LoadOrCreate(StoreLocation.LocalMachine, https?["CertificateThumbprint"]?.GetValue<string>());
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Euvejo", "Euvejo-Api");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "https-certificate.cer"), certificate.Export(X509ContentType.Cert));
        RunNetsh("advfirewall firewall delete rule name=\"T2med API HTTPS\"", ignoreFailure: true);
        RunNetsh("advfirewall firewall delete rule name=\"Euvejo-API HTTPS\"", ignoreFailure: true);
        RunNetsh($"advfirewall firewall add rule name=\"Euvejo-API HTTPS\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=domain,private");
    }

    private static void StartService(string serviceName)
    {
        var startResult = RunProcess("sc.exe", $"start \"{serviceName}\"", ignoreFailure: true);
        for (var i = 0; i < 30; i++)
        {
            var queryResult = RunProcess("sc.exe", $"query \"{serviceName}\"", ignoreFailure: true);
            if (queryResult.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(1000);
        }

        var finalQuery = RunProcess("sc.exe", $"query \"{serviceName}\"", ignoreFailure: true);
        throw new InvalidOperationException(
            $"Der Dienst '{serviceName}' konnte nicht gestartet werden.\r\n\r\n" +
            $"sc start:\r\n{startResult.Output}\r\n{startResult.Error}\r\n\r\n" +
            $"sc query:\r\n{finalQuery.Output}\r\n{finalQuery.Error}");
    }

    private static void RunNetsh(string arguments, bool ignoreFailure = false)
    {
        var result = RunProcess("netsh.exe", arguments, ignoreFailure);
        if (result.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException($"netsh.exe fehlgeschlagen ({result.ExitCode}). {result.Error} {result.Output}".Trim());
        }
    }

    private static ProcessResult RunProcess(string fileName, string arguments, bool ignoreFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (process is null)
        {
            throw new InvalidOperationException(fileName + " konnte nicht gestartet werden.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException($"{fileName} fehlgeschlagen ({process.ExitCode}). {error} {output}".Trim());
        }

        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
