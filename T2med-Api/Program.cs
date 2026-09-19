using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using T2med_Api;

var secureConfigurationPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
var protectConfiguration = SecureConfiguration.IsInstalledLocation(secureConfigurationPath)
    || !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
var secureConfigurationJson = SecureConfiguration.Load(
    secureConfigurationPath,
    migratePlaintext: protectConfiguration,
    restrictToAdministrators: protectConfiguration);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(secureConfigurationJson)));
builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0)
    builder.Configuration.AddCommandLine(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "euvejo-api";
});
var httpsCertificateProvider = HttpsHosting.Configure(builder);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("T2medDatabase")
    ?? throw new InvalidOperationException("Connection string 'T2medDatabase' is missing.");
var cdnConnectionString = builder.Configuration.GetConnectionString("T2medCdnDatabase")
    ?? throw new InvalidOperationException("Connection string 'T2medCdnDatabase' is missing.");
var mmiConnectionString = builder.Configuration.GetConnectionString("MMIDatabase")
    ?? throw new InvalidOperationException("Connection string 'MMIDatabase' is missing.");
var cdnRoot = builder.Configuration["T2med:CdnRoot"]
    ?? throw new InvalidOperationException("Configuration value 'T2med:CdnRoot' is missing.");
var apiPasswordVerifier = ApiPasswordSecurity.LoadVerifier(key => builder.Configuration[key]);
builder.Services.AddSingleton(apiPasswordVerifier);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ICalendarReader, CalendarReader>();
builder.Services.AddSingleton<CalendarBooking>();
builder.Services.AddSingleton(_ => new CdnDocumentStore(NpgsqlDataSource.Create(cdnConnectionString), cdnRoot));
builder.Services.AddSingleton(_ => new MmiMedicationPriceStore(NpgsqlDataSource.Create(mmiConnectionString)));

var app = builder.Build();
if (httpsCertificateProvider is not null)
    app.Lifetime.ApplicationStopped.Register(httpsCertificateProvider.Dispose);

app.UseMiddleware<ApiPasswordMiddleware>();
app.MapCalendarEndpoints();
await app.Services.GetRequiredService<CalendarBooking>().InitializeAsync(CancellationToken.None);
app.MapPost("/api/calendar/eva", async (EvaCalendarRequest request, CalendarBooking booking, HttpContext context) => { context.Response.Headers.CacheControl = "no-store"; return Results.Ok(await booking.ExecuteAsync(request, context.RequestAborted)); });

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    product = "Euvejo-API",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown"
}));

await using (var scope = app.Services.CreateAsyncScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var cdnDocumentStore = scope.ServiceProvider.GetRequiredService<CdnDocumentStore>();
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        logger.LogInformation("PostgreSQL database connection to '{Database}' established.", connection.Database);

        await using var cdnConnection = await cdnDocumentStore.DataSource.OpenConnectionAsync();
        logger.LogInformation("PostgreSQL CDN database connection to '{Database}' established.", cdnConnection.Database);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "PostgreSQL database connection could not be established.");
        throw;
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/patients/{nummer:long}", async (long nummer, NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    command.CommandText = """
        select
            objectid,
            revision,
            creationtimestamp,
            modificationtimestamp,
            nummer,
            geschlecht,
            namensdaten_anrede,
            namensdaten_titel,
            namensdaten_vorname,
            namensdaten_nachname,
            namensdaten_vorsatzwort,
            namensdaten_zusatz,
            geburtsdaten_datum_normalizeddate,
            geburtsdaten_datum_precision,
            geburtsdaten_name,
            geburtsdaten_ort,
            anschrift_strasse,
            anschrift_hausnummer,
            anschrift_plz,
            anschrift_ort,
            anschrift_land_kennzeichen,
            anschrift_zusatz,
            anschrift_postfach,
            anschrift_kommentar,
            postfachadresse_strasse,
            postfachadresse_hausnummer,
            postfachadresse_plz,
            postfachadresse_ort,
            postfachadresse_land_kennzeichen,
            postfachadresse_zusatz,
            postfachadresse_postfach,
            postfachadresse_kommentar,
            zweitanschrift_strasse,
            zweitanschrift_hausnummer,
            zweitanschrift_plz,
            zweitanschrift_ort,
            zweitanschrift_land_kennzeichen,
            zweitanschrift_zusatz,
            zweitanschrift_postfach,
            zweitanschrift_kommentar,
            aufnahmedatum,
            sterbedatum_normalizeddate,
            sterbedatum_precision,
            chroniker,
            pflegegrad,
            hausarzt_objectid,
            staatsangehoerigkeit_land_kennzeichen,
            benachrichtigungerlaubt,
            bevorzugterbenachrichtigungsweg,
            patientenquittungerwuenscht,
            erezeptimmerdrucken,
            padeinverstaendniserklaerungliegtvor
        from aps.patient
        where nummer = @nummer
        limit 1;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = $"Patient mit Nummer {nummer} wurde nicht gefunden." });
    }

    var patient = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < reader.FieldCount; i++)
    {
        patient[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
    }

    return Results.Ok(patient);
})
.WithName("GetPatientByNummer");

app.MapGet("/api/patients/search", async (string? name, NpgsqlDataSource dataSource) =>
{
    var query = (name ?? "").Trim();
    if (query.Length < 2)
    {
        return Results.BadRequest(new { message = "Bitte mindestens zwei Zeichen fuer die Patientensuche angeben." });
    }

    var tokens = Regex.Split(query, @"\s+")
        .Select(token => token.Trim())
        .Where(token => token.Length > 0)
        .Take(6)
        .ToList();

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    var tokenConditions = new List<string>();
    for (var index = 0; index < tokens.Count; index++)
    {
        var parameterName = $"token{index}";
        tokenConditions.Add($"""
            concat_ws(' ', nummer::text, namensdaten_nachname, namensdaten_vorname, geburtsdaten_datum_normalizeddate::text) ilike @{parameterName}
            """);
        command.Parameters.AddWithValue(parameterName, NpgsqlDbType.Text, $"%{tokens[index]}%");
    }

    command.CommandText = $"""
        select
            nummer,
            namensdaten_nachname,
            namensdaten_vorname,
            geburtsdaten_datum_normalizeddate
        from aps.patient
        where {string.Join(" and ", tokenConditions)}
        order by
            case when namensdaten_nachname ilike @prefix then 0 else 1 end,
            namensdaten_nachname nulls last,
            namensdaten_vorname nulls last,
            nummer
        limit 50;
        """;

    command.Parameters.AddWithValue("prefix", NpgsqlDbType.Text, $"{tokens[0]}%");

    var results = new List<PatientSearchResultDto>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        results.Add(new PatientSearchResultDto
        {
            Nummer = GetValue<long>(reader, "nummer"),
            Nachname = GetValue<string?>(reader, "namensdaten_nachname"),
            Vorname = GetValue<string?>(reader, "namensdaten_vorname"),
            Geburtsdatum = GetValue<DateOnly?>(reader, "geburtsdaten_datum_normalizeddate")
        });
    }

    return Results.Ok(new PatientSearchResponse { Patienten = results });
})
.WithName("SearchPatientsByName");

app.MapGet("/api/medications/{pzn}/price", async (string pzn, MmiMedicationPriceStore priceStore, NpgsqlDataSource dataSource) =>
{
    var normalizedPzn = NormalizePzn(pzn);
    if (normalizedPzn.Length == 0)
    {
        return Results.BadRequest(new { message = "Bitte eine gueltige PZN angeben." });
    }

    List<MedicationPriceDto> result;
    try
    {
        result = await priceStore.FindByPznAsync(normalizedPzn);
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
    {
        result = await FindT2medMedicationPricesAsync(dataSource, normalizedPzn);
    }
    catch (NpgsqlException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "MMI-Datenbank nicht erreichbar");
    }

    if (result.Count == 0)
    {
        return Results.NotFound(new { message = $"Fuer PZN {normalizedPzn} wurde in der MMI-Datenbank kein Preis gefunden." });
    }

    return Results.Ok(new MedicationPriceResponse
    {
        Pzn = normalizedPzn,
        Preise = result
    });
})
.WithName("GetMedicationPriceByPzn");

app.MapGet("/api/prescription-statistics", async (string from, string to, string? rezeptTyp, NpgsqlDataSource dataSource) =>
{
    if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
    {
        return Results.BadRequest(new { message = "Bitte gueltige Datumswerte fuer 'from' und 'to' im Format yyyy-MM-dd angeben." });
    }

    if (toDate < fromDate)
    {
        return Results.BadRequest(new { message = "'to' darf nicht vor 'from' liegen." });
    }

    var normalizedPrescriptionType = NormalizePrescriptionType(rezeptTyp);
    if (normalizedPrescriptionType is null)
    {
        return Results.BadRequest(new { message = "rezeptTyp muss 'kasse' oder 'privat' sein." });
    }

    var result = await GetPrescriptionStatisticsAsync(dataSource, fromDate, toDate, normalizedPrescriptionType);
    return Results.Ok(new PrescriptionStatisticsResponse
    {
        Von = fromDate,
        Bis = toDate,
        RezeptTyp = normalizedPrescriptionType,
        Eintraege = result
    });
})
.WithName("GetPrescriptionStatistics");

app.MapGet("/api/prescription-statistics/patients", async (
    string from,
    string to,
    string? rezeptTyp,
    string medication,
    string wirkstoff,
    decimal? einzelpreis,
    NpgsqlDataSource dataSource) =>
{
    if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
    {
        return Results.BadRequest(new { message = "Bitte gueltige Datumswerte fuer 'from' und 'to' im Format yyyy-MM-dd angeben." });
    }

    if (toDate < fromDate)
    {
        return Results.BadRequest(new { message = "'to' darf nicht vor 'from' liegen." });
    }

    var normalizedPrescriptionType = NormalizePrescriptionType(rezeptTyp);
    if (normalizedPrescriptionType is null)
    {
        return Results.BadRequest(new { message = "rezeptTyp muss 'kasse' oder 'privat' sein." });
    }

    var result = await GetPrescriptionStatisticsPatientsAsync(dataSource, fromDate, toDate, normalizedPrescriptionType, medication, wirkstoff, einzelpreis);
    return Results.Ok(new PrescriptionStatisticsPatientsResponse
    {
        Von = fromDate,
        Bis = toDate,
        RezeptTyp = normalizedPrescriptionType,
        Medikament = medication,
        Wirkstoff = wirkstoff,
        Einzelpreis = einzelpreis,
        Patienten = result
    });
})
.WithName("GetPrescriptionStatisticsPatients");

app.MapGet("/api/day-lab", async (string from, string to, NpgsqlDataSource dataSource) =>
{
    if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
    {
        return Results.BadRequest(new { message = "Bitte gueltige Datumswerte fuer 'from' und 'to' im Format yyyy-MM-dd angeben." });
    }

    if (toDate < fromDate)
    {
        return Results.BadRequest(new { message = "'to' darf nicht vor 'from' liegen." });
    }

    var result = await GetDailyLabEntriesAsync(dataSource, fromDate, toDate);
    return Results.Ok(new DailyLabResponse
    {
        Von = fromDate,
        Bis = toDate,
        Eintraege = result
    });
})
.WithName("GetDailyLab");

app.MapGet("/api/patients/{nummer:long}/current-case/diagnoses", async (long nummer, NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    command.CommandText = """
        with patient_by_number as (
            select
                objectid,
                nummer,
                namensdaten_nachname,
                namensdaten_vorname,
                geburtsdaten_datum_normalizeddate
            from aps.patient
            where nummer = @nummer
        ),
        current_case as (
            select
                bf.objectid,
                bf.revision,
                bf.creationtimestamp,
                bf.modificationtimestamp,
                bf.beginn,
                bf.ende,
                bf.classid,
                bf.abrechnung_quartal,
                bf.patientenvertrag_objectid
            from aps.behandlungsfall bf
            join aps.patientenvertrag pv on pv.objectid = bf.patientenvertrag_objectid
            join patient_by_number p on p.objectid = pv.patient_objectid
            order by
                case when bf.ende is null then 0 else 1 end,
                coalesce(bf.beginn, bf.aufnahmedatum::date, bf.creationtimestamp::date) desc nulls last,
                bf.modificationtimestamp desc nulls last,
                bf.creationtimestamp desc nulls last
            limit 1
        ),
        current_case_diagnoses as (
            select distinct on (da.diagnose_objectid)
                da.diagnose_objectid,
                da.zeitpunkt
            from aps.diagnoseaktivierung da
            join current_case cc on cc.objectid = da.behandlungsfall_objectid
            order by da.diagnose_objectid, da.zeitpunkt desc
        )
        select
            p.nummer as patient_nummer,
            p.namensdaten_nachname as patient_nachname,
            p.namensdaten_vorname as patient_vorname,
            p.geburtsdaten_datum_normalizeddate as patient_geburtsdatum,
            cc.objectid as behandlungsfall_objectid,
            cc.revision as behandlungsfall_revision,
            cc.creationtimestamp as behandlungsfall_creationtimestamp,
            cc.modificationtimestamp as behandlungsfall_modificationtimestamp,
            cc.beginn as behandlungsfall_beginn,
            cc.ende as behandlungsfall_ende,
            cc.classid as behandlungsfall_classid,
            cc.abrechnung_quartal as behandlungsfall_abrechnung_quartal,
            d.objectid as diagnose_objectid,
            d.revision as diagnose_revision,
            d.creationtimestamp as diagnose_creationtimestamp,
            d.beginn as diagnose_beginn,
            d.ende as diagnose_ende,
            d.icd,
            d.klartext,
            d.erlaeuterung,
            d.lokalisation,
            d.relevanz,
            d.sicherheit,
            d.fachinfoglobalid,
            d.internebemerkung
        from patient_by_number p
        left join current_case cc on true
        left join current_case_diagnoses ccd on true
        left join aps.gestelltediagnose d on d.objectid = ccd.diagnose_objectid
        order by ccd.zeitpunkt desc nulls last, d.beginn desc nulls last, d.icd;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = $"Patient mit Nummer {nummer} wurde nicht gefunden." });
    }

    var behandlungsfallObjectId = reader["behandlungsfall_objectid"] as string;
    if (behandlungsfallObjectId is null)
    {
        return Results.NotFound(new { message = $"Fuer Patient Nummer {nummer} wurde kein Behandlungsfall gefunden." });
    }

    var result = new CurrentCaseDiagnosesResponse
    {
        PatientNummer = nummer,
        PatientNachname = GetValue<string?>(reader, "patient_nachname"),
        PatientVorname = GetValue<string?>(reader, "patient_vorname"),
        PatientGeburtsdatum = GetValue<DateOnly?>(reader, "patient_geburtsdatum"),
        Behandlungsfall = new CurrentCaseDto
        {
            ObjectId = behandlungsfallObjectId,
            Revision = GetValue<int>(reader, "behandlungsfall_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_creationtimestamp"),
            ModificationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_modificationtimestamp"),
            Beginn = GetValue<DateOnly?>(reader, "behandlungsfall_beginn"),
            Ende = GetValue<DateOnly?>(reader, "behandlungsfall_ende"),
            ClassId = GetValue<int>(reader, "behandlungsfall_classid"),
            AbrechnungQuartal = GetValue<int?>(reader, "behandlungsfall_abrechnung_quartal")
        }
    };

    do
    {
        var diagnoseObjectId = reader["diagnose_objectid"] as string;
        if (diagnoseObjectId is null)
        {
            continue;
        }

        result.Diagnosen.Add(new DiagnosisDto
        {
            ObjectId = diagnoseObjectId,
            Revision = GetValue<int>(reader, "diagnose_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "diagnose_creationtimestamp"),
            Beginn = GetValue<DateTimeOffset>(reader, "diagnose_beginn"),
            Ende = GetValue<DateTimeOffset?>(reader, "diagnose_ende"),
            Icd = GetValue<string?>(reader, "icd"),
            Klartext = GetValue<string?>(reader, "klartext"),
            Erlaeuterung = GetValue<string?>(reader, "erlaeuterung"),
            Lokalisation = GetValue<int>(reader, "lokalisation"),
            Relevanz = GetValue<int>(reader, "relevanz"),
            Sicherheit = GetValue<int>(reader, "sicherheit"),
            FachinfoGlobalId = GetValue<int>(reader, "fachinfoglobalid"),
            InterneBemerkung = GetValue<string?>(reader, "internebemerkung")
        });
    }
    while (await reader.ReadAsync());

    return Results.Ok(result);
})
.WithName("GetCurrentCaseDiagnosesByPatientNummer");

app.MapGet("/api/patients/{nummer:long}/current-case/medication-plan", async (long nummer, NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    var medicationEntryColumns = await GetTableColumnsAsync(connection, "aps", "medikationsplaneintrag");
    var normalizedDosageColumns = await GetTableColumnsAsync(connection, "aps", "medikationseintrag_dosierschema");
    string OptionalMedicationEntryValue(string column, string dataType)
    {
        return medicationEntryColumns.Contains(column)
            ? $"nullif(mpe.{column}, '')"
            : $"null::{dataType}";
    }

    var hasNormalizedDosageSchema = new[]
    {
        "medikationseintrag_objectid",
        "dosierunganzahl",
        "freitext",
        "tageszeiten",
        "dosierschemaeintraege_order"
    }.All(normalizedDosageColumns.Contains);

    var normalizedDosageJoin = hasNormalizedDosageSchema
        ? """
            left join lateral (
                select
                    max(case when (coalesce(schema.tageszeiten, 0) & 2) <> 0
                                  or (schema.tageszeiten is null and schema.dosierschemaeintraege_order = 0)
                             then case when schema.dosierunganzahl ~ '^-?[0-9]+/1$'
                                       then split_part(schema.dosierunganzahl, '/', 1)
                                       else schema.dosierunganzahl end end) as morgens,
                    max(case when (coalesce(schema.tageszeiten, 0) & 4) <> 0
                                  or (schema.tageszeiten is null and schema.dosierschemaeintraege_order = 1)
                             then case when schema.dosierunganzahl ~ '^-?[0-9]+/1$'
                                       then split_part(schema.dosierunganzahl, '/', 1)
                                       else schema.dosierunganzahl end end) as mittags,
                    max(case when (coalesce(schema.tageszeiten, 0) & 8) <> 0
                                  or (schema.tageszeiten is null and schema.dosierschemaeintraege_order = 2)
                             then case when schema.dosierunganzahl ~ '^-?[0-9]+/1$'
                                       then split_part(schema.dosierunganzahl, '/', 1)
                                       else schema.dosierunganzahl end end) as abends,
                    max(case when (coalesce(schema.tageszeiten, 0) & 16) <> 0
                                  or (schema.tageszeiten is null and schema.dosierschemaeintraege_order = 3)
                             then case when schema.dosierunganzahl ~ '^-?[0-9]+/1$'
                                       then split_part(schema.dosierunganzahl, '/', 1)
                                       else schema.dosierunganzahl end end) as nachts,
                    string_agg(nullif(schema.freitext, ''), '; ' order by schema.dosierschemaeintraege_order) as freitext
                from aps.medikationseintrag_dosierschema schema
                where schema.medikationseintrag_objectid = mpe.objectid
            ) dosage on true
            """
        : """
            left join lateral (
                select
                    null::character varying as morgens,
                    null::character varying as mittags,
                    null::character varying as abends,
                    null::character varying as nachts,
                    null::character varying as freitext
            ) dosage on true
            """;

    var optionalDosageProjection = string.Join(
        Environment.NewLine + "                ",
        [
            $"coalesce({OptionalMedicationEntryValue("dosierschema_morgens", "character varying")}, dosage.morgens) as dosierschema_morgens,",
            $"coalesce({OptionalMedicationEntryValue("dosierschema_mittags", "character varying")}, dosage.mittags) as dosierschema_mittags,",
            $"coalesce({OptionalMedicationEntryValue("dosierschema_abends", "character varying")}, dosage.abends) as dosierschema_abends,",
            $"coalesce({OptionalMedicationEntryValue("dosierschema_nachts", "character varying")}, dosage.nachts) as dosierschema_nachts,",
            $"coalesce({OptionalMedicationEntryValue("dosierschema_freitext", "character varying")}, dosage.freitext) as dosierschema_freitext,"
        ]);
    var optionalDosageGroupBy = string.Join(
        Environment.NewLine + "                ",
        new[]
        {
            "dosierschema_morgens",
            "dosierschema_mittags",
            "dosierschema_abends",
            "dosierschema_nachts",
            "dosierschema_freitext"
        }
        .Where(medicationEntryColumns.Contains)
        .Select(column => $"mpe.{column},"));

    command.CommandText = $"""
        with patient_by_number as (
            select
                objectid,
                nummer,
                namensdaten_nachname,
                namensdaten_vorname,
                geburtsdaten_datum_normalizeddate
            from aps.patient
            where nummer = @nummer
        ),
        current_case as (
            select
                bf.objectid,
                bf.revision,
                bf.creationtimestamp,
                bf.modificationtimestamp,
                bf.beginn,
                bf.ende,
                bf.classid,
                bf.abrechnung_quartal
            from aps.behandlungsfall bf
            join aps.patientenvertrag pv on pv.objectid = bf.patientenvertrag_objectid
            join patient_by_number p on p.objectid = pv.patient_objectid
            order by
                case when bf.ende is null then 0 else 1 end,
                coalesce(bf.beginn, bf.aufnahmedatum::date, bf.creationtimestamp::date) desc nulls last,
                bf.modificationtimestamp desc nulls last,
                bf.creationtimestamp desc nulls last
            limit 1
        ),
        latest_medication_plan as (
            select
                mp.objectid,
                mp.ausstellungszeitpunkt
            from aps.medikationsplan mp
            join patient_by_number p on p.objectid = mp.patient_objectid
            order by mp.ausstellungszeitpunkt desc, mp.creationtimestamp desc
            limit 1
        ),
        medication_entries as (
            select
                mpe.objectid,
                mpe.revision,
                mpe.creationtimestamp,
                lmp.ausstellungszeitpunkt as verordnungszeitpunkt,
                mpe.medikationsplaneintraege_order,
                coalesce(mpe.medikationseintrag_arzneimittel, mpe.freitexteintrag_freitext, mpe.rezeptureintrag_rezeptur) as name,
                null::character varying as handelsname,
                string_agg(
                    nullif(coalesce(w.name, '') || case when w.wirkstaerke is null then '' else ' ' || w.wirkstaerke end, ''),
                    ', '
                    order by w.wirkstoffe_order
                ) as wirkstoff,
                null::double precision as wirkstaerke_wert,
                null::character varying as wirkstaerke_einheit,
                mpe.darreichungsform_freitext,
                mpe.darreichungsform_ifacode,
                {optionalDosageProjection}
                mpe.medikationseintrag_pzn as pzn,
                ivp.groesse,
                mpe.medikationseintrag_hinweise as hinweis,
                null::character varying as hinweise,
                mpe.freitexteintrag_freitext as freitext,
                null::integer as arzneimittelverordnung_typ
            from latest_medication_plan lmp
            join aps.medikationsplaneintrag mpe on mpe.medikationsplan_objectid = lmp.objectid
            left join aps.empmedikationsplaneintragzusatz ez on ez.objectid = mpe.empeintragzusatz_objectid
            left join aps.medikationsplaneintrag_wirkstoff w on w.medikationseintrag_objectid = mpe.objectid
            left join aps.individualverordnetepackung ivp on ivp.pzn = mpe.medikationseintrag_pzn
            {normalizedDosageJoin}
            where coalesce(ez.historisiert, false) = false
              and (ez.medikationsenddatum is null or ez.medikationsenddatum >= current_date)
              and mpe.classid in (187, 189, 190)
            group by
                mpe.objectid,
                mpe.revision,
                mpe.creationtimestamp,
                lmp.ausstellungszeitpunkt,
                mpe.medikationsplaneintraege_order,
                mpe.medikationseintrag_arzneimittel,
                mpe.freitexteintrag_freitext,
                mpe.rezeptureintrag_rezeptur,
                mpe.darreichungsform_freitext,
                mpe.darreichungsform_ifacode,
                {optionalDosageGroupBy}
                dosage.morgens,
                dosage.mittags,
                dosage.abends,
                dosage.nachts,
                dosage.freitext,
                mpe.medikationseintrag_pzn,
                ivp.groesse,
                mpe.medikationseintrag_hinweise
        )
        select
            p.nummer as patient_nummer,
            p.namensdaten_nachname as patient_nachname,
            p.namensdaten_vorname as patient_vorname,
            p.geburtsdaten_datum_normalizeddate as patient_geburtsdatum,
            cc.objectid as behandlungsfall_objectid,
            cc.revision as behandlungsfall_revision,
            cc.creationtimestamp as behandlungsfall_creationtimestamp,
            cc.modificationtimestamp as behandlungsfall_modificationtimestamp,
            cc.beginn as behandlungsfall_beginn,
            cc.ende as behandlungsfall_ende,
            cc.classid as behandlungsfall_classid,
            cc.abrechnung_quartal as behandlungsfall_abrechnung_quartal,
            me.objectid as medikation_objectid,
            me.revision as medikation_revision,
            me.creationtimestamp as medikation_creationtimestamp,
            me.verordnungszeitpunkt,
            me.name,
            me.handelsname,
            me.wirkstoff,
            me.wirkstaerke_wert,
            me.wirkstaerke_einheit,
            me.darreichungsform_freitext,
            me.darreichungsform_ifacode,
            me.dosierschema_morgens,
            me.dosierschema_mittags,
            me.dosierschema_abends,
            me.dosierschema_nachts,
            me.dosierschema_freitext,
            me.pzn,
            me.groesse,
            me.hinweis,
            me.hinweise,
            me.freitext,
            me.arzneimittelverordnung_typ
        from patient_by_number p
        left join current_case cc on true
        left join medication_entries me on true
        order by me.medikationsplaneintraege_order nulls last, me.name;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = $"Patient mit Nummer {nummer} wurde nicht gefunden." });
    }

    var behandlungsfallObjectId = reader["behandlungsfall_objectid"] as string;
    if (behandlungsfallObjectId is null)
    {
        return Results.NotFound(new { message = $"Fuer Patient Nummer {nummer} wurde kein Behandlungsfall gefunden." });
    }

    var result = new CurrentCaseMedicationPlanResponse
    {
        PatientNummer = nummer,
        PatientNachname = GetValue<string?>(reader, "patient_nachname"),
        PatientVorname = GetValue<string?>(reader, "patient_vorname"),
        PatientGeburtsdatum = GetValue<DateOnly?>(reader, "patient_geburtsdatum"),
        Behandlungsfall = new CurrentCaseDto
        {
            ObjectId = behandlungsfallObjectId,
            Revision = GetValue<int>(reader, "behandlungsfall_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_creationtimestamp"),
            ModificationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_modificationtimestamp"),
            Beginn = GetValue<DateOnly?>(reader, "behandlungsfall_beginn"),
            Ende = GetValue<DateOnly?>(reader, "behandlungsfall_ende"),
            ClassId = GetValue<int>(reader, "behandlungsfall_classid"),
            AbrechnungQuartal = GetValue<int?>(reader, "behandlungsfall_abrechnung_quartal")
        }
    };

    do
    {
        var medicationObjectId = reader["medikation_objectid"] as string;
        if (medicationObjectId is null)
        {
            continue;
        }

        result.Medikamente.Add(new MedicationDto
        {
            ObjectId = medicationObjectId,
            Revision = GetValue<int>(reader, "medikation_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "medikation_creationtimestamp"),
            Verordnungszeitpunkt = GetValue<DateTimeOffset>(reader, "verordnungszeitpunkt"),
            Name = GetValue<string?>(reader, "name"),
            Handelsname = GetValue<string?>(reader, "handelsname"),
            Wirkstoff = GetValue<string?>(reader, "wirkstoff"),
            WirkstaerkeWert = GetValue<double?>(reader, "wirkstaerke_wert"),
            WirkstaerkeEinheit = GetValue<string?>(reader, "wirkstaerke_einheit"),
            DarreichungsformFreitext = GetValue<string?>(reader, "darreichungsform_freitext"),
            DarreichungsformIfaCode = GetValue<string?>(reader, "darreichungsform_ifacode"),
            DosierschemaMorgens = GetValue<string?>(reader, "dosierschema_morgens"),
            DosierschemaMittags = GetValue<string?>(reader, "dosierschema_mittags"),
            DosierschemaAbends = GetValue<string?>(reader, "dosierschema_abends"),
            DosierschemaNachts = GetValue<string?>(reader, "dosierschema_nachts"),
            DosierschemaFreitext = GetValue<string?>(reader, "dosierschema_freitext"),
            Pzn = GetValue<string?>(reader, "pzn"),
            Groesse = GetValue<string?>(reader, "groesse"),
            Hinweis = GetValue<string?>(reader, "hinweis"),
            Hinweise = GetValue<string?>(reader, "hinweise"),
            Freitext = GetValue<string?>(reader, "freitext"),
            ArzneimittelverordnungTyp = GetValue<int?>(reader, "arzneimittelverordnung_typ")
        });
    }
    while (await reader.ReadAsync());

    return Results.Ok(result);
})
.WithName("GetCurrentCaseMedicationPlanByPatientNummer");

app.MapGet("/api/patients/{nummer:long}/current-case/chart-entries", async (long nummer, NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    command.CommandText = """
        with patient_by_number as (
            select
                objectid,
                nummer,
                namensdaten_nachname,
                namensdaten_vorname,
                geburtsdaten_datum_normalizeddate
            from aps.patient
            where nummer = @nummer
        ),
        current_case as (
            select
                bf.objectid,
                bf.revision,
                bf.creationtimestamp,
                bf.modificationtimestamp,
                bf.beginn,
                bf.ende,
                bf.classid,
                bf.abrechnung_quartal
            from aps.behandlungsfall bf
            join aps.patientenvertrag pv on pv.objectid = bf.patientenvertrag_objectid
            join patient_by_number p on p.objectid = pv.patient_objectid
            order by
                case when bf.ende is null then 0 else 1 end,
                coalesce(bf.beginn, bf.aufnahmedatum::date, bf.creationtimestamp::date) desc nulls last,
                bf.modificationtimestamp desc nulls last,
                bf.creationtimestamp desc nulls last
            limit 1
        ),
        category_by_type as (
            select
                wl.fachinformationstypwhitelist as fachinfoglobalid,
                min(kf.kurzbezeichnung) as kategorie
            from aps.karteifilter_fachinformationstypwhitelist wl
            join aps.karteifilter kf on kf.objectid = wl.karteifilter_objectid
            where kf.classid = 221
              and kf.kurzbezeichnung not in ('Patmed2')
            group by wl.fachinformationstypwhitelist
        )
        select
            p.nummer as patient_nummer,
            p.namensdaten_nachname as patient_nachname,
            p.namensdaten_vorname as patient_vorname,
            p.geburtsdaten_datum_normalizeddate as patient_geburtsdatum,
            cc.objectid as behandlungsfall_objectid,
            cc.revision as behandlungsfall_revision,
            cc.creationtimestamp as behandlungsfall_creationtimestamp,
            cc.modificationtimestamp as behandlungsfall_modificationtimestamp,
            cc.beginn as behandlungsfall_beginn,
            cc.ende as behandlungsfall_ende,
            cc.classid as behandlungsfall_classid,
            cc.abrechnung_quartal as behandlungsfall_abrechnung_quartal,
            k.objectid as karteieintrag_objectid,
            k.revision as karteieintrag_revision,
            k.creationtimestamp as karteieintrag_creationtimestamp,
            k.informationszeitpunkt,
            case
                when ec.kategorie = 'Dokumente' then 'DOK'
                else coalesce(
                    case ec.kategorie
                        when 'Abrechnung' then 'LEI'
                        when 'Anamnese' then 'ANA'
                        when 'Bilder' then 'BIL'
                        when 'Diagnosen' then 'DIA'
                        when 'Labor' then 'LAB'
                        when 'Rezepte' then 'RP.'
                        when 'Therapie' then 'THE'
                        else null
                    end,
                    upper(nullif(ec.kategorie, '')),
                    upper(nullif(k.kuerzel, '')),
                    upper(nullif(k.symbol, ''))
                )
            end as kategorie,
            k.kuerzel,
            k.titel,
            k.text,
            k.symbol,
            k.anamnestisch,
            k.cavehinweis,
            k.fachinfoglobalid,
            k.fachinfoqualifiedid,
            k.ordnungszaehler,
            coalesce(lv.laborwerte_json, '[]'::jsonb)::text as laborwerte_json
        from patient_by_number p
        left join current_case cc on true
        left join aps.karteieintrag k
            on k.patient_objectid = p.objectid
        left join lateral (
            select cbt.kategorie
            from regexp_split_to_table(trim(both '.' from coalesce(k.fachinfoqualifiedid, k.fachinfoglobalid::text)), '\.')
                with ordinality as path(fachinformationstyp, position)
            join category_by_type cbt on cbt.fachinfoglobalid = path.fachinformationstyp::integer
            where path.fachinformationstyp ~ '^[0-9]+$'
            order by path.position desc
            limit 1
        ) ec on true
        left join lateral (
            select jsonb_agg(
                jsonb_build_object(
                    'kurzbezeichnung', lt.kurzbezeichnung,
                    'langbezeichnung', coalesce(nullif(lt.langbezeichnung, ''), lt.kurzbezeichnung),
                    'ergebniswert', lw.ergebniswert,
                    'ergebnistext', lw.ergebnistext,
                    'masseinheit', coalesce(lw.masseinheit, lt.masseinheit),
                    'normwertbereichText', coalesce(lw.normwertbereich_text, lt.standardnormwertbereich_text),
                    'grenzwertindikator', lw.grenzwertindikator
                )
                order by lw.messwerte_order nulls last, lt.kurzbezeichnung
            ) as laborwerte_json
            from aps.laborbefund lb
            join aps.laborwert lw on lw.laborbefund_objectid = lb.objectid
            join aps.labortest lt on lt.objectid = lw.labortest_objectid
            where lb.objectid = k.fachinformationreference
        ) lv on true
        order by k.informationszeitpunkt desc nulls last, k.ordnungszaehler desc nulls last;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = $"Patient mit Nummer {nummer} wurde nicht gefunden." });
    }

    var behandlungsfallObjectId = reader["behandlungsfall_objectid"] as string;
    if (behandlungsfallObjectId is null)
    {
        return Results.NotFound(new { message = $"Fuer Patient Nummer {nummer} wurde kein Behandlungsfall gefunden." });
    }

    var result = new CurrentCaseChartEntriesResponse
    {
        PatientNummer = nummer,
        PatientNachname = GetValue<string?>(reader, "patient_nachname"),
        PatientVorname = GetValue<string?>(reader, "patient_vorname"),
        PatientGeburtsdatum = GetValue<DateOnly?>(reader, "patient_geburtsdatum"),
        Behandlungsfall = new CurrentCaseDto
        {
            ObjectId = behandlungsfallObjectId,
            Revision = GetValue<int>(reader, "behandlungsfall_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_creationtimestamp"),
            ModificationTimestamp = GetValue<DateTimeOffset?>(reader, "behandlungsfall_modificationtimestamp"),
            Beginn = GetValue<DateOnly?>(reader, "behandlungsfall_beginn"),
            Ende = GetValue<DateOnly?>(reader, "behandlungsfall_ende"),
            ClassId = GetValue<int>(reader, "behandlungsfall_classid"),
            AbrechnungQuartal = GetValue<int?>(reader, "behandlungsfall_abrechnung_quartal")
        }
    };

    do
    {
        var chartEntryObjectId = reader["karteieintrag_objectid"] as string;
        if (chartEntryObjectId is null)
        {
            continue;
        }

        result.Karteieintraege.Add(new ChartEntryDto
        {
            ObjectId = chartEntryObjectId,
            Revision = GetValue<int>(reader, "karteieintrag_revision"),
            CreationTimestamp = GetValue<DateTimeOffset?>(reader, "karteieintrag_creationtimestamp"),
            Informationszeitpunkt = GetValue<DateTimeOffset>(reader, "informationszeitpunkt"),
            Kategorie = GetValue<string?>(reader, "kategorie"),
            Kuerzel = GetValue<string?>(reader, "kuerzel"),
            Titel = GetValue<string?>(reader, "titel"),
            Text = GetValue<string?>(reader, "text"),
            Symbol = GetValue<string?>(reader, "symbol"),
            Anamnestisch = GetValue<bool>(reader, "anamnestisch"),
            Cavehinweis = GetValue<bool>(reader, "cavehinweis"),
            FachinfoGlobalId = GetValue<int>(reader, "fachinfoglobalid"),
            FachinfoQualifiedId = GetValue<string?>(reader, "fachinfoqualifiedid"),
            Ordnungszaehler = GetValue<long>(reader, "ordnungszaehler"),
            Laborwerte = JsonSerializer.Deserialize<List<LaborValueDto>>(GetValue<string?>(reader, "laborwerte_json") ?? "[]", JsonSerializerOptions.Web) ?? []
        });
    }
    while (await reader.ReadAsync());

    return Results.Ok(result);
})
.WithName("GetCurrentCaseChartEntriesByPatientNummer");

app.MapGet("/api/patients/{nummer:long}/chart-entries/{chartEntryObjectId}/document", async (
    long nummer,
    string chartEntryObjectId,
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    command.CommandText = """
        select
            k.objectid as karteieintrag_objectid,
            k.text,
            k.fachinfoglobalid,
            v.verweis,
            vi.mimetype,
            vi.name
        from aps.patient p
        join aps.karteieintrag k on k.patient_objectid = p.objectid
        join aps.verweiseintrag v on v.objectid = k.fachinformationreference
        left join aps.verweiseintragdateiinfo vi on vi.verweiseintrag_objectid = v.objectid
        where p.nummer = @nummer
          and k.objectid = @chartEntryObjectId
          and k.fachinfoglobalid = 75
        limit 1;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);
    command.Parameters.AddWithValue("chartEntryObjectId", NpgsqlDbType.Varchar, chartEntryObjectId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = "Dokument-Karteieintrag wurde nicht gefunden." });
    }

    var documentReference = GetValue<string?>(reader, "verweis");
    if (documentReference is null || !documentReference.StartsWith("cdn://", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { message = "Fuer diesen Karteieintrag wurde kein CDN-Dokument gefunden." });
    }

    var mediaType = GetValue<string?>(reader, "mimetype") ?? "application/pdf";
    if (!string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Das Dokument ist kein PDF, sondern '{mediaType}'." });
    }

    var originalFileName = GetValue<string?>(reader, "name") ?? "dokument.pdf";
    var cdnKey = documentReference["cdn://".Length..];
    var contentPath = $"APS/Praxis/Patient/{cdnKey}";

    await reader.DisposeAsync();

    await using var cdnConnection = await cdnDocumentStore.DataSource.OpenConnectionAsync();
    await using var cdnCommand = cdnConnection.CreateCommand();
    cdnCommand.CommandText = """
        select
            storagepath,
            latestversion,
            coalesce(mediatype, @mediaType) as mediatype,
            coalesce(originalfilename, @originalFileName) as originalfilename
        from cdn.contentitem
        where contentpath = @contentPath
           or contentpath = @cdnKey
           or uuid = @cdnKey
        order by modificationtimestamp desc nulls last
        limit 1;
        """;

    cdnCommand.Parameters.AddWithValue("contentPath", NpgsqlDbType.Varchar, contentPath);
    cdnCommand.Parameters.AddWithValue("cdnKey", NpgsqlDbType.Varchar, cdnKey);
    cdnCommand.Parameters.AddWithValue("mediaType", NpgsqlDbType.Varchar, mediaType);
    cdnCommand.Parameters.AddWithValue("originalFileName", NpgsqlDbType.Varchar, originalFileName);

    await using var cdnReader = await cdnCommand.ExecuteReaderAsync();
    if (!await cdnReader.ReadAsync())
    {
        return Results.NotFound(new { message = "CDN-Dokument wurde in t2med_cdn nicht gefunden." });
    }

    var storagePath = GetValue<string?>(cdnReader, "storagepath");
    if (string.IsNullOrWhiteSpace(storagePath))
    {
        return Results.NotFound(new { message = "CDN-Dokument hat keinen Speicherpfad." });
    }

    var latestVersion = GetValue<int>(cdnReader, "latestversion");
    mediaType = GetValue<string?>(cdnReader, "mediatype") ?? mediaType;
    originalFileName = GetValue<string?>(cdnReader, "originalfilename") ?? originalFileName;
    var documentPath = ResolveCdnFilePath(cdnDocumentStore.RootPath, storagePath, latestVersion);

    if (!System.IO.File.Exists(documentPath))
    {
        return Results.NotFound(new { message = $"CDN-Datei wurde nicht gefunden: {documentPath}" });
    }

    var bytes = await System.IO.File.ReadAllBytesAsync(documentPath);
    return Results.File(bytes, mediaType, originalFileName);
})
.WithName("GetChartEntryDocumentByPatientNummer");

app.MapPost("/api/patients/{nummer:long}/documents", async (
    long nummer,
    HttpRequest request,
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Bitte multipart/form-data mit der Datei 'file' senden." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "Bitte eine PDF-Datei im Formularfeld 'file' senden." });
    }

    var title = form["title"].ToString();
    if (string.IsNullOrWhiteSpace(title))
    {
        title = "AI-Konnektor";
    }

    var fileName = form["fileName"].ToString();
    if (string.IsNullOrWhiteSpace(fileName))
    {
        fileName = file.FileName;
    }

    if (string.IsNullOrWhiteSpace(fileName))
    {
        fileName = $"AI-Konnektor-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
    }

    fileName = Path.GetFileName(fileName);
    if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        fileName += ".pdf";
    }

    await using var memory = new MemoryStream();
    await file.CopyToAsync(memory);
    var bytes = memory.ToArray();

    var result = await CreatePatientDocumentAsync(dataSource, cdnDocumentStore, nummer, title, fileName, bytes);
    return Results.Created($"/api/patients/{nummer}/chart-entries/{result.ChartEntryObjectId}/document", result);
})
.DisableAntiforgery()
.WithName("CreatePatientDocument");

app.MapDelete("/api/patients/{nummer:long}/documents/{chartEntryObjectId}", async (
    long nummer,
    string chartEntryObjectId,
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore) =>
{
    var result = await DeletePatientDocumentAsync(dataSource, cdnDocumentStore, nummer, chartEntryObjectId);
    return result.Deleted
        ? Results.Ok(result)
        : Results.NotFound(new { message = "Der DOK-Karteieintrag wurde nicht gefunden oder gehoert nicht zu diesem Patienten." });
})
.WithName("DeletePatientDocument")
.ExcludeFromDescription();

app.MapGet("/api/patients/{nummer:long}/chart-entries/{chartEntryObjectId}/image", async (
    long nummer,
    string chartEntryObjectId,
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    command.CommandText = """
        select
            k.objectid as karteieintrag_objectid,
            k.text,
            k.fachinfoglobalid,
            v.verweis,
            vi.mimetype,
            vi.name
        from aps.patient p
        join aps.karteieintrag k on k.patient_objectid = p.objectid
        join aps.verweiseintrag v on v.objectid = k.fachinformationreference
        left join aps.verweiseintragdateiinfo vi on vi.verweiseintrag_objectid = v.objectid
        where p.nummer = @nummer
          and k.objectid = @chartEntryObjectId
          and k.fachinfoglobalid = 74
        limit 1;
        """;

    command.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, nummer);
    command.Parameters.AddWithValue("chartEntryObjectId", NpgsqlDbType.Varchar, chartEntryObjectId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.NotFound(new { message = "Bild-Karteieintrag wurde nicht gefunden." });
    }

    var imageReference = GetValue<string?>(reader, "verweis");
    if (imageReference is null || !imageReference.StartsWith("cdn://", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { message = "Fuer diesen Karteieintrag wurde kein CDN-Bild gefunden." });
    }

    var mediaType = GetValue<string?>(reader, "mimetype") ?? "image/jpeg";
    if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Die Datei ist kein Bild, sondern '{mediaType}'." });
    }

    var originalFileName = GetValue<string?>(reader, "name") ?? "bild";
    var cdnPath = imageReference["cdn://".Length..];
    var contentPath = $"APS/Praxis/Patient/{cdnPath}";

    await reader.DisposeAsync();

    await using var cdnConnection = await cdnDocumentStore.DataSource.OpenConnectionAsync();
    await using var cdnCommand = cdnConnection.CreateCommand();
    cdnCommand.CommandText = """
        select
            storagepath,
            latestversion,
            coalesce(mediatype, @mediaType) as mediatype,
            coalesce(originalfilename, @originalFileName) as originalfilename
        from cdn.contentitem
        where contentpath = @contentPath
           or contentpath = @cdnPath
           or uuid = @cdnPath
        order by modificationtimestamp desc nulls last
        limit 1;
        """;

    cdnCommand.Parameters.AddWithValue("contentPath", NpgsqlDbType.Varchar, contentPath);
    cdnCommand.Parameters.AddWithValue("cdnPath", NpgsqlDbType.Varchar, cdnPath);
    cdnCommand.Parameters.AddWithValue("mediaType", NpgsqlDbType.Varchar, mediaType);
    cdnCommand.Parameters.AddWithValue("originalFileName", NpgsqlDbType.Varchar, originalFileName);

    await using var cdnReader = await cdnCommand.ExecuteReaderAsync();
    if (!await cdnReader.ReadAsync())
    {
        return Results.NotFound(new { message = "CDN-Bild wurde in t2med_cdn nicht gefunden." });
    }

    var storagePath = GetValue<string?>(cdnReader, "storagepath");
    if (string.IsNullOrWhiteSpace(storagePath))
    {
        return Results.NotFound(new { message = "CDN-Bild hat keinen Speicherpfad." });
    }

    var latestVersion = GetValue<int>(cdnReader, "latestversion");
    mediaType = GetValue<string?>(cdnReader, "mediatype") ?? mediaType;
    originalFileName = GetValue<string?>(cdnReader, "originalfilename") ?? originalFileName;
    var imagePath = ResolveCdnFilePath(cdnDocumentStore.RootPath, storagePath, latestVersion);

    if (!System.IO.File.Exists(imagePath))
    {
        return Results.NotFound(new { message = $"CDN-Bilddatei wurde nicht gefunden: {imagePath}" });
    }

    var bytes = await System.IO.File.ReadAllBytesAsync(imagePath);
    return Results.File(bytes, mediaType, originalFileName);
})
.WithName("GetChartEntryImageByPatientNummer");

app.Run();

static T? GetValue<T>(NpgsqlDataReader reader, string name)
{
    var ordinal = reader.GetOrdinal(name);
    return reader.IsDBNull(ordinal) ? default : reader.GetFieldValue<T>(ordinal);
}

static async Task<HashSet<string>> GetTableColumnsAsync(NpgsqlConnection connection, string schemaName, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        select column_name
        from information_schema.columns
        where table_schema = @schemaName
          and table_name = @tableName;
        """;
    command.Parameters.AddWithValue("schemaName", NpgsqlDbType.Varchar, schemaName);
    command.Parameters.AddWithValue("tableName", NpgsqlDbType.Varchar, tableName);

    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        columns.Add(reader.GetString(0));
    }

    return columns;
}

static string NormalizePzn(string pzn)
{
    return new string(pzn.Where(char.IsDigit).ToArray());
}

static string? NormalizePrescriptionType(string? prescriptionType)
{
    if (string.IsNullOrWhiteSpace(prescriptionType))
    {
        return "kasse";
    }

    return prescriptionType.Trim().ToLowerInvariant() switch
    {
        "kasse" => "kasse",
        "privat" => "privat",
        _ => null
    };
}

static async Task<List<MedicationPriceDto>> FindT2medMedicationPricesAsync(NpgsqlDataSource dataSource, string normalizedPzn)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        select
            pzn::text as pzn,
            coalesce(name, handelsname, originalname, freitext)::text as name,
            coalesce(preisineuro, einzelpreis)::text as preis,
            case when preisineuro is not null then 'preisineuro' else 'einzelpreis' end as preisspalte
        from aps.verordnung
        where regexp_replace(coalesce(pzn::text, ''), '\D', '', 'g') = @pzn
          and coalesce(preisineuro, einzelpreis) is not null
        order by verordnungszeitpunkt desc
        limit 20;
        """;
    command.Parameters.AddWithValue("pzn", NpgsqlDbType.Varchar, normalizedPzn);

    var result = new List<MedicationPriceDto>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        result.Add(new MedicationPriceDto
        {
            Pzn = GetValue<string?>(reader, "pzn"),
            Name = GetValue<string?>(reader, "name"),
            Preis = GetValue<string?>(reader, "preis") ?? "",
            Schema = "aps",
            Tabelle = "verordnung",
            PznSpalte = "pzn",
            PreisSpalte = GetValue<string?>(reader, "preisspalte") ?? ""
        });
    }

    return result;
}

static async Task<List<PrescriptionStatisticsEntryDto>> GetPrescriptionStatisticsAsync(
    NpgsqlDataSource dataSource,
    DateOnly from,
    DateOnly to,
    string prescriptionType)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        with prescription_items as (
            select
                nullif(coalesce(v.name, v.handelsname, v.originalname, v.freitext), '') as medikament,
                nullif(v.wirkstoff, '') as wirkstoff,
                coalesce(v.preisineuro, v.einzelpreis) as einzelpreis
            from aps.rezept r
            join aps.verordnung v on v.rezept_objectid = r.objectid
            where r.ausstellungszeitpunkt::date between @from and @to
              and nullif(coalesce(v.name, v.handelsname, v.originalname, v.freitext), '') is not null
              and (
                    (@rezeptTyp = 'kasse' and v.classid in (54, 258, 413))
                    or (@rezeptTyp = 'privat' and v.classid in (64, 65, 70, 71))
              )
        )
        select
            medikament,
            coalesce(wirkstoff, '') as wirkstoff,
            count(*)::integer as menge,
            einzelpreis,
            (count(*) * coalesce(einzelpreis, 0))::numeric as gesamtkosten
        from prescription_items
        group by medikament, wirkstoff, einzelpreis
        order by (count(*) * coalesce(einzelpreis, 0)) desc, count(*) desc, medikament;
        """;
    command.Parameters.AddWithValue("from", NpgsqlDbType.Date, from);
    command.Parameters.AddWithValue("to", NpgsqlDbType.Date, to);
    command.Parameters.AddWithValue("rezeptTyp", NpgsqlDbType.Varchar, prescriptionType);

    var result = new List<PrescriptionStatisticsEntryDto>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        result.Add(new PrescriptionStatisticsEntryDto
        {
            Medikament = GetValue<string?>(reader, "medikament") ?? "",
            Wirkstoff = GetValue<string?>(reader, "wirkstoff") ?? "",
            Menge = GetValue<int>(reader, "menge"),
            Einzelpreis = GetValue<decimal?>(reader, "einzelpreis"),
            Gesamtkosten = GetValue<decimal>(reader, "gesamtkosten")
        });
    }

    return result;
}

static async Task<List<PrescriptionStatisticsPatientDto>> GetPrescriptionStatisticsPatientsAsync(
    NpgsqlDataSource dataSource,
    DateOnly from,
    DateOnly to,
    string prescriptionType,
    string medication,
    string wirkstoff,
    decimal? einzelpreis)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        with prescription_items as (
            select
                p.nummer,
                p.namensdaten_nachname as nachname,
                p.namensdaten_vorname as vorname,
                p.geburtsdaten_datum_normalizeddate as geburtsdatum,
                nullif(coalesce(v.name, v.handelsname, v.originalname, v.freitext), '') as medikament,
                coalesce(nullif(v.wirkstoff, ''), '') as wirkstoff,
                coalesce(v.preisineuro, v.einzelpreis) as einzelpreis
            from aps.rezept r
            join aps.verordnung v on v.rezept_objectid = r.objectid
            join aps.patient p on p.objectid = r.patient_objectid
            where r.ausstellungszeitpunkt::date between @from and @to
              and nullif(coalesce(v.name, v.handelsname, v.originalname, v.freitext), '') = @medication
              and coalesce(nullif(v.wirkstoff, ''), '') = @wirkstoff
              and (
                    (@einzelpreis is null and coalesce(v.preisineuro, v.einzelpreis) is null)
                    or coalesce(v.preisineuro, v.einzelpreis) = @einzelpreis
              )
              and (
                    (@rezeptTyp = 'kasse' and v.classid in (54, 258, 413))
                    or (@rezeptTyp = 'privat' and v.classid in (64, 65, 70, 71))
              )
        )
        select
            nummer,
            nachname,
            vorname,
            geburtsdatum,
            count(*)::integer as menge,
            (count(*) * coalesce(max(einzelpreis), 0))::numeric as gesamtkosten
        from prescription_items
        group by nummer, nachname, vorname, geburtsdatum
        order by gesamtkosten desc, nachname, vorname;
        """;
    command.Parameters.AddWithValue("from", NpgsqlDbType.Date, from);
    command.Parameters.AddWithValue("to", NpgsqlDbType.Date, to);
    command.Parameters.AddWithValue("rezeptTyp", NpgsqlDbType.Varchar, prescriptionType);
    command.Parameters.AddWithValue("medication", NpgsqlDbType.Varchar, medication);
    command.Parameters.AddWithValue("wirkstoff", NpgsqlDbType.Varchar, wirkstoff);
    command.Parameters.Add("einzelpreis", NpgsqlDbType.Numeric).Value = einzelpreis is null ? DBNull.Value : einzelpreis.Value;

    var result = new List<PrescriptionStatisticsPatientDto>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        result.Add(new PrescriptionStatisticsPatientDto
        {
            PatientNummer = GetValue<long>(reader, "nummer"),
            Nachname = GetValue<string?>(reader, "nachname") ?? "",
            Vorname = GetValue<string?>(reader, "vorname") ?? "",
            Geburtsdatum = GetValue<DateOnly?>(reader, "geburtsdatum"),
            Menge = GetValue<int>(reader, "menge"),
            Gesamtkosten = GetValue<decimal>(reader, "gesamtkosten")
        });
    }

    return result;
}

static async Task<List<DailyLabEntryDto>> GetDailyLabEntriesAsync(NpgsqlDataSource dataSource, DateOnly from, DateOnly to)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        select
            p.nummer as patient_nummer,
            p.namensdaten_nachname as patient_nachname,
            p.namensdaten_vorname as patient_vorname,
            p.geburtsdaten_datum_normalizeddate as patient_geburtsdatum,
            k.objectid as karteieintrag_objectid,
            k.informationszeitpunkt,
            k.text,
            coalesce(lv.laborwerte_json, '[]'::jsonb)::text as laborwerte_json
        from aps.karteieintrag k
        join aps.patient p on p.objectid = k.patient_objectid
        join aps.laborbefund lb on lb.objectid = k.fachinformationreference
        left join lateral (
            select jsonb_agg(
                jsonb_build_object(
                    'kurzbezeichnung', lt.kurzbezeichnung,
                    'langbezeichnung', coalesce(nullif(lt.langbezeichnung, ''), lt.kurzbezeichnung),
                    'ergebniswert', lw.ergebniswert,
                    'ergebnistext', lw.ergebnistext,
                    'masseinheit', coalesce(lw.masseinheit, lt.masseinheit),
                    'normwertbereichText', coalesce(lw.normwertbereich_text, lt.standardnormwertbereich_text),
                    'grenzwertindikator', lw.grenzwertindikator
                )
                order by lw.messwerte_order nulls last, lt.kurzbezeichnung
            ) as laborwerte_json
            from aps.laborwert lw
            join aps.labortest lt on lt.objectid = lw.labortest_objectid
            where lw.laborbefund_objectid = lb.objectid
        ) lv on true
        where k.informationszeitpunkt::date between @from and @to
        order by k.informationszeitpunkt, p.namensdaten_nachname, p.namensdaten_vorname;
        """;
    command.Parameters.AddWithValue("from", NpgsqlDbType.Date, from);
    command.Parameters.AddWithValue("to", NpgsqlDbType.Date, to);

    var result = new List<DailyLabEntryDto>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        result.Add(new DailyLabEntryDto
        {
            PatientNummer = GetValue<long>(reader, "patient_nummer"),
            PatientNachname = GetValue<string?>(reader, "patient_nachname"),
            PatientVorname = GetValue<string?>(reader, "patient_vorname"),
            PatientGeburtsdatum = GetValue<DateOnly?>(reader, "patient_geburtsdatum"),
            ObjectId = GetValue<string?>(reader, "karteieintrag_objectid"),
            Informationszeitpunkt = GetValue<DateTimeOffset>(reader, "informationszeitpunkt"),
            Text = GetValue<string?>(reader, "text"),
            Laborwerte = JsonSerializer.Deserialize<List<LaborValueDto>>(GetValue<string?>(reader, "laborwerte_json") ?? "[]", JsonSerializerOptions.Web) ?? []
        });
    }

    return result;
}

static async Task<CreatePatientDocumentResponse> CreatePatientDocumentAsync(
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore,
    long patientNumber,
    string title,
    string fileName,
    byte[] bytes)
{
    var now = DateTimeOffset.UtcNow;
    var cdnUuid = Guid.NewGuid().ToString();
    var cdnKey = Guid.NewGuid().ToString("N");
    var storagePath = $"{cdnUuid[..2]}/{cdnUuid.Substring(2, 2)}/{cdnUuid}";
    var filePath = ResolveCdnFilePathForWrite(cdnDocumentStore.RootPath, storagePath, 0);

    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    await File.WriteAllBytesAsync(filePath, bytes);

    await using var cdnConnection = await cdnDocumentStore.DataSource.OpenConnectionAsync();
    await using var cdnTransaction = await cdnConnection.BeginTransactionAsync();
    try
    {
        long backupRevision;
        string modificationUserId;
        await using (var metadataCommand = cdnConnection.CreateCommand())
        {
            metadataCommand.Transaction = cdnTransaction;
            metadataCommand.CommandText = """
                select
                    coalesce(max(backuprevision), 0) + 1 as next_backuprevision,
                    coalesce(
                        (
                            select modificationuserid
                            from cdn.contentitem
                            where modificationuserid is not null
                              and length(modificationuserid) = 36
                            order by modificationtimestamp desc nulls last, backuprevision desc nulls last
                            limit 1
                        ),
                        ''
                    ) as modificationuserid
                from cdn.contentitem;
                """;

            await using var reader = await metadataCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("CDN-Metadaten konnten nicht ermittelt werden.");
            }

            backupRevision = GetValue<long>(reader, "next_backuprevision");
            modificationUserId = GetValue<string>(reader, "modificationuserid") ?? "";
        }

        await using (var cdnCommand = cdnConnection.CreateCommand())
        {
            cdnCommand.Transaction = cdnTransaction;
            cdnCommand.CommandText = """
                insert into cdn.contentitem (
                    uuid,
                    contentpath,
                    latestversion,
                    mediatype,
                    modificationtimestamp,
                    modificationuserid,
                    originalfilename,
                    storagepath,
                    storagetype,
                    backuprevision,
                    writelock
                )
                values (
                    @uuid,
                    @contentpath,
                    0,
                    'application/pdf',
                    @modificationtimestamp,
                    @modificationuserid,
                    @originalfilename,
                    @storagepath,
                    0,
                    @backuprevision,
                    false
                );
                """;
            cdnCommand.Parameters.AddWithValue("uuid", NpgsqlDbType.Varchar, cdnUuid);
            cdnCommand.Parameters.AddWithValue("contentpath", NpgsqlDbType.Varchar, $"APS/Praxis/Patient/{cdnKey}");
            cdnCommand.Parameters.AddWithValue("modificationtimestamp", NpgsqlDbType.TimestampTz, now);
            cdnCommand.Parameters.AddWithValue("modificationuserid", NpgsqlDbType.Varchar, modificationUserId);
            cdnCommand.Parameters.AddWithValue("originalfilename", NpgsqlDbType.Varchar, fileName);
            cdnCommand.Parameters.AddWithValue("storagepath", NpgsqlDbType.Varchar, storagePath);
            cdnCommand.Parameters.AddWithValue("backuprevision", NpgsqlDbType.Bigint, backupRevision);
            await cdnCommand.ExecuteNonQueryAsync();
        }

        await cdnTransaction.CommitAsync();
    }
    catch
    {
        await cdnTransaction.RollbackAsync();
        TryDeleteFile(filePath);
        throw;
    }

    var verweisObjectId = CreateT2medObjectId("003c");
    var dateiInfoObjectId = CreateT2medObjectId("024c");
    var karteiObjectId = CreateT2medObjectId("0023");
    var documentText = NormalizeDocumentTitle(title, fileName);

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    try
    {
        string patientObjectId;
        string? roleObjectId;
        string? locationObjectId;
        int fallart;
        int informationsquelle;
        long orderNumber;
        await using (var patientCommand = connection.CreateCommand())
        {
            patientCommand.Transaction = transaction;
            patientCommand.CommandText = """
                with patient_by_number as (
                    select objectid
                    from aps.patient
                    where nummer = @nummer
                    limit 1
                ),
                current_context as (
                    select
                        k.arztrolle_objectid,
                        k.behandlungsort_objectid,
                        k.fallart,
                        k.informationsquelle
                    from aps.karteieintrag k
                    join patient_by_number p on p.objectid = k.patient_objectid
                    order by
                        k.creationtimestamp desc nulls last,
                        k.informationszeitpunkt desc nulls last,
                        k.ordnungszaehler desc nulls last
                    limit 1
                )
                select
                    p.objectid as patient_objectid,
                    ctx.arztrolle_objectid,
                    ctx.behandlungsort_objectid,
                    coalesce(ctx.fallart, 8) as fallart,
                    coalesce(ctx.informationsquelle, 1) as informationsquelle,
                    coalesce(max(k.ordnungszaehler), 0) + 1 as ordnungszaehler
                from patient_by_number p
                left join current_context ctx on true
                left join aps.karteieintrag k on k.patient_objectid = p.objectid
                group by p.objectid, ctx.arztrolle_objectid, ctx.behandlungsort_objectid, ctx.fallart, ctx.informationsquelle;
                """;
            patientCommand.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, patientNumber);

            await using var reader = await patientCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException($"Patient mit Nummer {patientNumber} wurde nicht gefunden.");
            }

            patientObjectId = GetValue<string>(reader, "patient_objectid")!;
            roleObjectId = GetValue<string?>(reader, "arztrolle_objectid");
            locationObjectId = GetValue<string?>(reader, "behandlungsort_objectid");
            fallart = GetValue<int>(reader, "fallart");
            informationsquelle = GetValue<int>(reader, "informationsquelle");
            orderNumber = GetValue<long>(reader, "ordnungszaehler");
        }

        await using (var verweisCommand = connection.CreateCommand())
        {
            verweisCommand.Transaction = transaction;
            verweisCommand.CommandText = """
                insert into aps.verweiseintrag (
                    objectid,
                    revision,
                    creationtimestamp,
                    anamnestisch,
                    fachinfoglobalid,
                    gueltigkeitszeitpunkt,
                    text,
                    verweis,
                    behandlungsfall_objectid,
                    arztrolle_objectid,
                    behandlungsort_objectid,
                    patient_objectid,
                    classid,
                    kuerzel,
                    unzugeordnet
                )
                values (
                    @objectid,
                    0,
                    @now,
                    false,
                    75,
                    @now,
                    @documentText,
                    @verweis,
                    null,
                    @arztrolleObjectId,
                    @behandlungsortObjectId,
                    @patientObjectId,
                    60,
                    '',
                    false
                );
                """;
            verweisCommand.Parameters.AddWithValue("objectid", NpgsqlDbType.Varchar, verweisObjectId);
            verweisCommand.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            verweisCommand.Parameters.AddWithValue("documentText", NpgsqlDbType.Varchar, documentText);
            verweisCommand.Parameters.AddWithValue("verweis", NpgsqlDbType.Varchar, $"cdn://{cdnKey}");
            verweisCommand.Parameters.Add("arztrolleObjectId", NpgsqlDbType.Varchar).Value = roleObjectId is null ? DBNull.Value : roleObjectId;
            verweisCommand.Parameters.Add("behandlungsortObjectId", NpgsqlDbType.Varchar).Value = locationObjectId is null ? DBNull.Value : locationObjectId;
            verweisCommand.Parameters.AddWithValue("patientObjectId", NpgsqlDbType.Varchar, patientObjectId);
            await verweisCommand.ExecuteNonQueryAsync();
        }

        await using (var fileInfoCommand = connection.CreateCommand())
        {
            fileInfoCommand.Transaction = transaction;
            fileInfoCommand.CommandText = """
                insert into aps.verweiseintragdateiinfo (
                    objectid,
                    creationtimestamp,
                    revision,
                    groesse,
                    mimetype,
                    name,
                    verweiseintrag_objectid
                )
                values (
                    @objectid,
                    @now,
                    0,
                    @size,
                    'application/pdf',
                    @name,
                    @verweiseintragObjectId
                );
                """;
            fileInfoCommand.Parameters.AddWithValue("objectid", NpgsqlDbType.Varchar, dateiInfoObjectId);
            fileInfoCommand.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            fileInfoCommand.Parameters.AddWithValue("size", NpgsqlDbType.Bigint, (long)bytes.Length);
            fileInfoCommand.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, fileName);
            fileInfoCommand.Parameters.AddWithValue("verweiseintragObjectId", NpgsqlDbType.Varchar, verweisObjectId);
            await fileInfoCommand.ExecuteNonQueryAsync();
        }

        await using (var chartCommand = connection.CreateCommand())
        {
            chartCommand.Transaction = transaction;
            chartCommand.CommandText = """
                insert into aps.karteieintrag (
                    objectid,
                    revision,
                    anamnestisch,
                    fachinfoglobalid,
                    fachinfoqualifiedid,
                    fachinformationreference,
                    informationszeitpunkt,
                    text,
                    kuerzel,
                    notinstandardansicht,
                    ordnungszaehler,
                    titel,
                    behandlungsfall_objectid,
                    arztrolle_objectid,
                    behandlungsort_objectid,
                    fallart,
                    informationsquelle,
                    patient_objectid,
                    creationtimestamp,
                    cavehinweis,
                    sichtbarfuer,
                    symbol,
                    freigabe,
                    notnulldummyfield
                )
                values (
                    @objectid,
                    0,
                    false,
                    75,
                    '2.75.',
                    @verweiseintragObjectId,
                    @now,
                    @documentText,
                    '',
                    false,
                    @ordnungszaehler,
                    '',
                    null,
                    @arztrolleObjectId,
                    @behandlungsortObjectId,
                    @fallart,
                    @informationsquelle,
                    @patientObjectId,
                    @now,
                    false,
                    1,
                    '',
                    1,
                    1
                );
                """;
            chartCommand.Parameters.AddWithValue("objectid", NpgsqlDbType.Varchar, karteiObjectId);
            chartCommand.Parameters.AddWithValue("verweiseintragObjectId", NpgsqlDbType.Varchar, verweisObjectId);
            chartCommand.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            chartCommand.Parameters.AddWithValue("documentText", NpgsqlDbType.Varchar, documentText);
            chartCommand.Parameters.AddWithValue("ordnungszaehler", NpgsqlDbType.Bigint, orderNumber);
            chartCommand.Parameters.Add("arztrolleObjectId", NpgsqlDbType.Varchar).Value = roleObjectId is null ? DBNull.Value : roleObjectId;
            chartCommand.Parameters.Add("behandlungsortObjectId", NpgsqlDbType.Varchar).Value = locationObjectId is null ? DBNull.Value : locationObjectId;
            chartCommand.Parameters.AddWithValue("fallart", NpgsqlDbType.Integer, fallart);
            chartCommand.Parameters.AddWithValue("informationsquelle", NpgsqlDbType.Integer, informationsquelle);
            chartCommand.Parameters.AddWithValue("patientObjectId", NpgsqlDbType.Varchar, patientObjectId);
            await chartCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        await DeleteCdnContentItemAsync(cdnDocumentStore.DataSource, cdnUuid);
        TryDeleteFile(filePath);
        throw;
    }

    return new CreatePatientDocumentResponse
    {
        ChartEntryObjectId = karteiObjectId,
        DocumentObjectId = verweisObjectId,
        CdnKey = cdnKey,
        FileName = fileName
    };
}

static async Task<DeletePatientDocumentResponse> DeletePatientDocumentAsync(
    NpgsqlDataSource dataSource,
    CdnDocumentStore cdnDocumentStore,
    long patientNumber,
    string chartEntryObjectId)
{
    string? documentObjectId;
    string? cdnReference;
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    await using (var lookupCommand = connection.CreateCommand())
    {
        lookupCommand.Transaction = transaction;
        lookupCommand.CommandText = """
            select
                k.fachinformationreference as dokument_objectid,
                v.verweis
            from aps.patient p
            join aps.karteieintrag k on k.patient_objectid = p.objectid
            join aps.verweiseintrag v on v.objectid = k.fachinformationreference
            where p.nummer = @nummer
              and k.objectid = @chartEntryObjectId
              and k.fachinfoglobalid = 75
              and v.fachinfoglobalid = 75
            limit 1;
            """;
        lookupCommand.Parameters.AddWithValue("nummer", NpgsqlDbType.Bigint, patientNumber);
        lookupCommand.Parameters.AddWithValue("chartEntryObjectId", NpgsqlDbType.Varchar, chartEntryObjectId);

        await using var reader = await lookupCommand.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await transaction.RollbackAsync();
            return new DeletePatientDocumentResponse { Deleted = false };
        }

        documentObjectId = GetValue<string?>(reader, "dokument_objectid");
        cdnReference = GetValue<string?>(reader, "verweis");
    }

    if (string.IsNullOrWhiteSpace(documentObjectId))
    {
        await transaction.RollbackAsync();
        return new DeletePatientDocumentResponse { Deleted = false };
    }

    await using (var fileInfoCommand = connection.CreateCommand())
    {
        fileInfoCommand.Transaction = transaction;
        fileInfoCommand.CommandText = "delete from aps.verweiseintragdateiinfo where verweiseintrag_objectid = @documentObjectId;";
        fileInfoCommand.Parameters.AddWithValue("documentObjectId", NpgsqlDbType.Varchar, documentObjectId);
        await fileInfoCommand.ExecuteNonQueryAsync();
    }

    await using (var chartCommand = connection.CreateCommand())
    {
        chartCommand.Transaction = transaction;
        chartCommand.CommandText = """
            delete from aps.karteieintrag
            where objectid = @chartEntryObjectId
              and fachinformationreference = @documentObjectId
              and fachinfoglobalid = 75;
            """;
        chartCommand.Parameters.AddWithValue("chartEntryObjectId", NpgsqlDbType.Varchar, chartEntryObjectId);
        chartCommand.Parameters.AddWithValue("documentObjectId", NpgsqlDbType.Varchar, documentObjectId);
        var deletedChartEntries = await chartCommand.ExecuteNonQueryAsync();
        if (deletedChartEntries != 1)
        {
            await transaction.RollbackAsync();
            return new DeletePatientDocumentResponse { Deleted = false };
        }
    }

    await using (var documentCommand = connection.CreateCommand())
    {
        documentCommand.Transaction = transaction;
        documentCommand.CommandText = "delete from aps.verweiseintrag where objectid = @documentObjectId and fachinfoglobalid = 75;";
        documentCommand.Parameters.AddWithValue("documentObjectId", NpgsqlDbType.Varchar, documentObjectId);
        await documentCommand.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();

    var cdnKey = cdnReference?.StartsWith("cdn://", StringComparison.OrdinalIgnoreCase) == true
        ? cdnReference["cdn://".Length..]
        : null;
    var deletedCdn = false;
    if (!string.IsNullOrWhiteSpace(cdnKey))
    {
        deletedCdn = await DeleteCdnDocumentAsync(cdnDocumentStore, cdnKey);
    }

    return new DeletePatientDocumentResponse
    {
        Deleted = true,
        ChartEntryObjectId = chartEntryObjectId,
        DocumentObjectId = documentObjectId,
        CdnKey = cdnKey ?? "",
        CdnDeleted = deletedCdn
    };
}

static async Task<bool> DeleteCdnDocumentAsync(CdnDocumentStore cdnDocumentStore, string cdnKey)
{
    string? storagePath = null;
    var latestVersion = 0;

    await using var cdnConnection = await cdnDocumentStore.DataSource.OpenConnectionAsync();
    await using (var lookupCommand = cdnConnection.CreateCommand())
    {
        lookupCommand.CommandText = """
            select storagepath, latestversion
            from cdn.contentitem
            where contentpath = @contentPath
               or contentpath = @cdnKey
               or uuid = @cdnKey
            limit 1;
            """;
        lookupCommand.Parameters.AddWithValue("contentPath", NpgsqlDbType.Varchar, $"APS/Praxis/Patient/{cdnKey}");
        lookupCommand.Parameters.AddWithValue("cdnKey", NpgsqlDbType.Varchar, cdnKey);

        await using var reader = await lookupCommand.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            storagePath = GetValue<string?>(reader, "storagepath");
            latestVersion = GetValue<int>(reader, "latestversion");
        }
    }

    await using (var deleteCommand = cdnConnection.CreateCommand())
    {
        deleteCommand.CommandText = """
            delete from cdn.contentitem
            where contentpath = @contentPath
               or contentpath = @cdnKey
               or uuid = @cdnKey;
            """;
        deleteCommand.Parameters.AddWithValue("contentPath", NpgsqlDbType.Varchar, $"APS/Praxis/Patient/{cdnKey}");
        deleteCommand.Parameters.AddWithValue("cdnKey", NpgsqlDbType.Varchar, cdnKey);
        await deleteCommand.ExecuteNonQueryAsync();
    }

    if (string.IsNullOrWhiteSpace(storagePath))
    {
        return false;
    }

    TryDeleteFile(ResolveCdnFilePath(cdnDocumentStore.RootPath, storagePath, latestVersion));
    return true;
}

static string ResolveCdnFilePath(string cdnRoot, string storagePath, int latestVersion)
{
    var root = Path.GetFullPath(cdnRoot);
    var relativeStoragePath = storagePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    var fullPath = Path.GetFullPath(Path.Combine(root, relativeStoragePath, latestVersion.ToString()));

    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("CDN storage path points outside the configured CDN root.");
    }

    return fullPath;
}

static string CreateT2medObjectId(string prefix)
{
    if (prefix.Length != 4)
    {
        throw new ArgumentException("T2med object ID prefix must have four characters.", nameof(prefix));
    }

    return prefix + Guid.NewGuid().ToString("N");
}

static string NormalizeDocumentTitle(string title, string fileName)
{
    if (!string.IsNullOrWhiteSpace(title))
    {
        return title.Trim();
    }

    var baseName = Path.GetFileNameWithoutExtension(fileName);
    if (string.IsNullOrWhiteSpace(baseName))
    {
        return "AI-Konnektor Antwort";
    }

    baseName = Regex.Replace(baseName, @"-\d{8}-\d{6}$", "", RegexOptions.CultureInvariant);
    return string.Join("-", baseName
        .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(part.ToLower(CultureInfo.CurrentCulture))));
}

static string ResolveCdnFilePathForWrite(string cdnRoot, string storagePath, int latestVersion)
{
    var root = Path.GetFullPath(cdnRoot);
    var relativeStoragePath = storagePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    var fullPath = Path.GetFullPath(Path.Combine(root, relativeStoragePath, latestVersion.ToString()));

    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("CDN storage path points outside the configured CDN root.");
    }

    return fullPath;
}

static async Task DeleteCdnContentItemAsync(NpgsqlDataSource cdnDataSource, string cdnUuid)
{
    try
    {
        await using var cdnConnection = await cdnDataSource.OpenConnectionAsync();
        await using var command = cdnConnection.CreateCommand();
        command.CommandText = "delete from cdn.contentitem where uuid = @uuid;";
        command.Parameters.AddWithValue("uuid", NpgsqlDbType.Varchar, cdnUuid);
        await command.ExecuteNonQueryAsync();
    }
    catch
    {
        // Cleanup must not hide the original database error.
    }
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
        // Cleanup must not hide the original database error.
    }
}

sealed class CurrentCaseDiagnosesResponse
{
    public long PatientNummer { get; set; }
    public string? PatientNachname { get; set; }
    public string? PatientVorname { get; set; }
    public DateOnly? PatientGeburtsdatum { get; set; }
    public CurrentCaseDto? Behandlungsfall { get; set; }
    public List<DiagnosisDto> Diagnosen { get; } = [];
}

sealed class CurrentCaseDto
{
    public string? ObjectId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset? CreationTimestamp { get; set; }
    public DateTimeOffset? ModificationTimestamp { get; set; }
    public DateOnly? Beginn { get; set; }
    public DateOnly? Ende { get; set; }
    public int ClassId { get; set; }
    public int? AbrechnungQuartal { get; set; }
}

sealed class DiagnosisDto
{
    public string? ObjectId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset? CreationTimestamp { get; set; }
    public DateTimeOffset Beginn { get; set; }
    public DateTimeOffset? Ende { get; set; }
    public string? Icd { get; set; }
    public string? Klartext { get; set; }
    public string? Erlaeuterung { get; set; }
    public int Lokalisation { get; set; }
    public int Relevanz { get; set; }
    public int Sicherheit { get; set; }
    public int FachinfoGlobalId { get; set; }
    public string? InterneBemerkung { get; set; }
}

sealed class CurrentCaseMedicationPlanResponse
{
    public long PatientNummer { get; set; }
    public string? PatientNachname { get; set; }
    public string? PatientVorname { get; set; }
    public DateOnly? PatientGeburtsdatum { get; set; }
    public CurrentCaseDto? Behandlungsfall { get; set; }
    public List<MedicationDto> Medikamente { get; } = [];
}

sealed class MedicationDto
{
    public string? ObjectId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset? CreationTimestamp { get; set; }
    public DateTimeOffset Verordnungszeitpunkt { get; set; }
    public string? Name { get; set; }
    public string? Handelsname { get; set; }
    public string? Wirkstoff { get; set; }
    public double? WirkstaerkeWert { get; set; }
    public string? WirkstaerkeEinheit { get; set; }
    public string? DarreichungsformFreitext { get; set; }
    public string? DarreichungsformIfaCode { get; set; }
    public string? DosierschemaMorgens { get; set; }
    public string? DosierschemaMittags { get; set; }
    public string? DosierschemaAbends { get; set; }
    public string? DosierschemaNachts { get; set; }
    public string? DosierschemaFreitext { get; set; }
    public string? Pzn { get; set; }
    public string? Groesse { get; set; }
    public string? Hinweis { get; set; }
    public string? Hinweise { get; set; }
    public string? Freitext { get; set; }
    public int? ArzneimittelverordnungTyp { get; set; }
}

sealed class CurrentCaseChartEntriesResponse
{
    public long PatientNummer { get; set; }
    public string? PatientNachname { get; set; }
    public string? PatientVorname { get; set; }
    public DateOnly? PatientGeburtsdatum { get; set; }
    public CurrentCaseDto? Behandlungsfall { get; set; }
    public List<ChartEntryDto> Karteieintraege { get; } = [];
}

sealed class CreatePatientDocumentResponse
{
    public string ChartEntryObjectId { get; set; } = "";
    public string DocumentObjectId { get; set; } = "";
    public string CdnKey { get; set; } = "";
    public string FileName { get; set; } = "";
}

sealed class DeletePatientDocumentResponse
{
    public bool Deleted { get; set; }
    public string ChartEntryObjectId { get; set; } = "";
    public string DocumentObjectId { get; set; } = "";
    public string CdnKey { get; set; } = "";
    public bool CdnDeleted { get; set; }
}

sealed class PatientSearchResponse
{
    public List<PatientSearchResultDto> Patienten { get; set; } = [];
}

sealed class PatientSearchResultDto
{
    public long Nummer { get; set; }
    public string? Nachname { get; set; }
    public string? Vorname { get; set; }
    public DateOnly? Geburtsdatum { get; set; }
}

sealed class ChartEntryDto
{
    public string? ObjectId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset? CreationTimestamp { get; set; }
    public DateTimeOffset Informationszeitpunkt { get; set; }
    public string? Kategorie { get; set; }
    public string? Kuerzel { get; set; }
    public string? Titel { get; set; }
    public string? Text { get; set; }
    public string? Symbol { get; set; }
    public bool Anamnestisch { get; set; }
    public bool Cavehinweis { get; set; }
    public int FachinfoGlobalId { get; set; }
    public string? FachinfoQualifiedId { get; set; }
    public long Ordnungszaehler { get; set; }
    public List<LaborValueDto> Laborwerte { get; set; } = [];
}

sealed class LaborValueDto
{
    public string? Kurzbezeichnung { get; set; }
    public string? Langbezeichnung { get; set; }
    public string? Ergebniswert { get; set; }
    public string? Ergebnistext { get; set; }
    public string? Masseinheit { get; set; }
    public string? NormwertbereichText { get; set; }
    public int? Grenzwertindikator { get; set; }
}

sealed class MedicationPriceResponse
{
    public string Pzn { get; set; } = "";
    public List<MedicationPriceDto> Preise { get; set; } = [];
}

sealed class MedicationPriceDto
{
    public string? Pzn { get; set; }
    public string? Name { get; set; }
    public string Preis { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Tabelle { get; set; } = "";
    public string PznSpalte { get; set; } = "";
    public string PreisSpalte { get; set; } = "";
}

sealed class PrescriptionStatisticsResponse
{
    public DateOnly Von { get; set; }
    public DateOnly Bis { get; set; }
    public string RezeptTyp { get; set; } = "kasse";
    public List<PrescriptionStatisticsEntryDto> Eintraege { get; set; } = [];
}

sealed class PrescriptionStatisticsEntryDto
{
    public string Medikament { get; set; } = "";
    public string Wirkstoff { get; set; } = "";
    public int Menge { get; set; }
    public decimal? Einzelpreis { get; set; }
    public decimal Gesamtkosten { get; set; }
}

sealed class PrescriptionStatisticsPatientsResponse
{
    public DateOnly Von { get; set; }
    public DateOnly Bis { get; set; }
    public string RezeptTyp { get; set; } = "kasse";
    public string Medikament { get; set; } = "";
    public string Wirkstoff { get; set; } = "";
    public decimal? Einzelpreis { get; set; }
    public List<PrescriptionStatisticsPatientDto> Patienten { get; set; } = [];
}

sealed class PrescriptionStatisticsPatientDto
{
    public long PatientNummer { get; set; }
    public string Nachname { get; set; } = "";
    public string Vorname { get; set; } = "";
    public DateOnly? Geburtsdatum { get; set; }
    public int Menge { get; set; }
    public decimal Gesamtkosten { get; set; }
}

sealed class DailyLabResponse
{
    public DateOnly Von { get; set; }
    public DateOnly Bis { get; set; }
    public List<DailyLabEntryDto> Eintraege { get; set; } = [];
}

sealed class DailyLabEntryDto
{
    public long PatientNummer { get; set; }
    public string? PatientNachname { get; set; }
    public string? PatientVorname { get; set; }
    public DateOnly? PatientGeburtsdatum { get; set; }
    public string? ObjectId { get; set; }
    public DateTimeOffset Informationszeitpunkt { get; set; }
    public string? Text { get; set; }
    public List<LaborValueDto> Laborwerte { get; set; } = [];
}

sealed class MmiMedicationPriceStore
{
    private static readonly string[] PznColumnTerms = ["pzn", "pharmazentralnummer"];
    private static readonly string[] PriceColumnTerms = ["preis", "avp", "taxe", "festbetrag"];
    private static readonly string[] NameColumnTerms = ["name", "bezeichnung", "handelsname", "arzneimittel"];
    private IReadOnlyList<MmiPriceCandidateTable>? cachedCandidateTables;

    public MmiMedicationPriceStore(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public async Task<List<MedicationPriceDto>> FindByPznAsync(string normalizedPzn)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        var candidates = await GetCandidateTablesAsync(connection);
        var result = new List<MedicationPriceDto>();

        foreach (var candidate in candidates)
        {
            foreach (var priceColumn in candidate.PriceColumns)
            {
                if (result.Count >= 50)
                {
                    return result;
                }

                await AddMatchesAsync(connection, candidate, priceColumn, normalizedPzn, result);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<MmiPriceCandidateTable>> GetCandidateTablesAsync(NpgsqlConnection connection)
    {
        if (cachedCandidateTables is not null)
        {
            return cachedCandidateTables;
        }

        var columnsByTable = new Dictionary<(string Schema, string Table), List<MmiColumn>>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                table_schema,
                table_name,
                column_name,
                data_type
            from information_schema.columns
            where table_schema not in ('pg_catalog', 'information_schema')
            order by table_schema, table_name, ordinal_position;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var column = new MmiColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3));
            var key = (column.Schema, column.Table);
            if (!columnsByTable.TryGetValue(key, out var columns))
            {
                columns = [];
                columnsByTable[key] = columns;
            }

            columns.Add(column);
        }

        var candidates = new List<MmiPriceCandidateTable>();
        foreach (var ((schema, table), columns) in columnsByTable)
        {
            var pznColumns = columns
                .Where(column => ContainsAny(column.Name, PznColumnTerms))
                .ToArray();
            var priceColumns = columns
                .Where(column => ContainsAny(column.Name, PriceColumnTerms))
                .ToArray();

            if (pznColumns.Length == 0 || priceColumns.Length == 0)
            {
                continue;
            }

            var nameColumn = columns.FirstOrDefault(column => ContainsAny(column.Name, NameColumnTerms));
            candidates.Add(new MmiPriceCandidateTable(
                schema,
                table,
                pznColumns[0].Name,
                priceColumns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                nameColumn?.Name));
        }

        cachedCandidateTables = candidates;
        return candidates;
    }

    private static async Task AddMatchesAsync(
        NpgsqlConnection connection,
        MmiPriceCandidateTable candidate,
        string priceColumn,
        string normalizedPzn,
        List<MedicationPriceDto> result)
    {
        var qualifiedTable = $"{QuoteIdentifier(candidate.Schema)}.{QuoteIdentifier(candidate.Table)}";
        var pznColumn = QuoteIdentifier(candidate.PznColumn);
        var quotedPriceColumn = QuoteIdentifier(priceColumn);
        var nameProjection = candidate.NameColumn is null
            ? "null::text as name"
            : $"{QuoteIdentifier(candidate.NameColumn)}::text as name";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            select
                {pznColumn}::text as pzn,
                {nameProjection},
                {quotedPriceColumn}::text as preis
            from {qualifiedTable}
            where regexp_replace(coalesce({pznColumn}::text, ''), '\D', '', 'g') = @pzn
              and {quotedPriceColumn} is not null
            limit 20;
            """;
        command.Parameters.AddWithValue("pzn", NpgsqlDbType.Varchar, normalizedPzn);

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new MedicationPriceDto
                {
                    Pzn = GetNullableString(reader, "pzn"),
                    Name = GetNullableString(reader, "name"),
                    Preis = GetNullableString(reader, "preis") ?? "",
                    Schema = candidate.Schema,
                    Tabelle = candidate.Table,
                    PznSpalte = candidate.PznColumn,
                    PreisSpalte = priceColumn
                });
            }
        }
        catch (PostgresException)
        {
            // MMI-Versionen koennen Views/Tabellen enthalten, die trotz information_schema nicht lesbar sind.
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}

sealed record MmiColumn(string Schema, string Table, string Name, string DataType);

sealed record MmiPriceCandidateTable(
    string Schema,
    string Table,
    string PznColumn,
    IReadOnlyList<string> PriceColumns,
    string? NameColumn);

sealed class CdnDocumentStore
{
    public CdnDocumentStore(NpgsqlDataSource dataSource, string rootPath)
    {
        DataSource = dataSource;
        RootPath = rootPath;
    }

    public NpgsqlDataSource DataSource { get; }
    public string RootPath { get; }
}
