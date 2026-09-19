using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using T2med_Api;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
static CalendarQuery Parse(string query)
{
    if (!CalendarQuery.TryParse(new QueryCollection(QueryHelpers.ParseQuery(query)), out var parsed, out var error))
        throw new Exception(error);
    return parsed!;
}

var spring = Parse("?from=2026-03-29&to=2026-03-29");
Check((spring.UntilUtc - spring.FromUtc).TotalHours == 23, "Sommerzeitbeginn: 23 Stunden für einen Berliner Kalendertag");
var autumn = Parse("?from=2026-10-25&to=2026-10-25");
Check((autumn.UntilUtc - autumn.FromUtc).TotalHours == 25, "Sommerzeitende: 25 Stunden für einen Berliner Kalendertag");
Check(Parse("?from=2026-09-01&to=2026-09-30").UntilUtc == new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc),
    "Enddatum vollständig enthalten, unabhängig von der Server-Zeitzone");

if (args.Contains("--live"))
{
    var cs = Environment.GetEnvironmentVariable("EUVEJO_CALENDAR_TEST_CONNECTION")
        ?? throw new Exception("EUVEJO_CALENDAR_TEST_CONNECTION muss für --live gesetzt sein.");
    var cb = new NpgsqlConnectionStringBuilder(cs) { Options = "-c default_transaction_read_only=on", Timeout = 5 };
    await using var ds = NpgsqlDataSource.Create(cb.ConnectionString);
    var reader = new CalendarReader(ds);
    var date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    var page = await reader.GetAppointmentsAsync(Parse($"?from={date}&to={date}&limit=1"), CancellationToken.None);
    Check(page.Items.Count <= 1, "Live-Abfrage: Kalenderseite ausgelesen");
    await reader.GetTypesAsync(CancellationToken.None);
    await reader.GetResourcesAsync(CancellationToken.None);
    Console.WriteLine("PASS: Live-Termintypen und Ressourcen gelesen (Inhalte nicht ausgegeben)");
    return;
}

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
var config = new JsonObject();
ApiPasswordSecurity.SetPassword(config, "calendar-test-only");
builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(config.ToJsonString())));
builder.Services.AddSingleton(ApiPasswordSecurity.LoadVerifier(key => builder.Configuration[key]));
var fake = new FakeReader();
builder.Services.AddSingleton<ICalendarReader>(fake);
await using var app = builder.Build();
app.UseMiddleware<ApiPasswordMiddleware>();
app.MapCalendarEndpoints();
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    foreach (var path in new[] { "appointments?from=2026-09-18&to=2026-09-18", "types", "resources" })
    {
        using var unauthorized = await http.GetAsync("/api/calendar/" + path);
        Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Kennwortschutz: " + path);
    }
    Check(fake.Calls == 0, "Ohne Kennwort wird keine Datenbankabfrage gestartet");
    http.DefaultRequestHeaders.Add(ApiPasswordSecurity.HeaderName, "calendar-test-only");
    foreach (var invalid in new[] { "", "?from=2026-02-30&to=2026-03-01", "?from=2026-09-20&to=2026-09-18",
        "?from=2026-09-01&to=2026-10-02", "?from=2026-09-18&to=2026-09-18&limit=1001",
        "?from=2026-09-18&to=2026-09-18&offset=-1", "?from=2026-09-18&to=9999-12-31",
        "?from=2026-09-18&from=2026-09-19&to=2026-09-19" })
    {
        using var bad = await http.GetAsync("/api/calendar/appointments" + invalid);
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "Ungültige Eingabe: " + invalid);
    }
    Check(fake.Calls == 0, "Ungültige Filter erreichen die Datenbank nicht");
    using var response = await http.GetAsync("/api/calendar/appointments?from=2026-09-18&to=2026-09-18&limit=2&offset=4&typeId=type-test&resourceId=room-test");
    response.EnsureSuccessStatusCode();
    var page = await response.Content.ReadFromJsonAsync<CalendarPage>();
    Check(page is { HasMore: true, NextOffset: 6, Offset: 4, Limit: 2 }, "JSON-Seitenvertrag mit Fortsetzung");
    Check(response.Headers.CacheControl?.NoStore == true, "Kalenderantwort wird nicht zwischengespeichert");
    Check(fake.LastQuery is { TypeId: "type-test", ResourceId: "room-test" }, "Typ- und Ressourcenfilter werden übergeben");
    foreach (var path in new[] { "types", "resources" })
    {
        using var result = await http.GetAsync("/api/calendar/" + path);
        result.EnsureSuccessStatusCode();
        Check(result.Headers.CacheControl?.NoStore == true, "Lesender Stammdaten-Endpunkt: " + path);
    }
    using var write = await http.PostAsJsonAsync("/api/calendar/appointments", new { });
    Check(write.StatusCode == HttpStatusCode.MethodNotAllowed, "Kalender-Schreibzugriff ist nicht registriert");
    fake.Failure = new NpgsqlException("private DB details");
    using var unavailable = await http.GetAsync("/api/calendar/types");
    Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable
        && !(await unavailable.Content.ReadAsStringAsync()).Contains("private DB details"), "DB-Fehler: 503 ohne interne Details");
    fake.Failure = new PostgresException("private schema", "ERROR", "ERROR", "42703");
    using var incompatible = await http.GetAsync("/api/calendar/resources");
    Check(incompatible.StatusCode == HttpStatusCode.ServiceUnavailable
        && (await incompatible.Content.ReadAsStringAsync()).Contains("Kalenderschema"), "Schemaabweichung wird verständlich gemeldet");
}
finally { await app.StopAsync(); }
Console.WriteLine("HTTP-/Validierungstests erfolgreich. Live-SQL wird nur bei --live geprüft.");

sealed class FakeReader : ICalendarReader
{
    public int Calls { get; private set; }
    public CalendarQuery? LastQuery { get; private set; }
    public Exception? Failure { get; set; }
    private void Count() { Calls++; if (Failure is not null) throw Failure; }
    public Task<CalendarPage> GetAppointmentsAsync(CalendarQuery query, CancellationToken cancellationToken)
    {
        Count(); LastQuery = query;
        return Task.FromResult(new CalendarPage(query.From, query.To, "Europe/Berlin", query.Limit, query.Offset,
            true, query.Offset + query.Limit, Array.Empty<CalendarAppointment>()));
    }
    public Task<IReadOnlyList<CalendarType>> GetTypesAsync(CancellationToken cancellationToken)
    { Count(); return Task.FromResult<IReadOnlyList<CalendarType>>(Array.Empty<CalendarType>()); }
    public Task<IReadOnlyList<CalendarResource>> GetResourcesAsync(CancellationToken cancellationToken)
    { Count(); return Task.FromResult<IReadOnlyList<CalendarResource>>(Array.Empty<CalendarResource>()); }
}
