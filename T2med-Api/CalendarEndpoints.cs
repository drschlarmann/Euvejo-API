using System.Globalization;
using Npgsql;

namespace T2med_Api;

public static class CalendarEndpoints
{
    public static void MapCalendarEndpoints(this WebApplication app)
    {
        app.MapGet("/api/calendar/appointments", async (HttpContext context, ICalendarReader reader, ILogger<CalendarReader> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!CalendarQuery.TryParse(context.Request.Query, out var query, out var error))
                return Results.BadRequest(new { message = error });
            return await ReadSafely(async () => await reader.GetAppointmentsAsync(query!, context.RequestAborted), logger);
        });
        app.MapGet("/api/calendar/types", async (HttpContext context, ICalendarReader reader, ILogger<CalendarReader> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ReadSafely(async () => await reader.GetTypesAsync(context.RequestAborted), logger);
        });
        app.MapGet("/api/calendar/resources", async (HttpContext context, ICalendarReader reader, ILogger<CalendarReader> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await ReadSafely(async () => await reader.GetResourcesAsync(context.RequestAborted), logger);
        });
    }

    private static async Task<IResult> ReadSafely(Func<Task<object>> read, ILogger logger)
    {
        try { return Results.Ok(await read()); }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            logger.LogWarning("Kalenderschema nicht kompatibel (SQLSTATE {SqlState}).", ex.SqlState);
            return Results.Problem(statusCode: 503, title: "Das Kalenderschema dieser T2med-Version wird noch nicht unterstützt.");
        }
        catch (NpgsqlException)
        {
            logger.LogWarning("Kalenderdatenbank konnte nicht gelesen werden.");
            return Results.Problem(statusCode: 503, title: "Kalenderdatenbank derzeit nicht erreichbar oder Abfrage fehlgeschlagen.");
        }
    }
}

public sealed record CalendarQuery(DateOnly From, DateOnly To, DateTime FromUtc, DateTime UntilUtc,
    string? TypeId, string? ResourceId, int Limit, int Offset)
{
    public static bool TryParse(IQueryCollection values, out CalendarQuery? query, out string error)
    {
        query = null;
        error = "from und to müssen im Format yyyy-MM-dd angegeben werden; maximal 31 Kalendertage einschließlich Enddatum.";
        foreach (var key in new[] { "from", "to", "typeId", "resourceId", "limit", "offset" })
            if (values[key].Count > 1) { error = $"Parameter {key} darf nur einmal vorkommen."; return false; }
        if (!DateOnly.TryParseExact(values["from"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(values["to"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            || to < from || to.DayNumber - from.DayNumber >= 31 || to == DateOnly.MaxValue) return false;
        if (!ParseNumber(values, "limit", 200, 1, 1000, out var limit)
            || !ParseNumber(values, "offset", 0, 0, 100000, out var offset))
        { error = "limit muss zwischen 1 und 1000 liegen, offset zwischen 0 und 100000."; return false; }
        var typeId = values["typeId"].ToString();
        var resourceId = values["resourceId"].ToString();
        if (typeId.Length > 255 || resourceId.Length > 255)
        { error = "Filter-IDs dürfen höchstens 255 Zeichen enthalten."; return false; }
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        query = new(from, to,
            TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone),
            TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone),
            string.IsNullOrWhiteSpace(typeId) ? null : typeId,
            string.IsNullOrWhiteSpace(resourceId) ? null : resourceId, limit, offset);
        error = "";
        return true;
    }

    private static bool ParseNumber(IQueryCollection values, string key, int fallback, int min, int max, out int result)
    {
        result = fallback;
        return !values.ContainsKey(key) || (int.TryParse(values[key], NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= min && result <= max);
    }
}
