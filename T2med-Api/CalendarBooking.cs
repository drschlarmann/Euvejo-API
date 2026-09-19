using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace T2med_Api;

public sealed record EvaDoctorPair(string TypeId, string ResourceId);
public sealed record EvaCalendarRequest(string Owner, EvaDoctorPair[] Assignments, JsonElement Input);

// Separate integration state: never alter T2med's schema or patient records.
public sealed class CalendarBooking(NpgsqlDataSource source)
{
    internal Action<string>? Diagnostic { get; set; }
    static readonly TimeZoneInfo Zone=TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    static string Local(DateTime v)=>TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(v,DateTimeKind.Utc),Zone).ToString("yyyy-MM-dd'T'HH:mm",CultureInfo.InvariantCulture);
    public static int CatalogId(string id)=> (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(id)),0)&0x7fffffff)+0;
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static string Str(JsonElement x,string key,string fallback="")=>x.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
    static int Number(JsonElement x,string key)=>x.TryGetProperty(key,out var v)&&v.TryGetInt32(out var n)?n:0;
    static bool Yes(JsonElement x,string key)=>x.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
    static object Error(string message)=>new {status="error",message};
    static bool Identifier(string s,int max=128)=>s.Length>0&&s.Length<=max&&!s.Contains('\0');
    const string Schema="""
        CREATE SCHEMA IF NOT EXISTS euvejo_calendar;
        CREATE TABLE IF NOT EXISTS euvejo_calendar.offers(
          id varchar(32) PRIMARY KEY, owner varchar(128) NOT NULL, conversation varchar(36) NOT NULL,
          type_id varchar(36) NOT NULL, resource_id varchar(36) NOT NULL,
          starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, token varchar(64) NOT NULL,
          expires_at timestamptz NOT NULL, hold_until timestamptz NULL, held_since timestamptz NULL);
        CREATE INDEX IF NOT EXISTS offers_hold ON euvejo_calendar.offers(resource_id,starts_at,ends_at,hold_until);
        CREATE TABLE IF NOT EXISTS euvejo_calendar.bookings(
          request_key varchar(64) PRIMARY KEY, owner varchar(128) NOT NULL, conversation varchar(36) NOT NULL,
          event_id varchar(36) NOT NULL, link_id varchar(36) NOT NULL, fingerprint varchar(64) NOT NULL,
          starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, resource_id varchar(36) NOT NULL,
          type_id varchar(36) NOT NULL, revision integer NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
        """;
    public async Task InitializeAsync(CancellationToken ct){await using var c=await source.OpenConnectionAsync(ct);await using var cmd=new NpgsqlCommand(Schema,c);await cmd.ExecuteNonQueryAsync(ct);}
    sealed record Pair(EvaDoctorPair Id,string Type,string Doctor,int Duration);
    sealed record Slot(DateTime Start,DateTime End,Pair Pair);
    sealed record Offer(string Id,string Type,string Resource,DateTime Start,DateTime End,string Token,DateTime Expires,DateTime? Hold,DateTime? HeldSince);
    static NpgsqlCommand Cmd(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params (string,object)[] parameters){var cmd=new NpgsqlCommand(sql,c,tx){CommandTimeout=12};foreach(var (k,v) in parameters)cmd.Parameters.AddWithValue(k,v);return cmd;}
    static async Task<object?> Scalar(NpgsqlConnection c,NpgsqlTransaction tx,string sql,CancellationToken ct,params (string,object)[] args){await using var cmd=Cmd(c,tx,sql,args);return await cmd.ExecuteScalarAsync(ct);}
    async Task<List<Pair>> Pairs(NpgsqlConnection c,NpgsqlTransaction tx,EvaDoctorPair[] assignments,CancellationToken ct){
        var result=new List<Pair>();
        foreach(var p in assignments){
            await using var cmd=Cmd(c,tx,"SELECT t.bezeichnung,r.bezeichnung,t.dauerinminuten FROM aps.termintyp t CROSS JOIN aps.terminressource r WHERE t.objectid=@t AND r.objectid=@r AND r.einsatzbereit AND r.ressourcentyp=3 AND t.dauerinminuten BETWEEN 1 AND 480",("t",p.TypeId),("r",p.ResourceId));
            await using var read=await cmd.ExecuteReaderAsync(ct);if(!await read.ReadAsync(ct))throw new InvalidOperationException("Eine konfigurierte Terminart oder Arzt-Ressource ist nicht mehr verfügbar. Bitte Konfiguration prüfen.");
            result.Add(new(p,read.GetString(0),read.GetString(1),read.GetInt32(2)));
        }
        var ids=result.SelectMany(p=>new[]{p.Id.TypeId,p.Id.ResourceId}).Distinct().ToArray();if(ids.Select(CatalogId).Distinct().Count()!=ids.Length||ids.Any(i=>CatalogId(i)==0))throw new InvalidOperationException("Katalogkennung nicht eindeutig.");return result;
    }
    // ISO weekdays (Monday=1), modes 1=both and 3=external; mode 2 is internal-only.
    // A FREI (2) record is a booked free-text appointment, NOT an available slot.
    const string Candidates="""
        WITH days AS (SELECT d::date AS day FROM generate_series(@first::date,@last::date,interval '1 day') d),
        windows AS MATERIALIZED (
          SELECT d.day, z.beginn,z.ende,t.dauerinminuten,r.wochenplan_objectid rw,r.jahresplan_objectid ry
          FROM days d CROSS JOIN aps.termintyp t CROSS JOIN aps.terminressource r
          JOIN aps.wochenplan_tagesplan w ON w.wochenplan_objectid=t.wochenplan_objectid AND w.tagesplaene_key=extract(isodow FROM d.day)
          JOIN aps.tagesplan_zeiten z ON z.tagesplan_objectid=w.tagesplaene_objectid AND z.modus IN (1,3)
          WHERE t.objectid=@type AND r.objectid=@resource AND r.einsatzbereit AND r.ressourcentyp=3 AND t.dauerinminuten BETWEEN 1 AND 480
        ), candidates AS MATERIALIZED (
          SELECT w.*, s AS local_start, s AT TIME ZONE 'Europe/Berlin' begins,(s+make_interval(mins=>w.dauerinminuten)) AT TIME ZONE 'Europe/Berlin' ends
          FROM windows w CROSS JOIN LATERAL generate_series(w.day+w.beginn,
            w.day+w.ende+(CASE WHEN w.ende='00:00'::time THEN interval '1 day' ELSE interval '0' END)-make_interval(mins=>w.dauerinminuten),make_interval(mins=>w.dauerinminuten)) s
          WHERE s::time>=@timeFrom AND s::time<=@timeTo
          AND EXISTS (
            SELECT 1 FROM aps.tagesplan_zeiten rz WHERE rz.modus IN (1,3)
            AND rz.tagesplan_objectid=COALESCE(
              (SELECT y.tagesplaene_objectid FROM aps.jahresplan_tagesplan y WHERE y.jahresplan_objectid=w.ry AND y.tagesplaene_key=w.day),
              (SELECT rw.tagesplaene_objectid FROM aps.wochenplan_tagesplan rw WHERE rw.wochenplan_objectid=w.rw AND rw.tagesplaene_key=extract(isodow FROM w.day)))
            AND s>=w.day+rz.beginn AND s+make_interval(mins=>w.dauerinminuten)<=w.day+rz.ende+(CASE WHEN rz.ende='00:00'::time THEN interval '1 day' ELSE interval '0' END))
        )
        ,busy AS MATERIALIZED (
          SELECT t.beginn,coalesce(t.ende,t.beginn+interval '1 minute') AS ende FROM aps.termin t
          WHERE t.beginn<(@last::date+interval '1 day') AT TIME ZONE 'Europe/Berlin' AND coalesce(t.ende,t.beginn+interval '1 minute')>@first::date::timestamp AT TIME ZONE 'Europe/Berlin'
          AND (EXISTS(SELECT 1 FROM aps.belegteressource b WHERE b.termin_objectid=t.objectid AND b.terminressource_objectid=@resource)
            OR (t.termintyp_objectid=@type AND NOT EXISTS(SELECT 1 FROM aps.belegteressource b WHERE b.termin_objectid=t.objectid)))
        )
        SELECT DISTINCT begins,ends FROM candidates c WHERE begins>now() AND begins AT TIME ZONE 'Europe/Berlin'=local_start AND ends-begins=make_interval(mins=>dauerinminuten)
          AND NOT EXISTS(SELECT 1 FROM busy t WHERE t.beginn<c.ends AND t.ende>c.begins)
          AND NOT EXISTS(SELECT 1 FROM aps.sperrzeit s WHERE s.beginn<c.ends AND s.ende>c.begins
            AND (EXISTS(SELECT 1 FROM aps.terminressource_sperrzeit rs WHERE rs.sperrzeiten_objectid=s.objectid AND rs.terminressource_objectid=@resource)
              OR NOT EXISTS(SELECT 1 FROM aps.terminressource_sperrzeit rs WHERE rs.sperrzeiten_objectid=s.objectid)))
          AND NOT EXISTS(SELECT 1 FROM euvejo_calendar.offers o WHERE o.resource_id=@resource AND o.starts_at<c.ends AND o.ends_at>c.begins
            AND o.hold_until>now() AND NOT(o.owner=@owner AND o.conversation=@conversation))
        ORDER BY begins LIMIT 10000
        """;
    async Task<List<Slot>> Slots(NpgsqlConnection c,NpgsqlTransaction tx,Pair p,DateOnly first,DateOnly last,TimeOnly from,TimeOnly to,string owner,string conversation,CancellationToken ct){
        var list=new List<Slot>();await using var cmd=Cmd(c,tx,Candidates,("first",first),("last",last),("timeFrom",from),("timeTo",to),("type",p.Id.TypeId),("resource",p.Id.ResourceId),("owner",owner),("conversation",conversation));
        await using var rd=await cmd.ExecuteReaderAsync(ct);while(await rd.ReadAsync(ct))list.Add(new(rd.GetDateTime(0),rd.GetDateTime(1),p));return list;
    }
    static async Task<Offer?> GetOffer(NpgsqlConnection c,NpgsqlTransaction tx,string id,string owner,string conversation,CancellationToken ct){
        await using var cmd=Cmd(c,tx,"SELECT id,type_id,resource_id,starts_at,ends_at,token,expires_at,hold_until,held_since FROM euvejo_calendar.offers WHERE id=@id AND owner=@owner AND conversation=@conversation FOR UPDATE",("id",id),("owner",owner),("conversation",conversation));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetDateTime(3),r.GetDateTime(4),r.GetString(5),r.GetDateTime(6),r.IsDBNull(7)?null:r.GetDateTime(7),r.IsDBNull(8)?null:r.GetDateTime(8)):null;
    }
    static string Fingerprint(string name,string description)=>Hash(name+"\n"+description);
    static async Task<object?> Prior(NpgsqlConnection c,NpgsqlTransaction tx,string key,string owner,string conversation,CancellationToken ct){
        await using var cmd=Cmd(c,tx,"""
            SELECT b.event_id,b.starts_at,b.ends_at,b.resource_id,b.type_id,b.revision,b.fingerprint,
              t.objectid,t.beginn,t.ende,t.revision,t.bezeichnung,t.beschreibung,t.termintyp_objectid,
              EXISTS(SELECT 1 FROM aps.belegteressource l WHERE l.objectid=b.link_id AND l.termin_objectid=t.objectid AND l.terminressource_objectid=b.resource_id),
              (SELECT r.bezeichnung FROM aps.terminressource r WHERE r.objectid=b.resource_id),t.variante
            FROM euvejo_calendar.bookings b LEFT JOIN aps.termin t ON t.objectid=b.event_id
            WHERE b.request_key=@key AND b.owner=@owner AND b.conversation=@conversation
            """,("key",key),("owner",owner),("conversation",conversation));
        await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        if(r.IsDBNull(7)||r.GetDateTime(1)!=r.GetDateTime(8)||r.GetDateTime(2)!=r.GetDateTime(9)||r.GetInt32(5)!=r.GetInt32(10)||r.GetString(4)!=r.GetString(13)||!r.GetBoolean(14)||r.GetInt32(16)!=2||r.GetString(6)!=Fingerprint(r.GetString(11),r.GetString(12)))return new {status="changed",message="Der Termin wurde nachträglich geändert oder entfernt. Bitte durch das Team prüfen lassen."};
        return new {status="booked",id=r.GetString(0),start=Local(r.GetDateTime(1)),end=Local(r.GetDateTime(2)),timezone="Europe/Berlin",source="t2med",staff_name=r.IsDBNull(15)?"":r.GetString(15),recovered=true};
    }
    public async Task<object> ExecuteAsync(EvaCalendarRequest request,CancellationToken ct){
      try{return await ExecuteCore(request,ct);}catch(InvalidOperationException e){return Error(e.Message);}catch(ArgumentException){return Error("Ungültige Termindaten. Bitte neu prüfen.");}catch(PostgresException e){Diagnostic?.Invoke(e.SqlState+": "+e.MessageText);return Error("T2med konnte die Aktion nicht abschließen. Keine Buchung bestätigen; Status prüfen.");}
    }
    async Task<object> ExecuteCore(EvaCalendarRequest req,CancellationToken ct){
        if(!Identifier(req.Owner)||req.Assignments is null||req.Assignments.Length>50||req.Assignments.Any(p=>p is null||!Identifier(p.TypeId,36)||!Identifier(p.ResourceId,36))||req.Assignments.Select(p=>p.ResourceId).Distinct().Count()!=req.Assignments.Length)throw new ArgumentException();
        var input=req.Input;var action=Str(input,"action");var conversation=Str(input,"conversation_id");
        if(action is not("options" or "health")&&!Guid.TryParse(conversation,out _))throw new ArgumentException();
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await Scalar(c,tx,"SET LOCAL search_path=aps,public; SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='12s'",ct);
        var pairs=await Pairs(c,tx,req.Assignments,ct);
        if(action=="health")return new {status="reachable",enabled=pairs.Count>0,source="t2med",booking=true};
        if(action=="options")return new {source="t2med",types=pairs.GroupBy(p=>p.Id.TypeId).Select(g=>new {id=CatalogId(g.Key),name=g.First().Type,duration=g.First().Duration,examples="Konfigurierte Terminart dieses Arztes"}),staff=pairs.Select(p=>new {id=CatalogId(p.Id.ResourceId),name=p.Doctor,type_id=CatalogId(p.Id.TypeId)}),selection_note="Jeder Arzt hat eine fest konfigurierte Terminart. Mitarbeiterwunsch erfragen; bei mehreren Ärzten vor Buchung den Arzt auswählen lassen. Nicht die Terminart eines anderen Arztes verwenden. Absagen/Verschieben durch das Team.",capabilities=new[]{"availability","hold","book","status","release"}};
        var slot=Str(input,"slot_id");var key=Hash(req.Owner+":"+conversation+":"+slot);
        if(action=="status"&&Str(input,"operation_action")!="book")return new {status="handoff",message="Nur T2med-Buchungsstatus unterstützt."};
        if(action=="status")return await Prior(c,tx,key,req.Owner,conversation,ct)??new {status="not_recorded"};
        if(action=="book"){var prior=await Prior(c,tx,key,req.Owner,conversation,ct);if(prior!=null)return prior;}
        if(action=="release"){
            await Scalar(c,tx,"UPDATE euvejo_calendar.offers SET hold_until=NULL WHERE owner=@owner AND conversation=@conversation",ct,("owner",req.Owner),("conversation",conversation));await tx.CommitAsync(ct);return new {status="released"};
        }
        if(action=="availability"){
            if(!DateOnly.TryParseExact(Str(input,"date"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var first)||!DateOnly.TryParseExact(Str(input,"end_date",Str(input,"date")),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var last))throw new ArgumentException();
            var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,Zone));if(first<today||last<first||last.DayNumber-first.DayNumber>30||last>today.AddDays(366))throw new ArgumentException();
            if(!TimeOnly.TryParseExact(Str(input,"time_from","00:00"),"HH:mm",out var from)||!TimeOnly.TryParseExact(Str(input,"time_to","23:59"),"HH:mm",out var to)||from>to)throw new ArgumentException();
            var staff=Number(input,"staff_id");var type=Number(input,"type_id");var filtered=pairs.Where(p=>(staff==0||CatalogId(p.Id.ResourceId)==staff)&&(type==0||CatalogId(p.Id.TypeId)==type));var candidates=new List<Slot>();
            foreach(var p in filtered)candidates.AddRange(await Slots(c,tx,p,first,last,from,to,req.Owner,conversation,ct));
            var pref=TimeOnly.TryParseExact(Str(input,"preferred_time"),"HH:mm",out var preferred)?first.ToDateTime(preferred):(DateTime?)null;
            var choices=candidates.OrderBy(s=>pref.HasValue?Math.Abs((TimeZoneInfo.ConvertTimeFromUtc(s.Start,Zone)-pref.Value).TotalSeconds):s.Start.Ticks).ThenBy(s=>s.Pair.Doctor).Take(6).ToArray();
            var result=new List<object>();foreach(var s in choices){var id=Guid.NewGuid().ToString("N");var token=Hash(Guid.NewGuid().ToString());
                await Scalar(c,tx,"INSERT INTO euvejo_calendar.offers(id,owner,conversation,type_id,resource_id,starts_at,ends_at,token,expires_at) VALUES(@id,@owner,@conversation,@type,@resource,@start,@end,@token,now()+interval '15 minutes')",ct,("id",id),("owner",req.Owner),("conversation",conversation),("type",s.Pair.Id.TypeId),("resource",s.Pair.Id.ResourceId),("start",s.Start),("end",s.End),("token",token));
                result.Add(new {slot_id=id,offer_token=token,start=Local(s.Start),end=Local(s.End),staff_id=CatalogId(s.Pair.Id.ResourceId),staff_name=s.Pair.Doctor,type_id=CatalogId(s.Pair.Id.TypeId),type_name=s.Pair.Type});
            }
            await Scalar(c,tx,"DELETE FROM euvejo_calendar.offers WHERE expires_at<now()-interval '1 day' AND (hold_until IS NULL OR hold_until<now())",ct);await tx.CommitAsync(ct);
            return new {status="available",slots=result,public_free_in_range=candidates.Count,timezone="Europe/Berlin",source="t2med",selection_note="Bei mehreren passenden Ärzten Mitarbeiterwunsch erfragen. Jede angebotene Zeit gehört ausschließlich zum genannten Arzt und dessen Terminart."};
        }
        if(action is not("hold" or "book"))return new {status="handoff",message="Diese Aktion wird für T2med noch nicht unterstützt. Bitte an das Team übergeben. Keine Änderung zusagen."};
        // Serialize against all native T2med writes as well as other EVA instances. No long-lived locks.
        await Scalar(c,tx,"LOCK TABLE aps.termin,aps.belegteressource IN SHARE ROW EXCLUSIVE MODE; LOCK TABLE euvejo_calendar.offers IN SHARE ROW EXCLUSIVE MODE; LOCK TABLE aps.termintyp,aps.terminressource,aps.wochenplan_tagesplan,aps.jahresplan_tagesplan,aps.tagesplan_zeiten,aps.sperrzeit,aps.terminressource_sperrzeit IN SHARE MODE",ct);
        if(action=="book"){var prior=await Prior(c,tx,key,req.Owner,conversation,ct);if(prior!=null)return prior;}
        var offer=await GetOffer(c,tx,slot,req.Owner,conversation,ct)??throw new InvalidOperationException("Angebot nicht gefunden. Verfügbarkeit erneut prüfen.");
        if(offer.Expires<DateTime.UtcNow)throw new InvalidOperationException("Angebot abgelaufen. Verfügbarkeit erneut prüfen.");
        var pair=pairs.SingleOrDefault(p=>p.Id.TypeId==offer.Type&&p.Id.ResourceId==offer.Resource)??throw new InvalidOperationException("Arzt-Zuordnung wurde geändert. Bitte neu suchen.");
        var day=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(offer.Start,Zone));var tm=TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(offer.Start,Zone));
        if(!(await Slots(c,tx,pair,day,day,tm,tm,req.Owner,conversation,ct)).Any(s=>s.Start==offer.Start&&s.End==offer.End))throw new InvalidOperationException("Dieser Termin ist nicht mehr verfügbar. Bitte Alternativen suchen.");
        if(action=="hold"){
            var since=offer.HeldSince??DateTime.UtcNow;if(since.AddMinutes(10)<=DateTime.UtcNow)throw new InvalidOperationException("Reservierung abgelaufen. Bitte neu suchen.");var until=new[]{DateTime.UtcNow.AddMinutes(3),since.AddMinutes(10)}.Min();
            await Scalar(c,tx,"UPDATE euvejo_calendar.offers SET hold_until=NULL WHERE owner=@owner AND conversation=@conversation; UPDATE euvejo_calendar.offers SET hold_until=@until,held_since=@since WHERE id=@id",ct,("owner",req.Owner),("conversation",conversation),("until",until),("since",since),("id",slot));await tx.CommitAsync(ct);return new {status="held",seconds=(int)(until-DateTime.UtcNow).TotalSeconds,start=Local(offer.Start),end=Local(offer.End),staff_name=pair.Doctor,timezone="Europe/Berlin",message="Für EVA reserviert; vor endgültiger Buchung wird erneut gegen T2med geprüft."};
        }
        var name=Str(input,"name").Trim();var phone=Str(input,"phone").Trim();var reason=Str(input,"reason").Trim();
        if(!Yes(input,"confirmed")||!Identifier(name,200)||!Identifier(reason,1000)||!Identifier(phone,80)||phone.Count(char.IsDigit)<5||Str(input,"offer_token")!=offer.Token)throw new InvalidOperationException("Namen, Rückrufnummer, Anliegen und ausdrückliche Terminbestätigung prüfen.");
        if(offer.Hold is null||offer.Hold<DateTime.UtcNow)throw new InvalidOperationException("Termin zuerst erneut reservieren und bestätigen lassen.");
        var title=name+" – "+reason; if(title.Length>250)title=title[..250];var description="Durch EVA telefonisch gebucht.\nName: "+name+"\nTelefon: "+phone+"\nAnliegen: "+reason;
        var idTerm=(string)(await Scalar(c,tx,"SELECT aps.t2_generate_objectid('0059'::varchar)",ct))!;var idLink=(string)(await Scalar(c,tx,"SELECT aps.t2_generate_objectid('020c'::varchar)",ct))!;
        await Scalar(c,tx,"INSERT INTO aps.termin(objectid,revision,creationtimestamp,beginn,ende,bezeichnung,beschreibung,termintyp_objectid,variante) VALUES(@id,0,now(),@start,@end,@title,@description,@type,2); INSERT INTO aps.belegteressource(objectid,revision,creationtimestamp,termin_objectid,terminressource_objectid,belegteressourcen_order) VALUES(@link,0,now(),@id,@resource,0)",ct,("id",idTerm),("link",idLink),("start",offer.Start),("end",offer.End),("title",title),("description",description),("type",offer.Type),("resource",offer.Resource));
        await Scalar(c,tx,"INSERT INTO euvejo_calendar.bookings(request_key,owner,conversation,event_id,link_id,fingerprint,starts_at,ends_at,resource_id,type_id,revision) VALUES(@key,@owner,@conversation,@event,@link,@fingerprint,@start,@end,@resource,@type,0); UPDATE euvejo_calendar.offers SET hold_until=NULL WHERE owner=@owner AND conversation=@conversation",ct,("key",key),("owner",req.Owner),("conversation",conversation),("event",idTerm),("link",idLink),("fingerprint",Fingerprint(title,description)),("start",offer.Start),("end",offer.End),("resource",offer.Resource),("type",offer.Type));
        await tx.CommitAsync(ct);
        // A committed row is read through a new transaction before the caller receives booked.
        await using var verify=await c.BeginTransactionAsync(ct);return await Prior(c,verify,key,req.Owner,conversation,ct)??Error("Speicherstatus unklar. Status prüfen, keine neue Buchung anlegen.");
    }
}
