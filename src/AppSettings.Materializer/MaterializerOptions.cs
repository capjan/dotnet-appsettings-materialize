namespace AppSettings.Materializer;

/// <summary>
/// Fachliche Optionen für die Materialisierung mehrerer JSON-Konfigurationslayer.
/// </summary>
public sealed record MaterializerOptions
{
    /// <summary>
    /// JSON-Dateien in Prioritätsreihenfolge. Die letzte Datei hat Vorrang.
    /// </summary>
    public required IReadOnlyList<string> InputFiles { get; init; }

    /// <summary>
    /// Zielpfad der materialisierten JSON-Datei.
    /// </summary>
    public required string OutputFile { get; init; }

    /// <summary>
    /// Erlaubt das Ersetzen einer vorhandenen Ausgabedatei.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Schreibt eingerücktes JSON statt kompakter JSON-Ausgabe.
    /// </summary>
    public bool PrettyPrint { get; init; }

    /// <summary>
    /// Sortiert Objekteigenschaften für deterministische Ausgaben.
    /// </summary>
    public bool SortProperties { get; init; } = true;

    /// <summary>
    /// Validiert vorhandene Eingaben, schreibt aber keine Datei.
    /// </summary>
    public bool CheckOnly { get; init; }

    /// <summary>
    /// Erlaubt fehlende Eingabedateien. Fehlende Dateien werden dann übersprungen.
    /// </summary>
    public bool AllowMissingFiles { get; init; }
}
