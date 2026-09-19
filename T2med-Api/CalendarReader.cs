using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace T2med_Api;

public interface ICalendarReader
{
    Task<CalendarPage> GetAppointmentsAsync(CalendarQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<CalendarType>> GetTypesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CalendarResource>> GetResourcesAsync(CancellationToken cancellationToken);
}

public sealed record CalendarType(string Id, string? Bezeichnung, string? Kuerzel, int DauerInMinuten, int Farbe);
public sealed record CalendarResource(string Id, string Bezeichnung, string? Beschreibung, bool Einsatzbereit, int Ressourcentyp);
public sealed record CalendarAppointment(string Id, DateTime? Beginn, DateTime? Ende, string? Bezeichnung,
    string? Beschreibung, string? ExterneBemerkung, string? Inhaber, int Variante,
    string TermintypId, string? Termintyp, IReadOnlyList<string> RessourcenIds);
public sealed record CalendarPage(DateOnly From, DateOnly To, string TimeZone, int Limit, int Offset,
    bool HasMore, int? NextOffset, IReadOnlyList<CalendarAppointment> Items);

public sealed class CalendarReader(NpgsqlDataSource dataSource) : ICalendarReader
{
    internal const string AppointmentSql = """
        select t.objectid, t.beginn, t.ende, t.bezeichnung, t.beschreibung, t.externebemerkung,
               t.inhaber, t.variante, t.termintyp_objectid, tt.bezeichnung,
               coalesce((select jsonb_agg(distinct br.terminressource_objectid order by br.terminressource_objectid)
                         from aps.belegteressource br where br.termin_objectid = t.objectid
                         and br.terminressource_objectid is not null), '[]'::jsonb)::text
        from aps.termin t
        left join aps.termintyp tt on tt.objectid = t.termintyp_objectid
        where t.beginn < @until
          and (t.ende > @from or (t.beginn >= @from and (t.ende is null or t.ende <= t.beginn)))
          and (@typeId is null or t.termintyp_objectid = @typeId)
          and (@resourceId is null or exists (
              select 1 from aps.belegteressource br
              where br.termin_objectid = t.objectid and br.terminressource_objectid = @resourceId))
        order by t.beginn, t.objectid
        limit @take offset @offset
        """;

    public async Task<CalendarPage> GetAppointmentsAsync(CalendarQuery query, CancellationToken cancellationToken)
    {
        var items = await ReadAsync(AppointmentSql, cmd =>
        {
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, query.FromUtc);
            cmd.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, query.UntilUtc);
            cmd.Parameters.AddWithValue("typeId", NpgsqlDbType.Varchar, (object?)query.TypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("resourceId", NpgsqlDbType.Varchar, (object?)query.ResourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("take", query.Limit + 1);
            cmd.Parameters.AddWithValue("offset", query.Offset);
        }, r => new CalendarAppointment(r.GetString(0), Time(r, 1), Time(r, 2), Text(r, 3), Text(r, 4),
            Text(r, 5), Text(r, 6), r.GetInt32(7), r.GetString(8), Text(r, 9),
            JsonSerializer.Deserialize<string[]>(r.GetString(10))!), cancellationToken);
        var hasMore = items.Count > query.Limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(query.From, query.To, "Europe/Berlin", query.Limit, query.Offset, hasMore,
            hasMore && query.Offset + query.Limit <= 100000 ? query.Offset + query.Limit : null, items);
    }

    public async Task<IReadOnlyList<CalendarType>> GetTypesAsync(CancellationToken cancellationToken) =>
        await ReadAsync("select objectid, bezeichnung, kuerzel, dauerinminuten, farbe from aps.termintyp order by bezeichnung, objectid",
            _ => { }, r => new CalendarType(r.GetString(0), Text(r, 1), Text(r, 2), r.GetInt32(3), r.GetInt32(4)), cancellationToken);

    public async Task<IReadOnlyList<CalendarResource>> GetResourcesAsync(CancellationToken cancellationToken) =>
        await ReadAsync("select objectid, bezeichnung, beschreibung, einsatzbereit, ressourcentyp from aps.terminressource order by bezeichnung, objectid",
            _ => { }, r => new CalendarResource(r.GetString(0), r.GetString(1), Text(r, 2), r.GetBoolean(3), r.GetInt32(4)), cancellationToken);

    private async Task<List<T>> ReadAsync<T>(string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 15;
        command.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '15s'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = sql;
        bind(command);
        var result = new List<T>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Add(map(reader));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? Time(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
