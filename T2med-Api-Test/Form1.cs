using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace T2med_Api_Test;

public partial class Form1 : Form
{
    private HttpClient httpClient = T2medApiHttpsClient.CreateHttpClient();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private TextBox baseUrlTextBox = null!;
    private TextBox apiPasswordTextBox = null!;
    private NumericUpDown patientNumberInput = null!;
    private TextBox patientSearchTextBox = null!;
    private TextBox chartEntryIdTextBox = null!;
    private TextBox pznTextBox = null!;
    private DateTimePicker fromDatePicker = null!;
    private DateTimePicker toDatePicker = null!;
    private ComboBox prescriptionTypeComboBox = null!;
    private TextBox medicationTextBox = null!;
    private TextBox activeIngredientTextBox = null!;
    private TextBox singlePriceTextBox = null!;
    private TextBox uploadPdfPathTextBox = null!;
    private TextBox uploadTitleTextBox = null!;
    private ComboBox endpointComboBox = null!;
    private Button executeButton = null!;
    private Button downloadDocumentButton = null!;
    private Button downloadImageButton = null!;
    private Button browseUploadPdfButton = null!;
    private Button autoConfigureCertificateButton = null!;
    private Button selectCertificateButton = null!;
    private Label certificateStatusLabel = null!;
    private TextBox responseTextBox = null!;
    private TextBox logTextBox = null!;
    private StatusStrip statusStrip = null!;
    private ToolStripStatusLabel statusLabel = null!;

    private readonly List<ApiEndpoint> endpoints =
    [
        new("Patient", "GET /api/patients/{nummer}", "Liest die Stammdaten eines Patienten.", EndpointKind.Json, c => $"/api/patients/{c.PatientNumber}"),
        new("Patientensuche", "GET /api/patients/search?name={name}", "Sucht Patienten nach Namen oder Namensbestandteilen.", EndpointKind.Json, c => $"/api/patients/search?name={Uri.EscapeDataString(RequireText(c.PatientSearch, "Patientensuche"))}"),
        new("Diagnosen aktueller Fall", "GET /api/patients/{nummer}/current-case/diagnoses", "Liest Diagnosen des aktuellen Behandlungsfalls.", EndpointKind.Json, c => $"/api/patients/{c.PatientNumber}/current-case/diagnoses"),
        new("Medikationsplan aktueller Fall", "GET /api/patients/{nummer}/current-case/medication-plan", "Liest den aktuellen Medikationsplan.", EndpointKind.Json, c => $"/api/patients/{c.PatientNumber}/current-case/medication-plan"),
        new("Karteieinträge aktueller Fall", "GET /api/patients/{nummer}/current-case/chart-entries", "Liest Karteieinträge des aktuellen Behandlungsfalls.", EndpointKind.Json, c => $"/api/patients/{c.PatientNumber}/current-case/chart-entries"),
        new("Dokument zu Karteieintrag", "GET /api/patients/{nummer}/chart-entries/{id}/document", "Lädt ein PDF-Dokument zu einem Dokument-Karteieintrag.", EndpointKind.File, c => $"/api/patients/{c.PatientNumber}/chart-entries/{Uri.EscapeDataString(c.ChartEntryId)}/document"),
        new("Bild zu Karteieintrag", "GET /api/patients/{nummer}/chart-entries/{id}/image", "Lädt ein Bild zu einem Bild-Karteieintrag.", EndpointKind.File, c => $"/api/patients/{c.PatientNumber}/chart-entries/{Uri.EscapeDataString(c.ChartEntryId)}/image"),
        new("Medikamentenpreis", "GET /api/medications/{pzn}/price", "Sucht den Preis zu einer PZN.", EndpointKind.Json, c => $"/api/medications/{Uri.EscapeDataString(RequireText(c.Pzn, "PZN"))}/price"),
        new("Verordnungsstatistik", "GET /api/prescription-statistics", "Ermittelt Mengen und Kosten von Verordnungen im Zeitraum.", EndpointKind.Json, c => BuildPrescriptionStatisticsPath(c)),
        new("Patienten einer Verordnung", "GET /api/prescription-statistics/patients", "Listet Patienten zu einem Medikament aus der Verordnungsstatistik.", EndpointKind.Json, c => BuildPrescriptionStatisticsPatientsPath(c)),
        new("Tageslabor", "GET /api/day-lab", "Liest LAB-Karteieinträge aller Patienten im Zeitraum.", EndpointKind.Json, c => $"/api/day-lab?from={c.From:yyyy-MM-dd}&to={c.To:yyyy-MM-dd}"),
        new("DOK-Dokument anlegen", "POST /api/patients/{nummer}/documents", "Schreibt eine PDF-Datei als DOK-Eintrag in die Akte.", EndpointKind.UploadDocument, c => $"/api/patients/{c.PatientNumber}/documents")
    ];

    public Form1()
    {
        InitializeComponent();
        BuildUi();
        endpointComboBox.DataSource = endpoints;
        endpointComboBox.DisplayMember = nameof(ApiEndpoint.DisplayName);
        endpointComboBox.SelectedIndex = 0;
        FormClosed += (_, _) => httpClient.Dispose();
    }

    private void BuildUi()
    {
        Text = "Euvejo-API Test";
        ClientSize = new Size(1250, 960);
        MinimumSize = new Size(1250, 900);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var leftScrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            RowCount = 23,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 8, 12)
        };

        for (var i = 0; i < left.RowCount; i++)
        {
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        left.Controls.Add(CreateLabel("HTTPS-API-Basisadresse"), 0, 0);
        baseUrlTextBox = new TextBox { Text = "https://localhost:5299", Dock = DockStyle.Top };
        left.Controls.Add(baseUrlTextBox, 0, 1);

        left.Controls.Add(CreateLabel("API-Kennwort", topMargin: 12), 0, 2);
        apiPasswordTextBox = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true };
        left.Controls.Add(apiPasswordTextBox, 0, 3);

        left.Controls.Add(CreateCertificatePanel(), 0, 4);

        left.Controls.Add(CreateLabel("Patientennummer", topMargin: 12), 0, 5);
        patientNumberInput = new NumericUpDown
        {
            Dock = DockStyle.Top,
            Minimum = 1,
            Maximum = 999999999,
            Value = 1,
            ThousandsSeparator = false
        };
        left.Controls.Add(patientNumberInput, 0, 6);

        left.Controls.Add(CreateLabel("Patientensuche (Name)", topMargin: 12), 0, 7);
        patientSearchTextBox = new TextBox { Dock = DockStyle.Top };
        left.Controls.Add(patientSearchTextBox, 0, 8);

        left.Controls.Add(CreateLabel("Karteieintrag ObjectId", topMargin: 12), 0, 9);
        chartEntryIdTextBox = new TextBox { Dock = DockStyle.Top };
        left.Controls.Add(chartEntryIdTextBox, 0, 10);

        left.Controls.Add(CreateLabel("PZN", topMargin: 12), 0, 11);
        pznTextBox = new TextBox { Dock = DockStyle.Top };
        left.Controls.Add(pznTextBox, 0, 12);

        left.Controls.Add(CreateDateRangePanel(), 0, 13);
        left.Controls.Add(CreatePrescriptionPanel(), 0, 14);
        left.Controls.Add(CreateUploadPanel(), 0, 15);

        left.Controls.Add(CreateLabel("Endpunkt", topMargin: 12), 0, 16);
        endpointComboBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        endpointComboBox.SelectedIndexChanged += (_, _) => UpdateEndpointDescription();
        left.Controls.Add(endpointComboBox, 0, 17);

        executeButton = new Button { Text = "Ausgewählten Endpunkt ausführen", Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 12, 0, 0) };
        executeButton.Click += async (_, _) => await ExecuteSelectedEndpointAsync();
        left.Controls.Add(executeButton, 0, 18);

        downloadDocumentButton = new Button { Text = "Dokument herunterladen", Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 8, 0, 0) };
        downloadDocumentButton.Click += async (_, _) => await DownloadFileEndpointAsync(endpoints[5]);
        left.Controls.Add(downloadDocumentButton, 0, 19);

        downloadImageButton = new Button { Text = "Bild herunterladen", Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 8, 0, 0) };
        downloadImageButton.Click += async (_, _) => await DownloadFileEndpointAsync(endpoints[6]);
        left.Controls.Add(downloadImageButton, 0, 20);

        var right = CreateResultPanel();

        statusLabel = new ToolStripStatusLabel("Bereit");
        statusStrip = new StatusStrip();
        statusStrip.Items.Add(statusLabel);

        leftScrollPanel.Controls.Add(left);
        root.Controls.Add(leftScrollPanel, 0, 0);
        root.Controls.Add(right, 1, 0);
        Controls.Add(root);
        Controls.Add(statusStrip);
    }

    private static Label CreateLabel(string text, int topMargin = 0)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, topMargin, 0, 0)
        };
    }

    private Control CreateCertificatePanel()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));

        var heading = CreateLabel("HTTPS-Serverzertifikat");
        heading.Font = new Font(heading.Font, FontStyle.Bold);
        panel.Controls.Add(heading, 0, 0);
        panel.SetColumnSpan(heading, 2);

        certificateStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Margin = new Padding(0, 4, 0, 6),
            Text = T2medApiHttpsClient.GetCertificateStatus()
        };
        panel.Controls.Add(certificateStatusLabel, 0, 1);
        panel.SetColumnSpan(certificateStatusLabel, 2);

        autoConfigureCertificateButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0),
            MinimumSize = new Size(0, 38),
            Padding = new Padding(6),
            Text = "Zertifikat automatisch einrichten"
        };
        autoConfigureCertificateButton.Click += async (_, _) => await ConfigureCertificateAutomaticallyAsync();
        panel.Controls.Add(autoConfigureCertificateButton, 0, 2);

        selectCertificateButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            MinimumSize = new Size(0, 38),
            Padding = new Padding(6),
            Text = "CER-Datei auswählen..."
        };
        selectCertificateButton.Click += async (_, _) => await SelectCertificateAsync();
        panel.Controls.Add(selectCertificateButton, 1, 2);
        return panel;
    }

    private Control CreateDateRangePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        fromDatePicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Dock = DockStyle.Fill, Value = DateTime.Today.AddMonths(-1) };
        toDatePicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Dock = DockStyle.Fill, Value = DateTime.Today };

        panel.Controls.Add(new Label { Text = "Von", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        panel.Controls.Add(fromDatePicker, 1, 0);
        panel.Controls.Add(new Label { Text = "Bis", AutoSize = true, Margin = new Padding(8, 6, 6, 0) }, 2, 0);
        panel.Controls.Add(toDatePicker, 3, 0);
        return panel;
    }

    private Control CreatePrescriptionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 8,
            Margin = new Padding(0, 12, 0, 0)
        };

        prescriptionTypeComboBox = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        prescriptionTypeComboBox.Items.AddRange(["kasse", "privat"]);
        prescriptionTypeComboBox.SelectedIndex = 0;
        medicationTextBox = new TextBox { Dock = DockStyle.Top };
        activeIngredientTextBox = new TextBox { Dock = DockStyle.Top };
        singlePriceTextBox = new TextBox { Dock = DockStyle.Top };

        panel.Controls.Add(CreateLabel("Rezepttyp"), 0, 0);
        panel.Controls.Add(prescriptionTypeComboBox, 0, 1);
        panel.Controls.Add(CreateLabel("Medikament (für Patientenliste)", topMargin: 6), 0, 2);
        panel.Controls.Add(medicationTextBox, 0, 3);
        panel.Controls.Add(CreateLabel("Wirkstoff (optional)", topMargin: 6), 0, 4);
        panel.Controls.Add(activeIngredientTextBox, 0, 5);
        panel.Controls.Add(CreateLabel("Einzelpreis (optional, z. B. 10,50)", topMargin: 6), 0, 6);
        panel.Controls.Add(singlePriceTextBox, 0, 7);
        return panel;
    }

    private Control CreateUploadPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        uploadPdfPathTextBox = new TextBox { Dock = DockStyle.Top };
        uploadTitleTextBox = new TextBox { Dock = DockStyle.Top, Text = "API-Test (KI)" };
        browseUploadPdfButton = new Button { Text = "...", Width = 34 };
        browseUploadPdfButton.Click += (_, _) => BrowseUploadPdf();

        panel.Controls.Add(CreateLabel("PDF für DOK-Upload"), 0, 0);
        panel.SetColumnSpan(panel.Controls[^1], 2);
        panel.Controls.Add(uploadPdfPathTextBox, 0, 1);
        panel.Controls.Add(browseUploadPdfButton, 1, 1);
        panel.Controls.Add(CreateLabel("Titel DOK-Eintrag", topMargin: 6), 0, 2);
        panel.SetColumnSpan(panel.Controls[^1], 2);
        panel.Controls.Add(uploadTitleTextBox, 0, 3);
        panel.SetColumnSpan(uploadTitleTextBox, 2);
        return panel;
    }

    private Control CreateResultPanel()
    {
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 30));

        responseTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9)
        };
        right.Controls.Add(responseTextBox, 0, 0);

        logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9)
        };
        right.Controls.Add(logTextBox, 0, 2);
        return right;
    }

    private async Task ConfigureCertificateAutomaticallyAsync()
    {
        Uri apiBaseUri;
        try
        {
            apiBaseUri = GetCertificateSetupBaseUri();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "HTTPS-Zertifikat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        certificateStatusLabel.Text = "Serverzertifikat wird abgerufen...";
        try
        {
            using var discoveryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var certificate = await T2medApiHttpsClient.DiscoverServerCertificateAsync(
                apiBaseUri,
                discoveryCancellation.Token);

            var confirmation = MessageBox.Show(
                this,
                $"Der Euvejo-API-Server hat folgendes Zertifikat gesendet:\n\n" +
                $"Server: {apiBaseUri.Host}:{apiBaseUri.Port}\n" +
                $"Ausgestellt für: {certificate.GetNameInfo(X509NameType.SimpleName, false)}\n" +
                $"Gültig bis: {certificate.NotAfter:dd.MM.yyyy}\n" +
                $"Fingerabdruck:\n{T2medApiHttpsClient.FormatThumbprint(certificate)}\n\n" +
                "Vertrauen Sie diesem Zertifikat?",
                "Euvejo-API-Zertifikat bestätigen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                certificateStatusLabel.Text = "Einrichtung abgebrochen. Es wurde nichts geändert.";
                return;
            }

            certificateStatusLabel.Text = "Zertifikat, Kennwort und HTTPS-Verbindung werden geprüft...";
            using var validationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await T2medApiHttpsClient.InstallCertificateAsync(
                certificate,
                apiBaseUri,
                apiPasswordTextBox.Text,
                validationCancellation.Token);
            RecreateHttpClient();
            certificateStatusLabel.Text = T2medApiHttpsClient.GetCertificateStatus();
            MessageBox.Show(
                this,
                "Das HTTPS-Zertifikat wurde geprüft und für Euvejo-API Test gespeichert.",
                "HTTPS-Zertifikat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            certificateStatusLabel.Text = T2medApiHttpsClient.GetCertificateStatus();
            MessageBox.Show(
                this,
                "Das Zertifikat konnte nicht eingerichtet werden:\n" + ex.Message,
                "HTTPS-Zertifikat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SelectCertificateAsync()
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Öffentliches Zertifikat (*.cer)|*.cer|Alle Dateien (*.*)|*.*",
            Title = "HTTPS-Zertifikat der Euvejo-API auswählen"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Uri apiBaseUri;
        try
        {
            apiBaseUri = GetCertificateSetupBaseUri();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "HTTPS-Zertifikat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        certificateStatusLabel.Text = "Zertifikat, Kennwort und HTTPS-Verbindung werden geprüft...";
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await T2medApiHttpsClient.InstallCertificateAsync(
                dialog.FileName,
                apiBaseUri,
                apiPasswordTextBox.Text,
                cancellation.Token);
            RecreateHttpClient();
            certificateStatusLabel.Text = T2medApiHttpsClient.GetCertificateStatus();
            MessageBox.Show(
                this,
                "Das HTTPS-Zertifikat wurde geprüft und für Euvejo-API Test gespeichert.",
                "HTTPS-Zertifikat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            certificateStatusLabel.Text = T2medApiHttpsClient.GetCertificateStatus();
            MessageBox.Show(
                this,
                "Das Zertifikat konnte nicht übernommen werden:\n" + ex.Message,
                "HTTPS-Zertifikat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Uri GetCertificateSetupBaseUri()
    {
        if (string.IsNullOrWhiteSpace(apiPasswordTextBox.Text))
        {
            throw new InvalidOperationException("Bitte zuerst das API-Kennwort eingeben.");
        }

        return T2medApiHttpsClient.CreateBaseUri(baseUrlTextBox.Text);
    }

    private void RecreateHttpClient()
    {
        var previousClient = httpClient;
        httpClient = T2medApiHttpsClient.CreateHttpClient();
        previousClient.Dispose();
    }

    private void BrowseUploadPdf()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "PDF-Dateien (*.pdf)|*.pdf|Alle Dateien (*.*)|*.*",
            Title = "PDF für DOK-Upload auswählen"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            uploadPdfPathTextBox.Text = dialog.FileName;
        }
    }

    private async Task ExecuteSelectedEndpointAsync()
    {
        if (endpointComboBox.SelectedItem is not ApiEndpoint endpoint)
        {
            return;
        }

        switch (endpoint.Kind)
        {
            case EndpointKind.File:
                await DownloadFileEndpointAsync(endpoint);
                break;
            case EndpointKind.UploadDocument:
                await UploadDocumentAsync(endpoint);
                break;
            default:
                await ExecuteJsonEndpointAsync(endpoint, append: false);
                break;
        }
    }

    private async Task ExecuteJsonEndpointAsync(ApiEndpoint endpoint, bool append)
    {
        await RunWithUiLockAsync(async () =>
        {
            var uri = BuildUri(endpoint);
            Log($"GET {uri}");

            using var response = await httpClient.GetAsync(uri);
            var body = await response.Content.ReadAsStringAsync();
            var display = FormatJsonOrText(body);

            Log($"{(int)response.StatusCode} {response.ReasonPhrase}");
            var block = CreateResponseBlock(endpoint, response.StatusCode, response.ReasonPhrase, display);

            if (append)
            {
                responseTextBox.AppendText(block);
            }
            else
            {
                responseTextBox.Text = block;
            }
        });
    }

    private async Task UploadDocumentAsync(ApiEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(uploadPdfPathTextBox.Text) || !File.Exists(uploadPdfPathTextBox.Text))
        {
            MessageBox.Show("Bitte eine vorhandene PDF-Datei für den DOK-Upload auswählen.", "PDF fehlt", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunWithUiLockAsync(async () =>
        {
            var uri = BuildUri(endpoint);
            Log($"POST {uri}");

            await using var stream = File.OpenRead(uploadPdfPathTextBox.Text);
            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(fileContent, "file", Path.GetFileName(uploadPdfPathTextBox.Text));
            content.Add(new StringContent(uploadTitleTextBox.Text.Trim()), "title");
            content.Add(new StringContent(Path.GetFileName(uploadPdfPathTextBox.Text)), "fileName");

            using var response = await httpClient.PostAsync(uri, content);
            var body = await response.Content.ReadAsStringAsync();
            responseTextBox.Text = CreateResponseBlock(endpoint, response.StatusCode, response.ReasonPhrase, FormatJsonOrText(body));
            Log($"{(int)response.StatusCode} {response.ReasonPhrase}");
        });
    }

    private async Task DownloadFileEndpointAsync(ApiEndpoint endpoint)
    {
        EnsureChartEntryId();
        await RunWithUiLockAsync(async () =>
        {
            var uri = BuildUri(endpoint);
            Log($"GET {uri}");

            using var response = await httpClient.GetAsync(uri);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                responseTextBox.Text = FormatJsonOrText(errorBody);
                Log($"{(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var fileName = GetFileName(response.Content.Headers, endpoint, contentType);
            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                fileName);

            await File.WriteAllBytesAsync(outputPath, bytes);
            responseTextBox.Text = $"Datei gespeichert:\r\n{outputPath}\r\n\r\nContent-Type: {contentType}\r\nBytes: {bytes.Length}";
            Log($"Gespeichert: {outputPath}");
        });
    }

    private Uri BuildUri(ApiEndpoint endpoint)
    {
        T2medApiHttpsClient.EnsureCertificateAvailable();
        httpClient.DefaultRequestHeaders.Remove("X-Euvejo-Api-Password");
        if (!string.IsNullOrEmpty(apiPasswordTextBox.Text))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Euvejo-Api-Password", apiPasswordTextBox.Text);
        }
        var baseUri = T2medApiHttpsClient.CreateBaseUri(baseUrlTextBox.Text);
        var context = CreateContext();
        var path = endpoint.BuildPath(context);
        return new Uri(baseUri, path.TrimStart('/'));
    }

    private ApiTestContext CreateContext()
    {
        return new ApiTestContext
        {
            PatientNumber = (long)patientNumberInput.Value,
            PatientSearch = patientSearchTextBox.Text.Trim(),
            ChartEntryId = chartEntryIdTextBox.Text.Trim(),
            Pzn = pznTextBox.Text.Trim(),
            From = DateOnly.FromDateTime(fromDatePicker.Value.Date),
            To = DateOnly.FromDateTime(toDatePicker.Value.Date),
            PrescriptionType = prescriptionTypeComboBox.SelectedItem?.ToString() ?? "kasse",
            Medication = medicationTextBox.Text.Trim(),
            ActiveIngredient = activeIngredientTextBox.Text.Trim(),
            SinglePrice = singlePriceTextBox.Text.Trim()
        };
    }

    private static string RequireText(string text, string fieldName)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException($"{fieldName} ist erforderlich.");
        }

        return text;
    }

    private void EnsureChartEntryId()
    {
        if (string.IsNullOrWhiteSpace(chartEntryIdTextBox.Text))
        {
            throw new InvalidOperationException("Karteieintrag ObjectId / DOK ObjectId ist erforderlich.");
        }
    }

    private static string BuildPrescriptionStatisticsPath(ApiTestContext context)
    {
        return "/api/prescription-statistics?" + string.Join("&",
        [
            $"from={context.From:yyyy-MM-dd}",
            $"to={context.To:yyyy-MM-dd}",
            $"rezeptTyp={Uri.EscapeDataString(context.PrescriptionType)}"
        ]);
    }

    private static string BuildPrescriptionStatisticsPatientsPath(ApiTestContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Medication))
        {
            throw new InvalidOperationException("Medikament ist für die Patientenliste erforderlich.");
        }

        var query = new List<string>
        {
            $"from={context.From:yyyy-MM-dd}",
            $"to={context.To:yyyy-MM-dd}",
            $"rezeptTyp={Uri.EscapeDataString(context.PrescriptionType)}",
            $"medication={Uri.EscapeDataString(context.Medication)}"
        };

        if (!string.IsNullOrWhiteSpace(context.ActiveIngredient))
        {
            query.Add($"wirkstoff={Uri.EscapeDataString(context.ActiveIngredient)}");
        }

        if (!string.IsNullOrWhiteSpace(context.SinglePrice))
        {
            query.Add($"einzelpreis={Uri.EscapeDataString(context.SinglePrice)}");
        }

        return "/api/prescription-statistics/patients?" + string.Join("&", query);
    }

    private async Task RunWithUiLockAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
            statusLabel.Text = "Fertig";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Fehler";
            responseTextBox.Text = ex.ToString();
            Log(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        executeButton.Enabled = !busy;
        downloadDocumentButton.Enabled = !busy;
        downloadImageButton.Enabled = !busy;
        browseUploadPdfButton.Enabled = !busy;
        autoConfigureCertificateButton.Enabled = !busy;
        selectCertificateButton.Enabled = !busy;
        statusLabel.Text = busy ? "Läuft..." : statusLabel.Text;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void UpdateEndpointDescription()
    {
        if (endpointComboBox.SelectedItem is ApiEndpoint endpoint)
        {
            statusLabel.Text = endpoint.Description;
        }
    }

    private void Log(string message)
    {
        logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private string FormatJsonOrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, jsonOptions);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string CreateResponseBlock(ApiEndpoint endpoint, HttpStatusCode statusCode, string? reasonPhrase, string display)
    {
        return new StringBuilder()
            .AppendLine(endpoint.Title)
            .AppendLine(endpoint.Route)
            .AppendLine($"{(int)statusCode} {reasonPhrase}")
            .AppendLine()
            .AppendLine(display)
            .AppendLine()
            .AppendLine(new string('-', 100))
            .AppendLine()
            .ToString();
    }

    private static string GetFileName(HttpContentHeaders headers, ApiEndpoint endpoint, string contentType)
    {
        var fileName = headers.ContentDisposition?.FileNameStar
            ?? headers.ContentDisposition?.FileName?.Trim('"');

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return SanitizeFileName(fileName);
        }

        var extension = contentType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".bin"
        };

        return SanitizeFileName($"{endpoint.Title}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }

}

internal enum EndpointKind
{
    Json,
    File,
    UploadDocument
}

internal sealed class ApiEndpoint
{
    public ApiEndpoint(string title, string route, string description, EndpointKind kind, Func<ApiTestContext, string> buildPath)
    {
        Title = title;
        Route = route;
        Description = description;
        Kind = kind;
        BuildPath = buildPath;
    }

    public string Title { get; }
    public string Route { get; }
    public string Description { get; }
    public EndpointKind Kind { get; }
    public Func<ApiTestContext, string> BuildPath { get; }
    public string DisplayName => $"{Title} - {Route}";
}

internal sealed class ApiTestContext
{
    public long PatientNumber { get; init; }
    public string PatientSearch { get; init; } = "";
    public string ChartEntryId { get; init; } = "";
    public string Pzn { get; init; } = "";
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string PrescriptionType { get; init; } = "kasse";
    public string Medication { get; init; } = "";
    public string ActiveIngredient { get; init; } = "";
    public string SinglePrice { get; init; } = "";
}
