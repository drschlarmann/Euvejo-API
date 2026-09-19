# Euvejo-API

ASP.NET-Core-API zur Anbindung von Euvejo und EVA an eine lokale T2med-Installation.
Veröffentlichter Quellstand: **1.0.15**, 19.09.2026.

Dies ist ein eigenständiges Integrationsprojekt, nicht die offizielle FHIR-API des
T2med-Herstellers. Der Zugriff erfolgt direkt auf die PostgreSQL-Datenbanken und den CDN-Speicher.

## Funktionen

- Patientenstammdaten und Patientensuche
- Diagnosen, Medikationsplan, Labor und Karteieinträge
- Dokument- und Bildabruf; PDF-Import und Rücknahme von Dokumenteinträgen
- Medikamentenpreise und Verordnungsstatistik
- Lesender Kalenderzugriff nach Zeitraum, Termintyp und Ressource
- EVA-Kalenderaktionen über `POST /api/calendar/eva`
- HTTPS, API-Kennwortschutz, Konfigurationsprogramm und Windows-MSI-Build

Details: [API-Dokumentation](T2med-Api/API-DOKUMENTATION.md).

## Voraussetzungen und Build

Windows x64 und .NET SDK 10. Die API verwendet Windows-DPAPI für installierte
Konfigurationen. Zum Betrieb werden eine passende T2med-Datenbank und CDN-Konfiguration benötigt.

```powershell
dotnet build T2med-Api/Euvejo-Api.csproj -c Release
dotnet build T2med-Api-Config/Euvejo-Api-Config.csproj -c Release
dotnet build T2med-Api-Test/Euvejo-Api-Test.csproj -c Release
dotnet run --project Euvejo.CalendarTests
```

`T2med-Api-Config` enthält die Anwendung zur lokalen Einrichtung von Datenbankzugängen,
CDN-Pfad, HTTPS und einem eigenen API-Kennwort. `T2med-Api/appsettings.example.json`
ist ausschließlich eine Vorlage; sie enthält kein gültiges API-Kennwort.
Die eingerichtete Datei muss als `appsettings.json` neben der API-EXE liegen.
Echte Konfigurationen und Zertifikatschlüssel gehören nicht ins Repository.

## Installer

Die historischen Verzeichnisnamen bleiben erhalten, damit relative Projektverweise funktionieren.
Der Installer-Build benötigt WiX einschließlich der UI-Erweiterung sowie eine lokal
eingerichtete `T2med-Api/appsettings.json` als Publish-Eingabe.
Siehe [Installer-Dokumentation](T2med-Api/installer/README.md).
Fertige MSI-Dateien und installationsspezifische Skripte sind nicht Bestandteil dieses Quellcode-Uploads.

## Betrieb und Testgrenzen

Die API enthält schreibende Operationen. Version 1.0.15 initialisiert beim Start
das eigene Schema `euvejo_calendar`; Kalenderbuchungen und PDF-Importe ändern Daten.
Vor dem Einsatz gegen eine Praxisdatenbank sind Kompatibilität, Berechtigungen und
Sicherung anhand einer geeigneten Testinstallation zu prüfen.

Die Veröffentlichung prüft den Build und die Kalender-HTTP-Tests mit einer Testdatenquelle.
Dabei werden keine Praxisdatenbank und keine Live-Schreibtests angesprochen.
Tests zur Windows-Konfiguration können lokale Testdateien oder Zertifikate erzeugen.

## Veröffentlichung

Enthalten sind API-Quellcode, Konfigurationsprogramm, Testprogramm, ausgewählte Tests
und Installer-Quellen. Ausgeschlossen sind lokale Zugangsdaten, verschlüsselte
Konfigurationen, Datenbankauszüge, Protokolle und Build-Artefakte.
Es wird mit dieser Veröffentlichung keine zusätzliche Open-Source-Lizenz eingeräumt.
