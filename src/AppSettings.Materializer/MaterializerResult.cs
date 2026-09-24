namespace AppSettings.Materializer;

/// <summary>
/// Nicht vertrauliche Metadaten einer Materialisierung.
/// </summary>
public sealed record MaterializerResult
{
    /// <summary>
    /// Anzahl der verarbeiteten Eingabedateien.
    /// </summary>
    public required int InputFileCount { get; init; }

    /// <summary>
    /// Vollständig normalisierte Eingabepfade.
    /// </summary>
    public required IReadOnlyList<string> InputFileNames { get; init; }

    /// <summary>
    /// Vollständig normalisierter Ausgabepfad.
    /// </summary>
    public required string OutputFile { get; init; }

    /// <summary>
    /// Anzahl der von IConfiguration gelieferten effektiven Schlüssel.
    /// </summary>
    public required int EffectiveKeyCount { get; init; }

    /// <summary>
    /// SHA-256-Hash der erzeugten Bytes in Kleinbuchstaben.
    /// </summary>
    public required string OutputSha256 { get; init; }

    /// <summary>
    /// Nicht vertrauliche Hinweise zur Verarbeitung.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Gibt an, ob die Ausgabedatei geschrieben wurde.
    /// </summary>
    public required bool WasWritten { get; init; }
}
