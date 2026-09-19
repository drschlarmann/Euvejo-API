# Euvejo-Api Installationspaket

Zielpfad: `C:\Program Files\euvejo-api`

## Installation

PowerShell als Administrator starten und im Paketordner ausfuehren:

```powershell
.\install.ps1
```

Das Skript kopiert die Dateien nach `C:\Program Files\euvejo-api`, legt den Windows-Dienst `Euvejo-Api` an und startet ihn.

Alternativ kann das MSI installiert werden:

```powershell
msiexec /i .\Euvejo-Api.msi
```

Das MSI:

- installiert `Euvejo-Api` als Windows-Dienst
- installiert `Euvejo-Api-Test.exe` in denselben Ordner
- erstellt eine Desktop-Verknuepfung `Euvejo-API Test`
- fragt nach dem Installationsordner von T2med
- fragt ein API-Kennwort mit 6 bis 256 Zeichen ab und speichert nur dessen Hash
- bildet daraus den CDN-Pfad `<T2med-Installationsordner>\data\cdn`
- schreibt den CDN-Pfad in `appsettings.json`
- verschluesselt `appsettings.json` mit Windows-DPAPI fuer den API-Rechner
- installiert `Euvejo-Api-Config.exe` und eine Desktop-Verknuepfung zur Konfiguration
- deaktiviert HTTP auf Port `5298` und entfernt dessen bisherige Windows-Firewallregel
- aktiviert HTTPS auf Port `5299` (Firewall: privates/Domaenen-Netz, lokales Subnetz)
- erstellt ein rechnerbezogenes HTTPS-Zertifikat im Windows-Zertifikatspeicher
- startet den Dienst am Ende der Installation

Silent-Installation:

```powershell
msiexec /i .\Euvejo-Api.msi /qn /norestart T2MEDAPIPASSWORD="<mindestens-6-Zeichen>"
```

Bei einer Silent-Installation wird fuer den T2med-Installationsordner der Standard `D:\t2med` verwendet.
Die Kennworteigenschaft ist in MSI-Protokollen als verborgen markiert. Da sie bei einer
Silent-Installation dennoch kurz in der Prozesskommandozeile steht, darf dieser Aufruf
nur in einer geschuetzten Administratorsitzung erfolgen.

Fuer das API-Kennwort gelten ausschliesslich diese Vorgaben: mindestens 6 und hoechstens
256 Zeichen, kein Zeilenumbruch und zweimal identische Eingabe. Gross-/Kleinbuchstaben,
Ziffern und Sonderzeichen sind erlaubt, aber nicht einzeln vorgeschrieben. Leerzeichen
zaehlen als Zeichen und werden nicht automatisch entfernt.

Eine vorhandene `appsettings.json` im Zielordner bleibt bei Updates erhalten. Zum Ueberschreiben:

```powershell
.\install.ps1 -OverwriteConfig
```

## Deinstallation

Nur Dienst entfernen:

```powershell
.\uninstall.ps1
```

Dienst und Dateien entfernen:

```powershell
.\uninstall.ps1 -RemoveFiles
```

## Konfiguration

Die verschluesselte Datei `appsettings.json` liegt nach der Installation im Zielordner.
Sie wird mit `Euvejo-API Konfiguration` bearbeitet. Das Programm startet mit
Administratorrechten und verwaltet Datenbankverbindungen, CDN-Pfad, HTTP-Kompatibilitaetsmodus, HTTPS-Port,
API-Kennwort und den optionalen Zertifikat-Fingerabdruck. Das vorhandene API-Kennwort
wird nicht angezeigt. Zum Aendern wird das neue Kennwort zweimal eingegeben. Nach dem
Speichern kann der Dienst direkt
neu gestartet werden. Die Datei darf nicht auf einen anderen Rechner kopiert werden,
weil ihre DPAPI-Verschluesselung an den API-Rechner gebunden ist.

## HTTPS ab Version 1.0.5

Ab Version 1.0.9 ist HTTP auf Port 5298 standardmaessig deaktiviert und die bisherige
Firewallregel wird bei Installation oder Upgrade entfernt. Die API ist unter
`https://<API-Server>:5299/health` erreichbar. Es gibt keine HTTP-Umleitung.
Jeder Aufruf muss den Klartext-Header `X-Euvejo-Api-Password` enthalten. Der Header ist
nur bei HTTPS transportverschluesselt; HTTP darf daher nicht produktiv verwendet werden.
Die Datenbank muss wie bisher erreichbar sein, bevor die API startet.

Das MSI erstellt beim ersten Start ein selbstsigniertes, zwei Jahre gueltiges
Serverzertifikat mit Rechnername, localhost und den aktuellen IP-Adressen.
Der private Schluessel bleibt nicht exportierbar in `LocalMachine\My`.
Er befindet sich weder im MSI noch im Quellcode. Bei Updates bleibt das Zertifikat erhalten.
Ab Version 1.0.8 prueft die API das verwaltete Standardzertifikat alle 12 Stunden und
erneuert es 60 Tage vor Ablauf automatisch. Der geschuetzte private Schluessel bleibt
dabei erhalten. Das aktuelle oeffentliche Zertifikat wird automatisch exportiert.

Das oeffentliche Zertifikat steht unter
`C:\ProgramData\Euvejo\Euvejo-Api\https-certificate.cer`.
Kopieren Sie dieses oeffentliche Zertifikat auf den Euvejo-Client. Waehlen Sie es
dort unter `Konfiguration > Arztdaten > Zertifikat auswaehlen` aus. Euvejo prueft
das Zertifikat und die HTTPS-Verbindung, bevor es den Vertrauensanker speichert.
Das Zertifikat enthaelt keinen privaten Schluessel.
Alternativ ein Zertifikat der eigenen Praxis-CA in `LocalMachine\My` importieren
und dessen Fingerabdruck konfigurieren. Keine Zertifikatspruefung deaktivieren.
Euvejo ab Build 1143 verwendet HTTPS auf Port 5299. Ab Build 1148 erkennt Euvejo ein
erneuertes Serverzertifikat automatisch an, wenn es mit demselben fest hinterlegten
Server-Schluessel ausgestellt wurde und Hostname, Laufzeit und Server-EKU gueltig sind.
Ein Zertifikat mit fremdem Schluessel bleibt gesperrt. Ab Build 1144 sendet Euvejo das
in der Client-Konfiguration hinterlegte API-Kennwort bei jedem Aufruf. Aeltere Client-Builds,
die nur HTTP verwenden, koennen die API 1.0.9 in der Standardkonfiguration nicht erreichen.

Die folgenden Netzwerkwerte sind im Konfigurationsprogramm voreingestellt:

```json
"Http": {
  "Enabled": false,
  "Port": 5298
},
"Https": {
  "Enabled": true,
  "Port": 5299,
  "CertificateStore": "LocalMachine",
  "CertificateThumbprint": ""
}
```

Der HTTP-Code und Port 5298 bleiben fuer einen notwendigen Kompatibilitaetsbetrieb erhalten.
HTTP kann im Konfigurationsprogramm bewusst aktiviert werden; dabei wird die auf das lokale
Subnetz begrenzte Firewallregel wieder angelegt. Das API-Kennwort ist ueber HTTP unverschluesselt
transportierbar, weshalb diese Option nicht fuer den regulaeren Betrieb vorgesehen ist.
Mindestens eines der beiden Protokolle muss aktiviert bleiben.
