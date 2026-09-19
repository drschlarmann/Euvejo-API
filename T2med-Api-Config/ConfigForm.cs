using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using T2med_Api;

namespace T2med_Api_Config;

internal sealed class ConfigForm : Form
{
    private const string ServiceName = "euvejo-api";
    private const string LegacyServiceName = "T2med-Api";
    private static readonly string DefaultConfigurationPath = ResolveDefaultConfigurationPath();
    private const string DefaultConfiguration = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning"
            }
          },
          "ConnectionStrings": {
            "T2medDatabase": "Host=localhost;Port=16569;Database=t2med;Username=t2med;Password=",
            "T2medCdnDatabase": "Host=localhost;Port=16569;Database=t2med_cdn;Username=t2med;Password=",
            "MMIDatabase": "Host=localhost;Port=16569;Database=mmi;Username=t2med;Password="
          },
          "T2med": {
            "CdnRoot": "D:\\t2med\\data\\cdn"
          },
          "Urls": "",
          "Http": {
            "Enabled": false,
            "Port": 5298
          },
          "Https": {
            "Enabled": true,
            "Port": 5299,
            "CertificateStore": "LocalMachine",
            "CertificateThumbprint": ""
          },
          "AllowedHosts": "*"
        }
        """;

    private readonly TextBox pathTextBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox cdnRootTextBox = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown httpPortInput = CreatePortInput(5298);
    private readonly CheckBox httpEnabledCheckBox = new() { Text = "HTTP aktivieren (nur Kompatibilitätsbetrieb)", AutoSize = true };
    private readonly NumericUpDown httpsPortInput = CreatePortInput(5299);
    private readonly CheckBox httpsEnabledCheckBox = new() { Text = "HTTPS aktivieren", AutoSize = true, Checked = true };
    private readonly TextBox thumbprintTextBox = new() { Dock = DockStyle.Fill, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox apiPasswordTextBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox apiPasswordConfirmationTextBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox showApiPasswordCheckBox = new() { Text = "API-Kennwort anzeigen", AutoSize = true };
    private readonly CheckBox showPasswordsCheckBox = new() { Text = "Kennwörter anzeigen", AutoSize = true };
    private readonly Label statusLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(20, 95, 55), Padding = new Padding(0, 8, 0, 0) };
    private readonly Dictionary<string, DatabaseFields> databaseFields = [];
    private JsonObject configuration = ParseConfiguration(DefaultConfiguration);

    public ConfigForm()
    {
        Text = "Euvejo-API Konfiguration";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 650);
        ClientSize = new Size(920, 720);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.White;

        Controls.Add(BuildLayout());
        showPasswordsCheckBox.CheckedChanged += (_, _) => SetPasswordVisibility(showPasswordsCheckBox.Checked);
        showApiPasswordCheckBox.CheckedChanged += (_, _) =>
        {
            apiPasswordTextBox.UseSystemPasswordChar = !showApiPasswordCheckBox.Checked;
            apiPasswordConfirmationTextBox.UseSystemPasswordChar = !showApiPasswordCheckBox.Checked;
        };
        httpEnabledCheckBox.CheckedChanged += (_, _) => httpPortInput.Enabled = httpEnabledCheckBox.Checked;
        Shown += (_, _) => LoadInitialConfiguration();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildPathRow(), 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 8) };
        tabs.TabPages.Add(BuildDatabaseTab());
        tabs.TabPages.Add(BuildServerTab());
        root.Controls.Add(tabs, 0, 1);

        var actionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        actionRow.Controls.Add(CreateButton("Speichern", (_, _) => SaveConfiguration(), primary: true, width: 132));
        actionRow.Controls.Add(CreateButton("Dienst neu starten", (_, _) => RestartService(), width: 190));
        actionRow.Controls.Add(CreateButton("Standardwerte", (_, _) => LoadDefaults(), width: 150));
        root.Controls.Add(actionRow, 0, 2);
        root.Controls.Add(statusLabel, 0, 3);
        return root;
    }

    private Control BuildPathRow()
    {
        pathTextBox.Text = DefaultConfigurationPath;
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = "Konfigurationsdatei", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) }, 0, 0);
        row.Controls.Add(pathTextBox, 1, 0);
        row.Controls.Add(CreateButton("Öffnen", (_, _) => SelectAndLoadConfiguration(), width: 104), 2, 0);
        return row;
    }

    private TabPage BuildDatabaseTab()
    {
        var tab = new TabPage("Datenbanken") { Padding = new Padding(14), BackColor = Color.White };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(BuildDatabaseSection("T2medDatabase", "T2med-Datenbank", "t2med"), 0, 0);
        layout.Controls.Add(BuildDatabaseSection("T2medCdnDatabase", "T2med-CDN-Datenbank", "t2med_cdn"), 0, 1);
        layout.Controls.Add(BuildDatabaseSection("MMIDatabase", "MMI-Datenbank", "mmi"), 0, 2);
        layout.Controls.Add(showPasswordsCheckBox, 0, 3);
        tab.Controls.Add(layout);
        return tab;
    }

    private Control BuildDatabaseSection(string key, string title, string defaultDatabase)
    {
        var fields = new DatabaseFields
        {
            Host = new TextBox { Text = "localhost", Dock = DockStyle.Fill },
            Port = CreatePortInput(16569),
            Database = new TextBox { Text = defaultDatabase, Dock = DockStyle.Fill },
            Username = new TextBox { Text = "t2med", Dock = DockStyle.Fill },
            Password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill }
        };
        databaseFields[key] = fields;

        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 10) };
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 10 };
        for (var index = 0; index < 5; index++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, index is 0 or 2 ? 24 : 17));
        }
        AddField(row, 0, "Server", fields.Host);
        AddField(row, 2, "Port", fields.Port);
        AddField(row, 4, "Datenbank", fields.Database);
        AddField(row, 6, "Benutzer", fields.Username);
        AddField(row, 8, "Kennwort", fields.Password);
        group.Controls.Add(row);
        return group;
    }

    private TabPage BuildServerTab()
    {
        var tab = new TabPage("Server und Pfade") { Padding = new Padding(18), BackColor = Color.White };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddServerField(layout, 0, "T2med-CDN-Verzeichnis", cdnRootTextBox);
        AddServerField(layout, 1, "HTTP", httpEnabledCheckBox);
        AddServerField(layout, 2, "HTTP-Port", httpPortInput);
        AddServerField(layout, 3, "HTTPS", httpsEnabledCheckBox);
        AddServerField(layout, 4, "HTTPS-Port", httpsPortInput);
        AddServerField(layout, 5, "Zertifikat-Fingerabdruck", thumbprintTextBox);
        AddServerField(layout, 6, "Neues API-Kennwort", apiPasswordTextBox);
        AddServerField(layout, 7, "Kennwort bestätigen", apiPasswordConfirmationTextBox);
        AddServerField(layout, 8, "", showApiPasswordCheckBox);
        var hint = new Label
        {
            Text = "Kennwortfelder leer lassen, um ein vorhandenes Kennwort beizubehalten. Mindestens 6 Zeichen.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0)
        };
        layout.Controls.Add(hint, 1, 9);
        tab.Controls.Add(layout);
        return tab;
    }

    private void LoadInitialConfiguration()
    {
        if (File.Exists(pathTextBox.Text))
        {
            LoadConfiguration(pathTextBox.Text);
        }
        else
        {
            LoadDefaults();
            SetStatus("Standardwerte geladen. Die Konfigurationsdatei ist noch nicht vorhanden.", isError: false);
        }
    }

    private void SelectAndLoadConfiguration()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "T2med-Konfiguration (appsettings.json)|appsettings.json|JSON-Dateien (*.json)|*.json|Alle Dateien (*.*)|*.*",
            FileName = Path.GetFileName(pathTextBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(pathTextBox.Text))
                ? Path.GetDirectoryName(pathTextBox.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            pathTextBox.Text = dialog.FileName;
            LoadConfiguration(dialog.FileName);
        }
    }

    private void LoadConfiguration(string path)
    {
        try
        {
            var json = SecureConfiguration.Load(path, migratePlaintext: true);
            configuration = ParseConfiguration(json);
            PopulateFields();
            SetStatus("Konfiguration geladen. Die Datei ist mit Windows-DPAPI verschlüsselt.", isError: false);
        }
        catch (Exception exception)
        {
            ShowError("Die Konfiguration konnte nicht geladen werden.", exception);
        }
    }

    private void LoadDefaults()
    {
        configuration = ParseConfiguration(DefaultConfiguration);
        PopulateFields();
        SetStatus("Standardkonfiguration geladen.", isError: false);
    }

    private void PopulateFields()
    {
        foreach (var (key, fields) in databaseFields)
        {
            var connectionString = configuration["ConnectionStrings"]?[key]?.GetValue<string>() ?? "";
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            fields.Host.Text = builder.Host;
            fields.Port.Value = ClampPort(builder.Port == 0 ? 16569 : builder.Port);
            fields.Database.Text = builder.Database;
            fields.Username.Text = builder.Username;
            fields.Password.Text = builder.Password ?? "";
        }

        cdnRootTextBox.Text = configuration["T2med"]?["CdnRoot"]?.GetValue<string>() ?? @"D:\t2med\data\cdn";
        httpEnabledCheckBox.Checked = configuration["Http"]?["Enabled"]?.GetValue<bool>() ?? false;
        httpPortInput.Value = ClampPort(configuration["Http"]?["Port"]?.GetValue<int>()
            ?? ExtractHttpPort(configuration["Urls"]?.GetValue<string>())
            ?? 5298);
        httpPortInput.Enabled = httpEnabledCheckBox.Checked;
        httpsEnabledCheckBox.Checked = configuration["Https"]?["Enabled"]?.GetValue<bool>() ?? true;
        httpsPortInput.Value = ClampPort(configuration["Https"]?["Port"]?.GetValue<int>() ?? 5299);
        thumbprintTextBox.Text = configuration["Https"]?["CertificateThumbprint"]?.GetValue<string>() ?? "";
        apiPasswordTextBox.Clear();
        apiPasswordConfirmationTextBox.Clear();
    }

    private void SaveConfiguration()
    {
        try
        {
            var path = Path.GetFullPath(pathTextBox.Text.Trim());
            ApplyFields();
            var json = configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            SecureConfiguration.EncryptAndWrite(path, json);
            UpdateHttpFirewall();
            pathTextBox.Text = path;
            SetStatus("Konfiguration verschlüsselt gespeichert.", isError: false);

            if (MessageBox.Show(
                this,
                "Die Konfiguration wurde gespeichert. Soll der Dienst Euvejo-Api jetzt neu gestartet werden?",
                "Euvejo-API Konfiguration",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes)
            {
                RestartService();
            }
        }
        catch (Exception exception)
        {
            ShowError("Die Konfiguration konnte nicht gespeichert werden.", exception);
        }
    }

    private void ApplyFields()
    {
        if (!httpEnabledCheckBox.Checked && !httpsEnabledCheckBox.Checked)
        {
            throw new InvalidOperationException("Mindestens eines der Protokolle HTTP oder HTTPS muss aktiviert bleiben.");
        }

        var connections = configuration["ConnectionStrings"] as JsonObject ?? new JsonObject();
        configuration["ConnectionStrings"] = connections;
        foreach (var (key, fields) in databaseFields)
        {
            if (string.IsNullOrWhiteSpace(fields.Host.Text)
                || string.IsNullOrWhiteSpace(fields.Database.Text)
                || string.IsNullOrWhiteSpace(fields.Username.Text))
            {
                throw new InvalidOperationException("Server, Datenbank und Benutzer dürfen nicht leer sein.");
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = fields.Host.Text.Trim(),
                Port = decimal.ToInt32(fields.Port.Value),
                Database = fields.Database.Text.Trim(),
                Username = fields.Username.Text.Trim(),
                Password = fields.Password.Text
            };
            connections[key] = builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(cdnRootTextBox.Text))
        {
            throw new InvalidOperationException("Das T2med-CDN-Verzeichnis darf nicht leer sein.");
        }

        var t2med = configuration["T2med"] as JsonObject ?? new JsonObject();
        configuration["T2med"] = t2med;
        t2med["CdnRoot"] = Path.GetFullPath(cdnRootTextBox.Text.Trim());
        var httpPort = decimal.ToInt32(httpPortInput.Value);
        var http = configuration["Http"] as JsonObject ?? new JsonObject();
        configuration["Http"] = http;
        http["Enabled"] = httpEnabledCheckBox.Checked;
        http["Port"] = httpPort;
        configuration["Urls"] = httpEnabledCheckBox.Checked ? $"http://0.0.0.0:{httpPort}" : "";

        var https = configuration["Https"] as JsonObject ?? new JsonObject();
        configuration["Https"] = https;
        https["Enabled"] = httpsEnabledCheckBox.Checked;
        https["Port"] = decimal.ToInt32(httpsPortInput.Value);
        https["CertificateStore"] = "LocalMachine";
        https["CertificateThumbprint"] = thumbprintTextBox.Text.Replace(" ", "", StringComparison.Ordinal).Trim();

        if (apiPasswordTextBox.Text.Length > 0 || apiPasswordConfirmationTextBox.Text.Length > 0)
        {
            if (!string.Equals(apiPasswordTextBox.Text, apiPasswordConfirmationTextBox.Text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Die eingegebenen API-Kennwörter stimmen nicht überein.");
            }
            ApiPasswordSecurity.SetPassword(configuration, apiPasswordTextBox.Text);
            apiPasswordTextBox.Clear();
            apiPasswordConfirmationTextBox.Clear();
        }
        else if (!ApiPasswordSecurity.HasPassword(configuration))
        {
            throw new InvalidOperationException("Bitte ein API-Kennwort mit mindestens 6 Zeichen festlegen.");
        }
    }

    private void RestartService()
    {
        try
        {
            RunServiceCommand("stop", allowFailure: true);
            var started = RunServiceCommand("start", allowFailure: false);
            SetStatus(started.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                ? "Dienst Euvejo-Api wurde neu gestartet."
                : "Startbefehl für den Dienst Euvejo-Api wurde ausgeführt.", isError: false);
        }
        catch (Exception exception)
        {
            ShowError("Der Dienst Euvejo-Api konnte nicht neu gestartet werden.", exception);
        }
    }

    private void UpdateHttpFirewall()
    {
        RunProcess("netsh.exe", "advfirewall firewall delete rule name=\"T2med API Port 5298\"", allowFailure: true);
        RunProcess("netsh.exe", "advfirewall firewall delete rule name=\"Euvejo-API Port 5298\"", allowFailure: true);
        if (httpEnabledCheckBox.Checked)
        {
            var port = decimal.ToInt32(httpPortInput.Value);
            RunProcess(
                "netsh.exe",
                $"advfirewall firewall add rule name=\"Euvejo-API Port 5298\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=domain,private",
                allowFailure: false);
        }
    }

    private static string RunServiceCommand(string command, bool allowFailure)
    {
        var serviceName = ResolveInstalledServiceName();
        var output = RunProcess("sc.exe", $"{command} {serviceName}", allowFailure);
        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Thread.Sleep(1500);
        }
        return output;
    }

    private static string ResolveInstalledServiceName()
    {
        var current = RunProcess("sc.exe", $"query {ServiceName}", allowFailure: true);
        return current.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
            ? ServiceName
            : LegacyServiceName;
    }

    private static string ResolveDefaultConfigurationPath()
    {
        var current = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Euvejo-API", "appsettings.json");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "t2med-api", "appsettings.json");
        return File.Exists(current) || !File.Exists(legacy) ? current : legacy;
    }

    private static string RunProcess(string fileName, string arguments, bool allowFailure)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"{fileName} konnte nicht gestartet werden.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"{fileName} meldet Fehler {process.ExitCode}: {error} {output}".Trim());
        }
        return output;
    }

    private void SetPasswordVisibility(bool visible)
    {
        foreach (var fields in databaseFields.Values)
        {
            fields.Password.UseSystemPasswordChar = !visible;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        statusLabel.Text = text;
        statusLabel.ForeColor = isError ? Color.Firebrick : Color.FromArgb(20, 95, 55);
    }

    private void ShowError(string message, Exception exception)
    {
        SetStatus(message, isError: true);
        MessageBox.Show(this, $"{message}\r\n\r\n{exception.Message}", "Euvejo-API Konfiguration", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static JsonObject ParseConfiguration(string json)
    {
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("Die Konfiguration ist leer.");
    }

    private static NumericUpDown CreatePortInput(int value)
    {
        return new NumericUpDown { Minimum = 1, Maximum = 65535, Value = value, Width = 100, ThousandsSeparator = false };
    }

    private static decimal ClampPort(int value) => Math.Clamp(value, 1, 65535);

    private static int? ExtractHttpPort(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return null;
        }
        foreach (var candidate in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return uri.Port;
            }
        }
        return null;
    }

    private static Button CreateButton(string text, EventHandler handler, bool primary = false, int width = 120)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Width = width,
            Height = 40,
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(32, 100, 210) : Color.White,
            ForeColor = primary ? Color.White : Color.Black
        };
        button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(160, 170, 185);
        button.Click += handler;
        return button;
    }

    private static void AddField(TableLayoutPanel row, int column, string label, Control control)
    {
        row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(column == 0 ? 0 : 10, 7, 6, 0) }, column, 0);
        control.Margin = new Padding(0, 3, 0, 3);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        row.Controls.Add(control, column + 1, 0);
    }

    private static void AddServerField(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 12, 10) }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 6, 0, 6);
        layout.Controls.Add(control, 1, row);
    }

    private sealed class DatabaseFields
    {
        public required TextBox Host { get; init; }
        public required NumericUpDown Port { get; init; }
        public required TextBox Database { get; init; }
        public required TextBox Username { get; init; }
        public required TextBox Password { get; init; }
    }
}
