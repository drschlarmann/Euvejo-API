# Euvejo-API Dokumentation

Diese Dokumentation beschreibt die HTTP-Endpunkte der `Euvejo-Api`.

## Authentifizierung

Jeder Aufruf, einschließlich `/health`, muss das bei der Installation festgelegte
API-Kennwort im HTTP-Header `X-Euvejo-Api-Password` mitsenden:

```http
X-Euvejo-Api-Password: <API-Kennwort>
```

Ein fehlendes oder falsches Kennwort wird mit `401 Unauthorized` abgewiesen. Der
Server speichert nur einen gesalzenen PBKDF2-SHA256-Hash des Kennworts in der
DPAPI-verschlüsselten `appsettings.json`. Das Kennwort lässt sich mit
`Euvejo-Api-Config.exe` ändern; danach muss der Windows-Dienst neu gestartet werden.
Das Kennwort muss 6 bis 256 Zeichen lang sein und darf keinen Zeilenumbruch enthalten.
Weitere Zusammensetzungsregeln bestehen nicht.
Da der Header auf Anwendungsebene Klartext enthält, sind produktive Aufrufe ausschließlich
über HTTPS auf Port 5299 zulässig. HTTP auf Port 5298 ist ab Version 1.0.9 standardmäßig
deaktiviert. Die Implementierung bleibt für einen bewusst aktivierten Kompatibilitätsbetrieb
über `Http:Enabled=true` erhalten.

## Basisadresse

Standard der installierten API:

```text
https://<T2med-Server>:5299
```

Das lokale Entwicklungsprofil darf weiterhin eine abweichende HTTP-Adresse verwenden.

## Kalender lesen (lokale Erweiterung vom 18.09.2026)

Diese Endpunkte gehören zur Euvejo-API. Sie lesen die T2med-Datenbank direkt und
verwenden denselben Kennwortschutz wie die übrige Euvejo-API. Die Hersteller-FHIR-API
ist daran nicht beteiligt.

| Methode | Pfad | Inhalt |
| --- | --- | --- |
| GET | `/api/calendar/appointments?from=2026-09-18&to=2026-09-18` | Termine, die den Zeitraum überlappen |
| GET | `/api/calendar/types` | Termintypen mit ID, Bezeichnung, Kürzel, Standarddauer und Farbwert |
| GET | `/api/calendar/resources` | Ressourcen mit ID, Bezeichnung, Beschreibung, Einsatzbereitschaft und Typcode |

### Terminfilter

- `from`, `to`: erforderlich, exakt `yyyy-MM-dd`, einschließlich beider Tage,
  maximal 31 Kalendertage. Die Tagesgrenzen werden in `Europe/Berlin` berechnet,
  auch beim Wechsel zwischen Sommer- und Winterzeit.
- `typeId`: optional, Object-ID des Termintyps.
- `resourceId`: optional, Object-ID einer belegten Terminressource.
- `limit`: 1–1000, Standard 200.
- `offset`: 0–100000, Standard 0.

Beispiel:

```http
GET https://<T2med-Server>:5299/api/calendar/appointments?from=2026-09-18&to=2026-09-24&limit=200&offset=0
X-Euvejo-Api-Password: <API-Kennwort>
```

Die Antwort enthält `from`, `to`, `timeZone`, `limit`, `offset`, `hasMore`,
`nextOffset` und `items`. Mit `nextOffset` kann die nächste Seite angefordert werden.
Bei Erreichen der Offset-Grenze ist `nextOffset` null, auch wenn `hasMore` noch true ist;
dann den Zeitraum eingrenzen. Sortierung: Beginn, anschließend Object-ID.
Bei gleichzeitigen Kalenderänderungen ist eine Folge von Offset-Seiten keine feste Momentaufnahme.

Jeder Termin enthält:

- `id`, `beginn`, `ende` (Zeitpunkte als UTC mit `Z`)
- `bezeichnung`, `beschreibung`, `externeBemerkung`
- `inhaber` (unveränderter Datenbankwert), `variante` (uninterpretierter Typcode)
- `termintypId`, `termintyp`, `ressourcenIds`

Auch vor dem Zeitraum beginnende, hineinreichende Termine werden berücksichtigt.
Termine ohne Ende oder mit Ende vor/gleich Beginn werden anhand ihres Beginns aufgenommen.
Ein Ende genau am Beginn des Suchzeitraums gilt bei normalen Zeitintervallen nicht als Überlappung.
Einträge ohne Beginn werden nicht ausgegeben. Es gibt keine implizite Filterung nach Variante.

### Grenzen und Prüfung

Implementiert anhand der lokalen Schemaanalyse vom 11.06.2026:
`aps.termin`, `aps.termintyp`, `aps.belegteressource`, `aps.terminressource`.
Das Schema und die fachliche Darstellung konnten am 18.09.2026 nicht mit einer laufenden
Datenbank abgeglichen werden: Verbindungen nach localhost und <T2med-Server> auf Port 16569
wurden abgewiesen. Insbesondere ist `inhaber` noch nicht als Patientenreferenz validiert;
deshalb werden keine daraus abgeleiteten Patientennummern oder Namen ausgegeben.

Die Endpunkte liefern gespeicherte Termine und Ressourcen, keine Berechnung freier
Zeitfenster, Wochenpläne, Sperrzeiten oder Serientermin-Expansion. Die Vollständigkeit
gegenüber einer konkreten T2med-Kalenderansicht muss am Zielsystem geprüft werden.

Alle Kalenderabfragen verwenden parametrisierte SQL-Anweisungen, eine READ-ONLY-Transaktion,
15 Sekunden Abfragezeitlimit und Abbruch bei getrenntem HTTP-Aufruf. Antworten tragen
`Cache-Control: no-store`. Ungültige Filter ergeben HTTP 400, fehlendes/falsches Kennwort
HTTP 401, inkompatibles Schema oder Datenbankfehler HTTP 503 ohne interne Verbindungsdetails.
Es gibt keine POST-, PUT- oder DELETE-Kalenderoperationen.

Tests: `dotnet run --project ..\Euvejo.CalendarTests` prüft HTTP-Verhalten mit Testdatenquelle,
Validierung und Zeitumstellungen. Für den echten Datenbanktest die Verbindungszeichenfolge
nur in der Prozessumgebung als `EUVEJO_CALENDAR_TEST_CONNECTION` setzen und
`dotnet run --project ..\Euvejo.CalendarTests -- --live` ausführen. Der Live-Test liest nur
und gibt keine Termin-/Patienteninhalte aus. Der SQL-Live-Test steht noch aus.

Die Erweiterung wurde lokal gebaut; ein neues MSI und eine Installation auf dem
Praxisserver sind damit noch nicht erfolgt.

## Datenbankvoraussetzungen

Die API benötigt Zugriff auf:

- die T2med-PostgreSQL-Datenbank
- die T2med-CDN-PostgreSQL-Datenbank
- den konfigurierten CDN-Dateispeicher

Die Produktionskonfiguration liegt DPAPI-verschlüsselt in `appsettings.json` und wird
mit `Euvejo-Api-Config.exe` bearbeitet. Nur das Entwicklungsprofil darf eine lokale
Klartextvorlage verwenden. Verwaltete Schlüssel sind:

- `ConnectionStrings:T2medDatabase`
- `ConnectionStrings:T2medCdnDatabase`
- `ConnectionStrings:MMIDatabase`
- `T2med:CdnRoot`
- `Security:ApiPassword` (nur Hash, Salt und Verfahrensparameter)

Beim späteren Wechsel vom bisherigen Installationsnamen übernimmt das Setup eine
vorhandene verschlüsselte Konfiguration automatisch. Zertifikat und API-Kennwort
bleiben dadurch für bereits eingerichtete Euvejo-Clients gültig.

## Fehlerformat

Fehler werden in der Regel als JSON-Objekt mit `message` zurückgegeben:

```json
{
  "message": "Fehlerbeschreibung"
}
```

## Endpunkte

### Patient nach Nummer laden

```http
GET /api/patients/{nummer}
```

Lädt die Stammdaten eines Patienten anhand der T2med-Patientennummer.

Parameter:

- `nummer` `long`: T2med-Patientennummer

Erfolgsantwort:

- `200 OK`
- JSON-Objekt mit den Patientendaten aus `aps.patient`

Typische Felder:

- `objectid`
- `nummer`
- `geschlecht`
- `namensdaten_vorname`
- `namensdaten_nachname`
- `geburtsdaten_datum_normalizeddate`
- `anschrift_strasse`
- `anschrift_hausnummer`
- `anschrift_plz`
- `anschrift_ort`
- weitere Patientenstammdaten

Fehler:

- `404 Not Found`: Patient wurde nicht gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345
Accept: application/json
```

### Medikamentenpreis nach PZN laden

```http
GET /api/medications/{pzn}/price
```

Sucht in der konfigurierten MMI-Datenbank nach Tabellen mit PZN- und Preis-Spalten und liefert passende Preiswerte zur PZN zurueck.

Parameter:

- `pzn` `string`: Pharmazentralnummer. Trennzeichen werden ignoriert.

Erfolgsantwort:

- `200 OK`

```json
{
  "pzn": "12345678",
  "preise": [
    {
      "pzn": "12345678",
      "name": "Beispielmedikament",
      "preis": "12.34",
      "schema": "public",
      "tabelle": "arzneimittel",
      "pznSpalte": "pzn",
      "preisSpalte": "preis"
    }
  ]
}
```

Fehler:

- `400 Bad Request`: PZN ist leer oder ungueltig
- `404 Not Found`: Fuer die PZN wurde kein Preis in der MMI-Datenbank gefunden

### Diagnosen des aktuellen Behandlungsfalls

```http
GET /api/patients/{nummer}/current-case/diagnoses
```

Lädt den aktuellen Behandlungsfall eines Patienten und die zugehörigen aktivierten Diagnosen.

Parameter:

- `nummer` `long`: T2med-Patientennummer

Erfolgsantwort:

- `200 OK`
- JSON-Objekt mit Patient, Behandlungsfall und Diagnosen

Antwortstruktur:

```json
{
  "patientNummer": 12345,
  "patientNachname": "Mustermann",
  "patientVorname": "Max",
  "patientGeburtsdatum": "1970-01-01",
  "behandlungsfall": {
    "objectId": "...",
    "revision": 1,
    "creationTimestamp": "...",
    "modificationTimestamp": "...",
    "beginn": "...",
    "ende": null,
    "classId": 0,
    "abrechnungQuartal": 0
  },
  "diagnosen": [
    {
      "objectId": "...",
      "icd": "I10.90",
      "klartext": "Essentielle Hypertonie",
      "erlaeuterung": null,
      "lokalisation": 0,
      "relevanz": 0,
      "sicherheit": 0,
      "fachinfoGlobalId": 0,
      "interneBemerkung": null
    }
  ]
}
```

Fehler:

- `404 Not Found`: Patient wurde nicht gefunden
- `404 Not Found`: Kein Behandlungsfall für den Patienten gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345/current-case/diagnoses
Accept: application/json
```

### Medikationsplan des aktuellen Behandlungsfalls

```http
GET /api/patients/{nummer}/current-case/medication-plan
```

Lädt den aktuellen Behandlungsfall und den neuesten Medikationsplan des Patienten.

Parameter:

- `nummer` `long`: T2med-Patientennummer

Erfolgsantwort:

- `200 OK`
- JSON-Objekt mit Patient, Behandlungsfall und Medikamenten

Antwortstruktur:

```json
{
  "patientNummer": 12345,
  "patientNachname": "Mustermann",
  "patientVorname": "Max",
  "patientGeburtsdatum": "1970-01-01",
  "behandlungsfall": {
    "objectId": "...",
    "beginn": "...",
    "ende": null
  },
  "medikamente": [
    {
      "objectId": "...",
      "verordnungszeitpunkt": "...",
      "name": "Medikament",
      "handelsname": null,
      "wirkstoff": "Wirkstoff",
      "wirkstaerkeWert": null,
      "wirkstaerkeEinheit": null,
      "darreichungsformFreitext": null,
      "dosierschemaMorgens": "1",
      "dosierschemaMittags": "0",
      "dosierschemaAbends": "1",
      "dosierschemaNachts": "0",
      "dosierschemaFreitext": null,
      "pzn": null,
      "hinweis": null,
      "freitext": null
    }
  ]
}
```

Fehler:

- `404 Not Found`: Patient wurde nicht gefunden
- `404 Not Found`: Kein Behandlungsfall für den Patienten gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345/current-case/medication-plan
Accept: application/json
```

### Karteieinträge des aktuellen Behandlungsfalls

```http
GET /api/patients/{nummer}/current-case/chart-entries
```

Lädt Karteieinträge des Patienten im Kontext des aktuellen Behandlungsfalls. Laborwerte werden bei Labor-Karteieinträgen als eingebettete Liste mitgeliefert.

Parameter:

- `nummer` `long`: T2med-Patientennummer

Erfolgsantwort:

- `200 OK`
- JSON-Objekt mit Patient, Behandlungsfall und Karteieinträgen

Antwortstruktur:

```json
{
  "patientNummer": 12345,
  "patientNachname": "Mustermann",
  "patientVorname": "Max",
  "patientGeburtsdatum": "1970-01-01",
  "behandlungsfall": {
    "objectId": "...",
    "beginn": "...",
    "ende": null
  },
  "karteieintraege": [
    {
      "objectId": "...",
      "informationszeitpunkt": "...",
      "kategorie": "LAB",
      "kuerzel": "...",
      "titel": "...",
      "text": "...",
      "symbol": "...",
      "anamnestisch": false,
      "cavehinweis": false,
      "fachinfoGlobalId": 0,
      "fachinfoQualifiedId": "...",
      "ordnungszaehler": 0,
      "laborwerte": [
        {
          "kurzbezeichnung": "HB",
          "langbezeichnung": "Haemoglobin",
          "ergebniswert": "14.2",
          "ergebnistext": null,
          "masseinheit": "g/dl",
          "normwertbereichText": "...",
          "grenzwertindikator": null
        }
      ]
    }
  ]
}
```

Hinweise:

- `objectId` wird für die Dokument- und Bild-Endpunkte benötigt.
- Dokumente werden über Karteieinträge mit `fachinfoGlobalId = 75` erwartet.
- Bilder werden über Karteieinträge mit `fachinfoGlobalId = 74` erwartet.

Fehler:

- `404 Not Found`: Patient wurde nicht gefunden
- `404 Not Found`: Kein Behandlungsfall für den Patienten gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345/current-case/chart-entries
Accept: application/json
```

### PDF-Dokument eines Karteieintrags laden

```http
GET /api/patients/{nummer}/chart-entries/{chartEntryObjectId}/document
```

Lädt ein PDF-Dokument zu einem Dokument-Karteieintrag.

Parameter:

- `nummer` `long`: T2med-Patientennummer
- `chartEntryObjectId` `string`: `objectId` des Karteieintrags

Voraussetzungen:

- Der Karteieintrag gehört zum Patienten.
- Der Karteieintrag hat `fachinfoGlobalId = 75`.
- Der Karteieintrag verweist per `cdn://...` auf ein CDN-Dokument.
- Das CDN-Dokument ist als PDF gespeichert.
- Die Datei existiert im konfigurierten CDN-Dateispeicher.

Erfolgsantwort:

- `200 OK`
- Binärdaten des PDF-Dokuments
- `Content-Type`, normalerweise `application/pdf`
- Download-Dateiname aus T2med, sofern vorhanden

Fehler:

- `404 Not Found`: Dokument-Karteieintrag wurde nicht gefunden
- `404 Not Found`: Kein CDN-Dokument am Karteieintrag gefunden
- `400 Bad Request`: Datei ist kein PDF
- `404 Not Found`: CDN-Dokument wurde in `t2med_cdn` nicht gefunden
- `404 Not Found`: CDN-Dokument hat keinen Speicherpfad
- `404 Not Found`: CDN-Datei wurde im Dateisystem nicht gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345/chart-entries/OBJECTID/document
Accept: application/pdf
```

### Bild eines Karteieintrags laden

```http
GET /api/patients/{nummer}/chart-entries/{chartEntryObjectId}/image
```

Lädt ein Bild zu einem Bild-Karteieintrag.

Parameter:

- `nummer` `long`: T2med-Patientennummer
- `chartEntryObjectId` `string`: `objectId` des Karteieintrags

Voraussetzungen:

- Der Karteieintrag gehört zum Patienten.
- Der Karteieintrag hat `fachinfoGlobalId = 74`.
- Der Karteieintrag verweist per `cdn://...` auf ein CDN-Bild.
- Die Datei hat einen `image/*` MIME-Type.
- Die Datei existiert im konfigurierten CDN-Dateispeicher.

Erfolgsantwort:

- `200 OK`
- Binärdaten des Bilds
- `Content-Type`, zum Beispiel `image/jpeg` oder `image/png`
- Download-Dateiname aus T2med, sofern vorhanden

Fehler:

- `404 Not Found`: Bild-Karteieintrag wurde nicht gefunden
- `404 Not Found`: Kein CDN-Bild am Karteieintrag gefunden
- `400 Bad Request`: Datei ist kein Bild
- `404 Not Found`: CDN-Bild wurde in `t2med_cdn` nicht gefunden
- `404 Not Found`: CDN-Bild hat keinen Speicherpfad
- `404 Not Found`: CDN-Bilddatei wurde im Dateisystem nicht gefunden

Beispiel:

```http
GET http://localhost:5298/api/patients/12345/chart-entries/OBJECTID/image
Accept: image/*
```

## Empfohlener Testablauf

1. API starten.
2. Patient laden:

   ```http
   GET http://localhost:5298/api/patients/12345
   ```

3. Diagnosen, Medikationsplan und Karteieinträge laden.
4. In den Karteieinträgen eine passende `objectId` suchen:
   - Dokument: `fachinfoGlobalId = 75`
   - Bild: `fachinfoGlobalId = 74`
5. Dokument- oder Bild-Endpunkt mit dieser `objectId` aufrufen.

Alternativ kann das WinForms-Testprojekt `Euvejo-Api-Test` verwendet werden.

## Beispiel mit PowerShell

```powershell
$baseUrl = "http://localhost:5298"
$nummer = 12345

Invoke-RestMethod "$baseUrl/api/patients/$nummer"
Invoke-RestMethod "$baseUrl/api/patients/$nummer/current-case/diagnoses"
Invoke-RestMethod "$baseUrl/api/patients/$nummer/current-case/medication-plan"
Invoke-RestMethod "$baseUrl/api/patients/$nummer/current-case/chart-entries"
```

PDF herunterladen:

```powershell
$objectId = "OBJECTID"
Invoke-WebRequest "$baseUrl/api/patients/$nummer/chart-entries/$objectId/document" -OutFile ".\dokument.pdf"
```

Bild herunterladen:

```powershell
$objectId = "OBJECTID"
Invoke-WebRequest "$baseUrl/api/patients/$nummer/chart-entries/$objectId/image" -OutFile ".\bild"
```

## Betriebshinweise

- Die API nutzt aktuell keine Authentifizierung. Zugriff daher nur in einem kontrollierten Praxisnetz erlauben.
- Firewall-Regeln bewusst setzen, wenn die API von anderen Rechnern erreichbar sein soll.
- Patientendaten, Dokumente und Bilder enthalten sensible medizinische Daten.
- Fehler mit CDN-Dokumenten sind häufig Konfigurations- oder Dateipfadprobleme (`T2med:CdnRoot`, `storagepath`, `latestversion`).
