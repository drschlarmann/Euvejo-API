using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace T2med_Api;

public static class SecureConfiguration
{
    public const string Format = "euvejo-dpapi-v1";
    private const string Protection = "Windows-DPAPI-LocalMachine";
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;
    // Compatibility identifier: changing this would make existing encrypted configurations unreadable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Euvejo.T2med-Api.Configuration.v1");

    public static string Load(string path, bool migratePlaintext, bool restrictToAdministrators = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = File.ReadAllText(path, Encoding.UTF8);
        if (TryReadEnvelope(content, out var ciphertext))
        {
            var plaintext = Unprotect(ciphertext);
            ValidateJson(plaintext);
            if (restrictToAdministrators)
            {
                RestrictFileAccess(path);
            }
            return plaintext;
        }

        ValidateJson(content);
        if (migratePlaintext)
        {
            EncryptAndWrite(path, content, restrictToAdministrators);
        }

        return content;
    }

    public static void EncryptAndWrite(string path, string plaintext, bool restrictToAdministrators = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateJson(plaintext);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Konfigurationsverzeichnis fehlt.");
        Directory.CreateDirectory(directory);

        var envelope = JsonSerializer.Serialize(new Envelope
        {
            Format = Format,
            Protection = Protection,
            Ciphertext = Convert.ToBase64String(Protect(plaintext))
        }, new JsonSerializerOptions { WriteIndented = true });

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(envelope);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (restrictToAdministrators)
            {
                RestrictFileAccess(temporaryPath);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static bool IsEncrypted(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return TryReadEnvelope(File.ReadAllText(path, Encoding.UTF8), out _);
    }

    public static bool IsInstalledLocation(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return IsBelow(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsBelow(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    }

    public static void RestrictFileAccess(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Die Konfigurationsverschluesselung wird nur unter Windows unterstuetzt.");
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateFullControlRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        security.AddAccessRule(CreateFullControlRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule CreateFullControlRule(SecurityIdentifier identity)
    {
        return new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow);
    }

    private static bool TryReadEnvelope(string content, out byte[] ciphertext)
    {
        ciphertext = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die Konfiguration enthaelt kein gueltiges JSON.", exception);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("format", out var formatProperty))
            {
                return false;
            }

            var format = formatProperty.GetString();
            if (!string.Equals(format, Format, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unbekanntes verschluesseltes Konfigurationsformat: {format}");
            }

            if (!document.RootElement.TryGetProperty("protection", out var protectionProperty)
                || !string.Equals(protectionProperty.GetString(), Protection, StringComparison.Ordinal)
                || !document.RootElement.TryGetProperty("ciphertext", out var ciphertextProperty))
            {
                throw new InvalidDataException("Die verschluesselte Konfigurationsdatei ist unvollstaendig.");
            }

            try
            {
                ciphertext = Convert.FromBase64String(ciphertextProperty.GetString() ?? "");
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Die verschluesselte Konfigurationsdatei ist beschaedigt.", exception);
            }

            if (ciphertext.Length == 0)
            {
                throw new InvalidDataException("Die verschluesselte Konfigurationsdatei enthaelt keine Nutzdaten.");
            }
        }

        return true;
    }

    private static byte[] Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return InvokeDataProtection(plaintextBytes, protect: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static string Unprotect(byte[] ciphertext)
    {
        var plaintextBytes = InvokeDataProtection(ciphertext, protect: false);
        try
        {
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static byte[] InvokeDataProtection(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows-DPAPI ist nur unter Windows verfuegbar.");
        }

        var inputBlob = AllocateBlob(input);
        var entropyBlob = AllocateBlob(Entropy);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var success = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "Euvejo-API Konfiguration",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden | CryptProtectLocalMachine,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);

            if (!success)
            {
                throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.Data);
            Marshal.FreeHGlobal(entropyBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
            {
                LocalFree(outputBlob.Data);
            }
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static DataBlob AllocateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Length = data.Length, Data = pointer };
    }

    private static void ValidateJson(string plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Die Konfiguration muss ein JSON-Objekt sein.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die Konfiguration enthaelt kein gueltiges JSON.", exception);
        }
    }

    private static bool IsBelow(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    private sealed class Envelope
    {
        [JsonPropertyName("format")]
        public string Format { get; init; } = SecureConfiguration.Format;

        [JsonPropertyName("protection")]
        public string Protection { get; init; } = SecureConfiguration.Protection;

        [JsonPropertyName("ciphertext")]
        public string Ciphertext { get; init; } = "";
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
