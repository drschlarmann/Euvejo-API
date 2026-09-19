using T2med_Api_Config;

var outputPath = args.FirstOrDefault()
    ?? Path.Combine(Path.GetTempPath(), "T2med-Api-Config-layout.png");

Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        using var form = new ConfigForm();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10000, -10000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();

        Assert(form.ClientSize.Width >= 820 && form.ClientSize.Height >= 650, "Formular ist zu klein.");
        var controls = Descendants(form).ToArray();
        var tabs = controls.OfType<TabControl>().Single();
        Assert(tabs.TabPages.Count == 2, "Erwartete Tabs fehlen.");
        var passwordFields = controls.OfType<TextBox>()
            .Where(textBox => textBox.UseSystemPasswordChar)
            .ToArray();
        Assert(passwordFields.Length >= 5, "API-Kennwortfelder fehlen.");
        Assert(controls.OfType<Label>().Any(label => label.Text.Contains("Mindestens 6 Zeichen", StringComparison.Ordinal)),
            "Hinweis zum API-Kennwort fehlt.");
        foreach (var button in controls.OfType<Button>())
        {
            Assert(button.Height >= 40, $"Button '{button.Text}' ist zu niedrig.");
            Assert(button.PreferredSize.Width <= button.Width + 8, $"Button '{button.Text}' schneidet Text ab.");
        }

        tabs.SelectedIndex = 1;
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        bitmap.Save(outputPath);
        form.Hide();
    }
    catch (Exception exception)
    {
        failure = exception;
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (failure is not null)
{
    throw failure;
}
Console.WriteLine($"PASS: Konfigurationsformular gerendert: {outputPath}");

static IEnumerable<Control> Descendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child))
        {
            yield return descendant;
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
