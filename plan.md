# Implementierungsplan: `dotnet-appsettings-materialize`

## Ziel des Dokuments

Dieses Dokument beschreibt den Plan für ein eigenständiges .NET-CLI-Tool, das mehrere ASP.NET-Core-Konfigurationsdateien in definierter Reihenfolge verarbeitet und daraus eine einzige effektive JSON-Datei erzeugt.

Das Dokument kann als Arbeitsauftrag an eine andere KI oder an ein Entwicklungsteam übergeben werden.

---

## 1. Ziel des Projekts

Erstelle ein eigenständiges .NET-CLI-Tool, das mehrere ASP.NET-Core-Konfigurationsdateien in definierter Reihenfolge verarbeitet und daraus eine einzige effektive JSON-Datei erzeugt.

Beispiel:

```text
appsettings.json
+ appsettings.Integration.json
→ appsettings.effective.json
```

Das Ergebnis muss anschließend wieder mit dem Standardmechanismus geladen werden können:

```csharp
new ConfigurationBuilder()
    .AddJsonFile("appsettings.effective.json")
    .Build();
```

Die daraus gelesenen Konfigurationsschlüssel und Werte müssen den ursprünglichen Layern entsprechen.

Das Tool soll später als globales .NET Tool über NuGet veröffentlicht werden und in CI/CD-Pipelines verwendbar sein.

## 2. Projektgrenzen

### Das Tool soll

- mehrere JSON-Dateien in definierter Reihenfolge laden
- spätere Dateien gegenüber früheren Dateien priorisieren
- die Semantik von `Microsoft.Extensions.Configuration.Json` berücksichtigen
- Arrays als indexierte Konfigurationsschlüssel behandeln
- verschachtelte Objekte und Arrays wieder als lesbares JSON ausgeben
- deterministische Ausgaben erzeugen
- für Linux, Windows und macOS funktionieren
- als globales .NET-CLI-Tool installierbar sein
- in GitLab CI, GitHub Actions und lokalen Build-Skripten verwendbar sein

### Das Tool soll zunächst nicht

- Environment Variables automatisch in die JSON-Datei übernehmen
- Command-Line-Argumente materialisieren
- Secrets verwalten oder speichern
- Eingabedateien automatisch löschen
- allgemeine RFC-7396- oder `yq`-Merge-Semantik anbieten
- die Konfiguration einer laufenden ASP.NET-Core-Anwendung verändern
- eine eigene Konfigurationshierarchie neben `Microsoft.Extensions.Configuration` erfinden

Environment Variables und Command-Line-Argumente sollen standardmäßig externe Overrides bleiben.

## 3. Repository-Struktur

Erstelle ein neues Repository mit einer klaren Trennung zwischen Kernlogik, CLI und Tests.

Vorschlag:

```text
dotnet-appsettings-materialize/
├── src/
│   ├── AppSettings.Materializer/
│   │   ├── AppSettings.Materializer.csproj
│   │   ├── ConfigurationMaterializer.cs
│   │   ├── MaterializerOptions.cs
│   │   ├── MaterializerResult.cs
│   │   └── ...
│   └── AppSettings.Materializer.Cli/
│       ├── AppSettings.Materializer.Cli.csproj
│       ├── Program.cs
│       └── ...
├── tests/
│   ├── AppSettings.Materializer.Tests/
│   └── AppSettings.Materializer.Cli.Tests/
├── samples/
│   ├── base/
│   ├── integration/
│   ├── stage/
│   └── expected/
├── .github/workflows/
├── README.md
├── LICENSE
├── CHANGELOG.md
├── CONTRIBUTING.md
├── SECURITY.md
└── Directory.Build.props
```

Für das erste Release genügt auch ein CLI-Projekt mit eingebauter Kernlogik. Eine separate Klassenbibliothek ist jedoch sinnvoll, damit die Merge-Logik unabhängig von der Kommandozeile getestet und später wiederverwendet werden kann.

## 4. Technische Zielplattform

Für die erste Version:

- .NET SDK 10.0
- Zielplattform `net10.0`
- nullable reference types aktiviert
- implizite Usings aktiviert
- Treat Warnings as Errors, soweit ohne unnötige Ausnahmen möglich
- zentrale Paketversionen über `Directory.Packages.props`
- keine unnötigen externen JSON-Merge-Bibliotheken

Verwende insbesondere:

```text
Microsoft.Extensions.Configuration
Microsoft.Extensions.Configuration.Json
System.Text.Json
System.CommandLine
```

`System.CommandLine` kann für die CLI verwendet werden. Falls die aktuell verwendete Version instabil oder unnötig komplex ist, ist ein kleiner eigener Argumentparser vorzuziehen.

Eine spätere Erweiterung auf `net8.0` oder Multi-Targeting kann nach dem ersten stabilen Release erfolgen.

## 5. CLI-Benutzeroberfläche

Der globale Toolname soll zunächst lauten:

```text
dotnet-appsettings-materialize
```

Der CLI-Befehl soll lauten:

```text
appsettings-materialize
```

Beispielinstallation:

```bash
dotnet tool install --global dotnet-appsettings-materialize
```

### Minimaler Befehl

```bash
appsettings-materialize merge \
  --input appsettings.json \
  --input appsettings.Integration.json \
  --output appsettings.effective.json
```

Die Reihenfolge der `--input`-Parameter ist verbindlich. Die letzte Datei hat die höchste Priorität.

### Komfortoption für ASP.NET-Core-Konventionen

Zusätzlich soll folgende Form unterstützt werden:

```bash
appsettings-materialize merge \
  --directory ./publish \
  --environment Integration \
  --output ./publish/appsettings.json
```

Das Tool sucht dann standardmäßig:

```text
appsettings.json
appsettings.Integration.json
```

Die explizite Form mit mehreren `--input`-Parametern bleibt die maßgebliche und eindeutigste Variante.

### Vorgeschlagene Optionen

```text
merge
  --input <file>              Mehrfach verwendbar, Reihenfolge ist relevant
  --output <file>             Zieldatei
  --directory <directory>     Basisverzeichnis für relative Pfade
  --environment <name>        Verwendet appsettings.{name}.json
  --overwrite                 Bestehende Ausgabedatei erlauben
  --check                     Nur prüfen, nichts schreiben
  --pretty                    Formatierte JSON-Ausgabe
  --no-sort                   Ursprüngliche bzw. ermittelte Reihenfolge beibehalten
  --verbose                   Nur technische Informationen, keine Konfigurationswerte
```

### Sicherheitsrelevantes Standardverhalten

- Fehlende Eingabedateien führen zu einem Fehler.
- Ungültiges JSON führt zu einem Fehler.
- Die Ausgabedatei wird nicht ohne `--overwrite` überschrieben.
- Eingabedateien werden niemals automatisch gelöscht.
- Keine Konfigurationswerte in Logs ausgeben.
- Keine Secrets in Fehlermeldungen oder Diagnoseausgaben aufnehmen.
- Temporäre Dateien nur im Zielverzeichnis oder einem sicheren temporären Verzeichnis anlegen.
- Ausgabe atomar schreiben: temporäre Datei erzeugen, flushen und anschließend ersetzen.

## 6. Merge-Semantik

Die Implementierung darf nicht einfach `JsonNode.DeepMerge` oder eine beliebige JSON-Merge-Bibliothek verwenden.

Die Semantik muss sich an folgendem Verhalten orientieren:

```csharp
var configuration = new ConfigurationBuilder()
    .AddJsonFile(input1)
    .AddJsonFile(input2)
    .AddJsonFile(input3)
    .Build();
```

Dabei gilt:

- spätere Provider überschreiben frühere Werte
- Konfigurationsschlüssel sind hierarchisch
- JSON-Arrays werden über numerische Segmente abgebildet
- Werte werden durch die Konfiguration als Stringwerte betrachtet
- nicht überschriebene Array-Indizes können aus früheren Providern erhalten bleiben
- Environment Variables und Command-Line-Argumente sind im MVP nicht Bestandteil des Merges

Beispiel:

```json
{
  "AppConfig": {
    "ApiKeyUsers": [
      {
        "UserName": "admin"
      }
    ]
  }
}
```

entspricht in der Konfiguration unter anderem:

```text
AppConfig:ApiKeyUsers:0:UserName = admin
```

### Rekonstruktion der Ausgabe

Das Tool muss aus der effektiven Konfiguration wieder eine verschachtelte JSON-Struktur erzeugen.

Dabei gelten folgende Regeln:

- numerische, zusammenhängende Untersegmente sollen als Arrays ausgegeben werden
- normale Schlüssel sollen als JSON-Objekte ausgegeben werden
- Array-Lücken müssen entweder korrekt als `null` dargestellt oder als nicht unterstützter Sonderfall mit einem klaren Fehler abgelehnt werden
- die erzeugte Datei muss nach dem Schreiben erneut über `ConfigurationBuilder` geladen und validiert werden

Die entscheidende Akzeptanzprüfung ist nicht ausschließlich die optische JSON-Struktur, sondern:

```text
IConfiguration(input layers)
==
IConfiguration(generated output)
```

Verglichen werden alle effektiven Schlüssel und Werte.

### JSON-Datentypen

Die Ausgabe soll nach Möglichkeit die JSON-Typen der jeweils wirksamen Eingabe beibehalten:

```json
{
  "Enabled": true,
  "RetryCount": 3
}
```

Nicht pauschal alle Werte als JSON-Strings schreiben.

Die Konfigurationsgleichheit muss trotzdem über `IConfiguration` geprüft werden, da ASP.NET Core skalare Konfigurationswerte intern als Strings behandelt.

Wenn ein bestimmter Werttyp aufgrund der Layer-Semantik nicht eindeutig erhalten werden kann, ist die korrekte `IConfiguration`-Semantik wichtiger als die ursprüngliche JSON-Darstellung. Diese Einschränkung muss dokumentiert und getestet werden.

## 7. Empfohlene interne Architektur

### `ConfigurationMaterializer`

Verantwortlich für:

- Validierung der Eingabedateien
- Aufbau des `ConfigurationBuilder`
- Ermittlung der effektiven Schlüssel und Werte
- Rekonstruktion des JSON-Baums
- Validierung der erzeugten Ausgabe

### `MaterializerOptions`

Enthält ausschließlich fachliche Optionen:

```text
InputFiles
OutputFile
Overwrite
PrettyPrint
SortProperties
AllowMissingFiles
```

### `MaterializerResult`

Enthält technische Metadaten, aber keine vertraulichen Werte:

```text
InputFileCount
InputFileNames
OutputFile
EffectiveKeyCount
OutputSha256
Warnings
```

Der Hash kann für CI-Logs und Artefaktprüfung ausgegeben werden. Niemals die vollständigen Konfigurationswerte loggen.

## 8. Teststrategie

Die Tests müssen die eigentliche Laufzeitsemantik abdecken und nicht nur einfache Objekt-Merges.

### Pflichtfälle

- einfache Schlüsselüberschreibung
- verschachtelte Objekte
- Objektwerte, die durch Skalare ersetzt werden
- skalare Werte, die durch Objekte ersetzt werden
- Strings
- Booleans
- Ganzzahlen und Dezimalwerte
- `null`
- leere Objekte
- leere Arrays
- Arrays gleicher Länge
- Arrays unterschiedlicher Länge
- Arrays mit Objekten
- Überschreibung einzelner Array-Indizes
- geerbte Array-Indizes
- Array-Lücken
- Groß-/Kleinschreibung von Schlüsseln
- JSON-Kommentare
- trailing commas
- ungültiges JSON
- fehlende Dateien
- case-sensitive Dateinamen auf Linux
- Unicode und deutsche Umlaute
- atomare Ausgabe
- vorhandene Ausgabedatei ohne `--overwrite`

### Golden-/Roundtrip-Tests

Für jedes Szenario:

1. Eingabedateien über `ConfigurationBuilder` laden.
2. Tool ausführen.
3. erzeugte Datei erneut laden.
4. alle Schlüssel und Werte vergleichen.
5. erwartete JSON-Struktur zusätzlich prüfen, sofern die Darstellung eindeutig ist.

### CLI-Integrationstests

Testen:

```bash
appsettings-materialize merge ...
```

einschließlich:

- Exit-Code `0` bei Erfolg
- Exit-Code ungleich `0` bei Fehler
- verständliche Fehlermeldungen
- keine Werte in Logs
- korrekte relative und absolute Pfade
- korrekte Überschreibung nur mit `--overwrite`

## 9. Dokumentation

Die README muss klar zwischen diesen Begriffen unterscheiden:

### Unterstützt

```text
Microsoft.Extensions.Configuration JSON-Layer materialisieren
```

### Nicht unterstützt

```text
RFC-7396 JSON Merge Patch
allgemeines Deep Merge
Array Union
Environment-Variable-Export
Secret Management
```

Die README soll mindestens enthalten:

- Motivation
- Installationsanleitung
- CLI-Beispiele
- Prioritätsregeln
- Array-Semantik
- Verhalten bei `null`
- Umgang mit fehlenden Dateien
- Sicherheitswarnungen
- Beispiele für GitHub Actions
- Beispiele für GitLab CI
- Beispiel für ASP.NET-Core-Publish-Verzeichnisse
- bekannte Einschränkungen
- Lizenz
- Versionierungsstrategie

Beispiel für eine wichtige Dokumentationsaussage:

> Das Tool materialisiert ausschließlich die angegebenen JSON-Provider. Environment Variables, User Secrets und Command-Line-Argumente werden nicht automatisch in die Ausgabedatei übernommen.

## 10. Package und Release

Das CLI-Projekt soll als echtes globales Tool gepackt werden:

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>appsettings-materialize</ToolCommandName>
```

Release-Ablauf:

1. Build
2. Unit Tests
3. CLI-Integrationstests
4. `dotnet pack`
5. Testinstallation aus lokalem NuGet-Feed
6. Smoke-Test des installierten Tools
7. GitHub Release
8. Veröffentlichung auf NuGet

Die Pipeline soll mindestens auf folgenden Plattformen testen:

```text
ubuntu-latest
windows-latest
macos-latest
```

Die Version muss bei jeder Veröffentlichung eindeutig und reproduzierbar sein. Keine Nutzung eines unversionierten `latest`-Pakets in CI-Beispielen.

## 11. Spätere Integration in das MobileCapture-Projekt

Erst nachdem das Tool als stabile Version veröffentlicht wurde, soll es im bestehenden Projekt verwendet werden.

Im MobileCapture-Projekt wird die Toolversion anschließend fest in `.config/dotnet-tools.json` oder über einen vergleichbaren reproduzierbaren Mechanismus eingetragen.

Die spätere Pipeline-Integration soll ungefähr so aussehen:

```text
dotnet publish
→ materialize appsettings.json für Zielumgebung
→ Ergebnis über IConfiguration roundtrip-validieren
→ appsettings.{Environment}.json entfernen
→ ZIP erzeugen
→ Deployment
```

Für das bestehende Projekt gelten zusätzlich:

- `ASPNETCORE_ENVIRONMENT` bleibt erhalten
- Environment Variables bleiben zunächst externe Overrides
- Stage-Secrets werden nicht in das allgemeine Release-Artefakt aufgenommen
- das Stage-Artefakt wird erst nach den bestehenden Stage-Prüfungen materialisiert
- das finale Deployment-Artefakt darf nur eine `appsettings.json` enthalten
- der bisherige Prototyp `Temp/Stage/merge.cs` wird nicht direkt in die Pipeline kopiert, sondern durch das veröffentlichte Tool ersetzt

## 12. Definition of Done

Das Tool gilt als bereit für die erste Veröffentlichung, wenn:

- es als globales Tool installierbar ist
- die CLI auf Linux, Windows und macOS funktioniert
- alle Pflichtfälle getestet sind
- die Roundtrip-Prüfung über `IConfiguration` erfolgreich ist
- Array-Semantik dokumentiert und getestet ist
- keine Eingabedateien ungefragt gelöscht werden
- keine Konfigurationswerte in Logs erscheinen
- das Tool bei Fehlern mit einem nicht erfolgreichen Exit-Code endet
- ein NuGet-Paket erzeugt werden kann
- ein GitHub Release reproduzierbar erstellt werden kann
- ein Beispiel-Pipeline-Skript vorhanden ist
- Lizenz, README, `SECURITY.md` und Changelog vorhanden sind
- bekannte Einschränkungen offen dokumentiert sind

## 13. Bewusst nicht implementieren

Für die erste Version nicht hinzufügen:

- GUI
- Visual-Studio-Erweiterung
- automatische Secret-Erkennung
- Azure Key Vault
- HashiCorp Vault
- Environment-Variable-Export
- JSON Schema Validation
- mehrere konkurrierende Array-Merge-Strategien
- automatische Löschung von Dateien
- Bearbeitung laufender Anwendungen
- projektspezifische Sonderlogik für MobileCapture

Diese Funktionen können später als separate Erweiterungen diskutiert werden.

## Abschluss

Das Tool soll zunächst vollständig unabhängig vom MobileCapture-Repository entwickelt, getestet und veröffentlicht werden. Erst danach wird eine feste Version in der bestehenden Pipeline verwendet.

