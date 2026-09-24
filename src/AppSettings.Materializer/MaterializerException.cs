namespace AppSettings.Materializer;

/// <summary>
/// Ein erwarteter, nicht vertraulicher Fehler bei der Materialisierung.
/// </summary>
public sealed class MaterializerException : Exception
{
    /// <summary>
    /// Erstellt einen erwarteten Materialisierungsfehler.
    /// </summary>
    public MaterializerException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Erstellt einen erwarteten Materialisierungsfehler mit einer technischen Ursache.
    /// </summary>
    public MaterializerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
