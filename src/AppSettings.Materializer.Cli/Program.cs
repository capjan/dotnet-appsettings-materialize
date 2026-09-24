using AppSettings.Materializer;

namespace AppSettings.Materializer.Cli;

/// <summary>
/// Einstiegspunkt des globalen CLI-Tools.
/// </summary>
public static class Program
{
    /// <summary>
    /// Verarbeitet den CLI-Aufruf und gibt einen Prozess-Exitcode zurück.
    /// </summary>
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (MaterializerException exception)
        {
            Console.Error.WriteLine($"Fehler: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Fehler: Zugriff auf eine Eingabe- oder Ausgabedatei verweigert.");
            return 1;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Fehler: Eine Eingabe- oder Ausgabedatei konnte nicht verarbeitet werden.");
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"Fehler: Unerwarteter {exception.GetType().Name}.");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        if (!string.Equals(args[0], "merge", StringComparison.Ordinal))
        {
            throw new MaterializerException("Der einzige unterstützte Befehl ist 'merge'.");
        }

        var parsed = ParseMergeArguments(args[1..]);
        var inputFiles = ResolveInputFiles(parsed);
        var outputFile = parsed.OutputFile ?? throw new MaterializerException("--output ist erforderlich.");

        var result = new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = inputFiles,
                OutputFile = outputFile,
                Overwrite = parsed.Overwrite,
                PrettyPrint = parsed.PrettyPrint,
                SortProperties = !parsed.NoSort,
                CheckOnly = parsed.CheckOnly
            });

        if (parsed.Verbose)
        {
            var action = result.WasWritten ? "geschrieben" : "validiert";
            Console.WriteLine($"{result.InputFileCount} Eingabedatei(en) verarbeitet; {result.EffectiveKeyCount} effektive Schlüssel {action}.");
            Console.WriteLine($"Ausgabe: {result.OutputFile}");
            Console.WriteLine($"SHA-256: {result.OutputSha256}");
        }

        return 0;
    }

    private static ParsedArguments ParseMergeArguments(string[] args)
    {
        var inputs = new List<string>();
        string? directory = null;
        string? environment = null;
        string? outputFile = null;
        var overwrite = false;
        var checkOnly = false;
        var prettyPrint = false;
        var noSort = false;
        var verbose = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--input":
                    inputs.Add(ReadValue(args, ref index, argument));
                    break;
                case "--output":
                    outputFile = ReadValue(args, ref index, argument);
                    break;
                case "--directory":
                    directory = ReadValue(args, ref index, argument);
                    break;
                case "--environment":
                    environment = ReadValue(args, ref index, argument);
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--check":
                    checkOnly = true;
                    break;
                case "--pretty":
                    prettyPrint = true;
                    break;
                case "--no-sort":
                    noSort = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    throw new MaterializerException($"Unbekannte Option: {argument}");
            }
        }

        if (inputs.Count > 0 && directory is not null)
        {
            throw new MaterializerException("--input und --directory können nicht gemeinsam verwendet werden.");
        }

        if (environment is not null && directory is null)
        {
            throw new MaterializerException("--environment erfordert --directory.");
        }

        if (directory is null && inputs.Count == 0)
        {
            throw new MaterializerException("Mindestens ein --input oder --directory ist erforderlich.");
        }

        return new ParsedArguments(inputs, directory, environment, outputFile, overwrite, checkOnly, prettyPrint, noSort, verbose);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new MaterializerException($"Für {option} ist ein Wert erforderlich.");
        }

        return args[index];
    }

    private static IReadOnlyList<string> ResolveInputFiles(ParsedArguments arguments)
    {
        if (arguments.Directory is null)
        {
            return arguments.Inputs;
        }

        var directory = Path.GetFullPath(arguments.Directory);
        if (!Directory.Exists(directory))
        {
            throw new MaterializerException($"Eingabeverzeichnis wurde nicht gefunden: {Path.GetFileName(directory)}");
        }

        if (arguments.Environment is not null && !IsSafeEnvironmentName(arguments.Environment))
        {
            throw new MaterializerException("--environment enthält ungültige Zeichen.");
        }

        var files = new List<string> { Path.Combine(directory, "appsettings.json") };
        if (arguments.Environment is not null)
        {
            files.Add(Path.Combine(directory, $"appsettings.{arguments.Environment}.json"));
        }

        return files;
    }

    private static bool IsSafeEnvironmentName(string environment)
    {
        if (string.IsNullOrWhiteSpace(environment) || environment.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (environment.Contains(Path.DirectorySeparatorChar) || environment.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        return environment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("appsettings-materialize merge --input <file> [--input <file> ...] --output <file> [options]");
        Console.WriteLine("appsettings-materialize merge --directory <directory> [--environment <name>] --output <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Optionen:");
        Console.WriteLine("  --overwrite   Vorhandene Ausgabedatei ersetzen");
        Console.WriteLine("  --check       Nur validieren, keine Datei schreiben");
        Console.WriteLine("  --pretty      Formatierte JSON-Ausgabe");
        Console.WriteLine("  --no-sort     Eigenschaftsreihenfolge der ermittelten Struktur beibehalten");
        Console.WriteLine("  --verbose     Technische Metadaten ausgeben, keine Konfigurationswerte");
    }

    private sealed record ParsedArguments(
        IReadOnlyList<string> Inputs,
        string? Directory,
        string? Environment,
        string? OutputFile,
        bool Overwrite,
        bool CheckOnly,
        bool PrettyPrint,
        bool NoSort,
        bool Verbose);
}
