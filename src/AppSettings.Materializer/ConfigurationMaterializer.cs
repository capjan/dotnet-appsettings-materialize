using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AppSettings.Materializer;

/// <summary>
/// Materialisiert dieselben effektiven Schlüssel, die die JSON-Konfigurationsprovider liefern.
/// </summary>
public sealed class ConfigurationMaterializer
{
    private const string KeyDelimiter = ":";
    private const int MacExtendedAclType = 0x00000100;
    private const int MacAclFirstEntry = 0;
    private const int ErrorNoEntry = 2;
    private const int LinuxErrorNoData = 61;
    private const int MacErrorNotSupported = 45;
    private const int MacErrorOperationNotSupported = 102;
    private const int LinuxErrorNotSupported = 95;
    private const int ErrorAlreadyExists = 17;
    private const int MacExtendedSecurityPathConf = 13;
    private const uint PrivateUnixDirectoryMode = (uint)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    private const string LinuxAclAttribute = "system.posix_acl_access";
    private const string LinuxDefaultAclAttribute = "system.posix_acl_default";

    /// <summary>
    /// Lädt die Eingabelayer, rekonstruiert die effektive Hierarchie und validiert den Roundtrip.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Die Instanz-API ist für spätere austauschbare Materializer-Implementierungen bewusst nicht statisch.")]
    public MaterializerResult Materialize(MaterializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var inputFiles = ValidateAndNormalizeInputFiles(options, out var warnings);
        var outputFile = Path.GetFullPath(options.OutputFile);

        if (inputFiles.Skip(1).Any(path => PathsEqual(path, outputFile)))
        {
            throw new MaterializerException("Die Ausgabedatei darf nicht gleichzeitig als nachgelagerte Eingabedatei verwendet werden.");
        }

        var rawFiles = inputFiles.Select(ParseRawFile).ToArray();
        var configuration = BuildConfiguration(inputFiles);

        var effectiveEntries = ReadConfigurationEntries(configuration);
        var tree = BuildTree(effectiveEntries, rawFiles);
        var outputBytes = Serialize(tree, options.PrettyPrint, options.SortProperties);

        ValidateRoundtrip(configuration, outputBytes);

        if (!options.CheckOnly)
        {
            WriteAtomically(outputFile, outputBytes, options.Overwrite);
            ValidateRoundtripFile(configuration, outputFile);
        }

        return new MaterializerResult
        {
            InputFileCount = inputFiles.Count,
            InputFileNames = inputFiles,
            OutputFile = outputFile,
            EffectiveKeyCount = effectiveEntries.Count,
            OutputSha256 = Convert.ToHexString(SHA256.HashData(outputBytes)).ToLowerInvariant(),
            Warnings = warnings,
            WasWritten = !options.CheckOnly
        };
    }

    private static List<string> ValidateAndNormalizeInputFiles(MaterializerOptions options, out List<string> warnings)
    {
        warnings = new List<string>();

        if (options.InputFiles is null || options.InputFiles.Count == 0)
        {
            throw new MaterializerException("Mindestens eine Eingabedatei ist erforderlich.");
        }

        if (string.IsNullOrWhiteSpace(options.OutputFile))
        {
            throw new MaterializerException("Eine Ausgabedatei ist erforderlich.");
        }

        var paths = new List<string>(options.InputFiles.Count);
        foreach (var inputFile in options.InputFiles)
        {
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                throw new MaterializerException("Leere Eingabepfade sind nicht zulässig.");
            }

            var path = Path.GetFullPath(inputFile);
            if (!File.Exists(path))
            {
                if (options.AllowMissingFiles)
                {
                    warnings.Add($"Eingabedatei übersprungen: {Path.GetFileName(path)}");
                    continue;
                }

                throw new MaterializerException($"Eingabedatei wurde nicht gefunden: {Path.GetFileName(path)}");
            }

            paths.Add(path);
        }

        if (paths.Count == 0)
        {
            throw new MaterializerException("Keine vorhandene Eingabedatei zum Verarbeiten gefunden.");
        }

        return paths;
    }

    private static IConfigurationRoot BuildConfiguration(List<string> inputFiles)
    {
        var builder = new ConfigurationBuilder();
        var streams = new List<Stream>(inputFiles.Count);

        try
        {
            foreach (var inputFile in inputFiles)
            {
                var stream = File.OpenRead(inputFile);
                streams.Add(stream);
                builder.AddJsonStream(stream);
            }

            return builder.Build();
        }
        catch (JsonException exception)
        {
            DisposeStreams(streams);
            throw new MaterializerException("Eine Eingabedatei enthält ungültiges JSON.", exception);
        }
        catch (InvalidDataException exception)
        {
            DisposeStreams(streams);
            throw new MaterializerException("Eine Eingabedatei konnte nicht als JSON-Konfiguration gelesen werden.", exception);
        }
        catch
        {
            DisposeStreams(streams);
            throw;
        }
        finally
        {
            DisposeStreams(streams);
        }
    }

    private static void DisposeStreams(IEnumerable<Stream> streams)
    {
        foreach (var stream in streams)
        {
            stream.Dispose();
        }
    }

    private static List<ConfigurationEntry> ReadConfigurationEntries(IConfiguration configuration)
    {
        var entries = new List<ConfigurationEntry>();

        foreach (var entry in configuration.AsEnumerable(true))
        {
            entries.Add(new ConfigurationEntry(entry.Key, entry.Value));
        }

        return entries;
    }

    private static RawFile ParseRawFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new MaterializerException("Jede Eingabedatei muss ein JSON-Objekt als Wurzel enthalten.");
            }

            var values = new Dictionary<string, RawValue>(StringComparer.OrdinalIgnoreCase);
            var emptyShapes = new Dictionary<string, EmptyShape>(StringComparer.OrdinalIgnoreCase);
            VisitObject(document.RootElement, prefix: null, values, emptyShapes);
            return new RawFile(values, emptyShapes);
        }
        catch (MaterializerException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new MaterializerException($"Die Eingabedatei enthält ungültiges JSON: {Path.GetFileName(path)}", exception);
        }
        catch (IOException exception)
        {
            throw new MaterializerException($"Die Eingabedatei konnte nicht gelesen werden: {Path.GetFileName(path)}", exception);
        }
    }

    private static void VisitObject(
        JsonElement element,
        string? prefix,
        IDictionary<string, RawValue> values,
        IDictionary<string, EmptyShape> emptyShapes)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = Combine(prefix, property.Name);
            VisitValue(key, property.Value, values, emptyShapes);
        }
    }

    private static void VisitArray(
        string key,
        JsonElement element,
        IDictionary<string, RawValue> values,
        IDictionary<string, EmptyShape> emptyShapes)
    {
        if (element.GetArrayLength() == 0)
        {
            emptyShapes[key] = new EmptyShape(JsonValueKind.Array);
            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            VisitValue(Combine(key, index.ToString(CultureInfo.InvariantCulture)), item, values, emptyShapes);
            index++;
        }
    }

    private static void VisitValue(
        string key,
        JsonElement element,
        IDictionary<string, RawValue> values,
        IDictionary<string, EmptyShape> emptyShapes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!element.EnumerateObject().Any())
                {
                    emptyShapes[key] = new EmptyShape(JsonValueKind.Object);
                }
                else
                {
                    VisitObject(element, key, values, emptyShapes);
                }

                break;
            case JsonValueKind.Array:
                VisitArray(key, element, values, emptyShapes);
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                values[key] = new RawValue(element.Clone());
                break;
            default:
                throw new MaterializerException("Die Eingabedatei enthält einen nicht unterstützten JSON-Wert.");
        }
    }

    private static MaterializedNode BuildTree(
        IReadOnlyList<ConfigurationEntry> effectiveEntries,
        IReadOnlyList<RawFile> rawFiles)
    {
        var latestRawValues = new Dictionary<string, RawValue>(StringComparer.OrdinalIgnoreCase);
        var latestEmptyShapes = new Dictionary<string, EmptyShape>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawFile in rawFiles)
        {
            foreach (var pair in rawFile.Values)
            {
                latestEmptyShapes.Remove(pair.Key);
                latestRawValues[pair.Key] = pair.Value;
            }

            foreach (var pair in rawFile.EmptyShapes)
            {
                latestRawValues.Remove(pair.Key);
                latestEmptyShapes[pair.Key] = pair.Value;
            }
        }

        var root = new MaterializedNode();
        foreach (var entry in effectiveEntries)
        {
            var node = GetOrAddPath(root, entry.Key);
            if (node.HasScalar)
            {
                continue;
            }

            if (latestRawValues.TryGetValue(entry.Key, out var rawValue))
            {
                node.Scalar = rawValue;
            }
        }

        foreach (var shape in latestEmptyShapes)
        {
            var node = GetOrAddPath(root, shape.Key);
            if (!node.HasScalar && node.Children.Count == 0)
            {
                node.EmptyShape = shape.Value;
            }
        }

        ValidateTree(root);
        return root;
    }

    private static MaterializedNode GetOrAddPath(MaterializedNode root, string key)
    {
        var current = root;
        foreach (var segment in key.Split(KeyDelimiter, StringSplitOptions.None))
        {
            current = current.GetOrAddChild(segment);
        }

        return current;
    }

    private static void ValidateTree(MaterializedNode node)
    {
        if (node.HasScalar && node.Children.Count > 0)
        {
            throw new MaterializerException(
                "Die effektive Konfiguration enthält einen nicht als einzelnes JSON-Dokument darstellbaren Skalar-/Objektkonflikt.");
        }

        if (node.Children.Count > 0)
        {
            var numericSegments = node.Children.Keys.All(segment => int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0);
            if (numericSegments)
            {
                var indexes = node.Children.Keys
                    .Select(segment => int.Parse(segment, CultureInfo.InvariantCulture))
                    .OrderBy(index => index)
                    .ToArray();

                for (var expected = 0; expected < indexes.Length; expected++)
                {
                    if (indexes[expected] != expected)
                    {
                        throw new MaterializerException("Die effektive Konfiguration enthält eine Lücke in einem Array-Index.");
                    }
                }
            }

            foreach (var child in node.Children.Values)
            {
                ValidateTree(child);
            }
        }
    }

    private static byte[] Serialize(MaterializedNode root, bool prettyPrint, bool sortProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = prettyPrint,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
                   }))
        {
            WriteNode(root, writer, sortProperties, isRoot: true);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteNode(MaterializedNode node, Utf8JsonWriter writer, bool sortProperties, bool isRoot = false)
    {
        if (node.HasScalar)
        {
            node.Scalar!.Value.WriteTo(writer);
            return;
        }

        if (node.Children.Count == 0 && !isRoot && node.EmptyShape is { } emptyShape)
        {
            if (emptyShape.Kind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            return;
        }

        var isArray = node.Children.Count > 0 && node.Children.Keys.All(segment => int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0);
        if (isArray)
        {
            writer.WriteStartArray();
            foreach (var child in node.Children.OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture)))
            {
                WriteNode(child.Value, writer, sortProperties);
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        IEnumerable<KeyValuePair<string, MaterializedNode>> children = sortProperties
            ? node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            : node.ChildrenInInsertionOrder;

        foreach (var child in children)
        {
            writer.WritePropertyName(child.Key);
            WriteNode(child.Value, writer, sortProperties);
        }

        writer.WriteEndObject();
    }

    private static void ValidateRoundtrip(IConfiguration source, byte[] outputBytes)
    {
        using var stream = new MemoryStream(outputBytes, writable: false);
        var output = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var sourceEntries = ToDictionary(source);
        var outputEntries = ToDictionary(output);

        if (sourceEntries.Count != outputEntries.Count || sourceEntries.Any(pair => !outputEntries.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.Ordinal)))
        {
            throw new MaterializerException("Die erzeugte JSON-Datei besteht die IConfiguration-Roundtrip-Prüfung nicht.");
        }
    }

    private static void ValidateRoundtripFile(IConfiguration source, string outputFile)
    {
        try
        {
            var output = new ConfigurationBuilder()
                .AddJsonFile(outputFile, optional: false, reloadOnChange: false)
                .Build();
            var sourceEntries = ToDictionary(source);
            var outputEntries = ToDictionary(output);

            if (sourceEntries.Count != outputEntries.Count || sourceEntries.Any(pair => !outputEntries.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.Ordinal)))
            {
                throw new MaterializerException("Die geschriebene JSON-Datei besteht die IConfiguration-Roundtrip-Prüfung nicht.");
            }
        }
        catch (MaterializerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            throw new MaterializerException("Die geschriebene JSON-Datei konnte nicht erneut geladen werden.", exception);
        }
    }

    private static Dictionary<string, string?> ToDictionary(IConfiguration configuration)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.AsEnumerable(true))
        {
            result[entry.Key] = entry.Value;
        }

        return result;
    }

    private static void WriteAtomically(string outputFile, byte[] content, bool overwrite)
    {
        var directory = Path.GetDirectoryName(outputFile);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new MaterializerException("Das Zielverzeichnis der Ausgabedatei existiert nicht.");
        }

        var outputExists = File.Exists(outputFile);
        if (!overwrite && outputExists)
        {
            throw new MaterializerException("Die Ausgabedatei existiert bereits. Verwende --overwrite, um sie zu ersetzen.");
        }

        var temporaryDirectory = directory;
        var temporaryFile = string.Empty;
        var unixMode = GetTemporaryUnixMode(outputFile, outputExists);
        var windowsSecurity = OperatingSystem.IsWindows()
            ? GetTemporaryWindowsSecurity(outputFile)
            : null;

        try
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                temporaryDirectory = CreatePrivateUnixTemporaryDirectory(directory);
            }

            var temporaryFileName = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()
                ? $".appsettings-materializer-{Guid.NewGuid():N}.tmp"
                : $".{Path.GetFileName(outputFile)}.{Guid.NewGuid():N}.tmp";
            temporaryFile = Path.Combine(temporaryDirectory, temporaryFileName);

            using (var stream = CreateTemporaryFile(temporaryFile, windowsSecurity))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                stream.Write(content);
                stream.Flush(flushToDisk: true);

                if (outputExists && OperatingSystem.IsMacOS())
                {
                    CopyMacAcl(outputFile, temporaryFile);
                }
                else if (outputExists && OperatingSystem.IsLinux())
                {
                    CopyLinuxAcl(outputFile, temporaryFile);
                }

                if (!OperatingSystem.IsWindows() && unixMode is { } finalMode)
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, finalMode);
                }

                stream.Flush(flushToDisk: true);
            }

            if (OperatingSystem.IsWindows() && overwrite && File.Exists(outputFile))
            {
                File.Replace(temporaryFile, outputFile, destinationBackupFileName: null);
            }
            else
            {
                // Keep the no-overwrite operation atomic. In particular, do not
                // turn a destination that appeared while writing the temporary
                // file into an implicit replacement on Windows.
                File.Move(temporaryFile, outputFile, overwrite);
            }
        }
        catch (IOException exception)
        {
            throw new MaterializerException($"Die Ausgabedatei konnte nicht atomar geschrieben werden: {exception.Message}", exception);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryFile))
            {
                TryDeleteTemporaryFile(temporaryFile);
            }

            if (!OperatingSystem.IsWindows() && temporaryDirectory != directory)
            {
                TryDeleteTemporaryDirectory(temporaryDirectory);
            }
        }
    }

    private static UnixFileMode? GetTemporaryUnixMode(string outputFile, bool outputExists)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return outputExists
            ? File.GetUnixFileMode(outputFile)
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    private static FileStream CreateTemporaryFile(
        string path,
        FileSecurity? windowsSecurity)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsTemporaryFile(path, windowsSecurity!);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.SequentialScan,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };

        return new FileStream(path, options);
    }

    [UnsupportedOSPlatform("windows")]
    private static string CreatePrivateUnixTemporaryDirectory(string parentDirectory)
    {
        while (true)
        {
            var path = Path.Combine(parentDirectory, $".appsettings-materializer-{Guid.NewGuid():N}.tmp");
            int result;
            if (OperatingSystem.IsMacOS())
            {
                result = MacCreateDirectory(path, PrivateUnixDirectoryMode);
            }
            else if (OperatingSystem.IsLinux())
            {
                result = LinuxCreateDirectory(path, PrivateUnixDirectoryMode);
            }
            else
            {
                throw new PlatformNotSupportedException("Private temporäre Verzeichnisse werden auf dieser Plattform nicht unterstützt.");
            }

            if (result != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorAlreadyExists)
                {
                    continue;
                }

                throw new IOException("Ein privates temporäres Verzeichnis konnte nicht erstellt werden.", new Win32Exception(error));
            }

            try
            {
                var preserveLinuxSetGroup = OperatingSystem.IsLinux() &&
                    (File.GetUnixFileMode(path) & UnixFileMode.SetGroup) != 0;
                if (preserveLinuxSetGroup)
                {
                    var ownerPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                    if ((File.GetUnixFileMode(path) & ownerPermissions) != ownerPermissions)
                    {
                        throw new IOException("Die umask verhindert den privaten Zugriff auf das temporäre Verzeichnis.");
                    }
                }
                else
                {
                    File.SetUnixFileMode(path, (UnixFileMode)PrivateUnixDirectoryMode);
                }

                if (OperatingSystem.IsMacOS())
                {
                    CopyMacAcl(null, path);
                }
                else if (OperatingSystem.IsLinux())
                {
                    CopyLinuxAcl(null, path);
                    RemoveLinuxAcl(path, LinuxDefaultAclAttribute);
                }

                if (!preserveLinuxSetGroup)
                {
                    File.SetUnixFileMode(path, (UnixFileMode)PrivateUnixDirectoryMode);
                }

                return path;
            }
            catch
            {
                TryDeleteTemporaryDirectory(path);
                throw;
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static FileSecurity? GetTemporaryWindowsSecurity(string outputFile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetWindowsFileSecurity(outputFile);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity GetWindowsFileSecurity(string outputFile)
    {
        if (File.Exists(outputFile))
        {
            return new FileInfo(outputFile).GetAccessControl(AccessControlSections.Access);
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsTemporaryFile(string path, FileSecurity security)
    {
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.SequentialScan,
            fileSecurity: security);
    }

    [SupportedOSPlatform("macos")]
    private static void ClearMacAcl(string path)
    {
        if (!MacSupportsExtendedSecurity(path))
        {
            return;
        }

        if (MacAclDeleteFile(path, MacExtendedAclType) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is not ErrorNoEntry and not MacErrorNotSupported and not MacErrorOperationNotSupported)
            {
                throw new IOException("Geerbte ACL-Einträge konnten nicht entfernt werden.", new Win32Exception(error));
            }

            var empty = MacAclInit(0);
            if (empty == IntPtr.Zero)
            {
                throw new IOException("Geerbte ACL-Einträge konnten nicht entfernt werden.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            try
            {
                if (MacAclSetFile(path, MacExtendedAclType, empty) != 0)
                {
                    throw new IOException("Geerbte ACL-Einträge konnten nicht entfernt werden.", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }
            finally
            {
                _ = MacAclFree(empty);
            }
        }

        var remaining = MacAclGetFile(path, MacExtendedAclType);
        if (remaining != IntPtr.Zero)
        {
            var hasEntry = MacAclGetEntry(remaining, MacAclFirstEntry, out _) == 0;
            _ = MacAclFree(remaining);
            if (hasEntry)
            {
                throw new IOException("Geerbte ACL-Einträge konnten nicht entfernt werden.");
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private static bool MacSupportsExtendedSecurity(string path)
    {
        Marshal.SetLastPInvokeError(0);
        var result = MacPathConf(path, MacExtendedSecurityPathConf);
        if (result >= 0)
        {
            return result > 0;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != 0)
        {
            throw new IOException("Die ACL-Unterstützung des Dateisystems konnte nicht ermittelt werden.", new Win32Exception(error));
        }

        return true;
    }

    [SupportedOSPlatform("macos")]
    private static void CopyMacAcl(string? sourceFile, string temporaryFile)
    {
        var acl = sourceFile is null
            ? IntPtr.Zero
            : MacAclGetFile(sourceFile, MacExtendedAclType);

        if (acl == IntPtr.Zero)
        {
            var error = sourceFile is null ? 0 : Marshal.GetLastPInvokeError();
            if (error is not 0 and not ErrorNoEntry and not MacErrorNotSupported and not MacErrorOperationNotSupported)
            {
                throw new IOException("Die ACL der bestehenden Ausgabedatei konnte nicht gelesen werden.", new Win32Exception(error));
            }

            ClearMacAcl(temporaryFile);

            return;
        }

        try
        {
            if (MacAclSetFile(temporaryFile, MacExtendedAclType, acl) != 0)
            {
                throw new IOException("Die ACL der bestehenden Ausgabedatei konnte nicht übernommen werden.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = MacAclFree(acl);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void CopyLinuxAcl(string? sourceFile, string temporaryFile)
    {
        if (sourceFile is null)
        {
            RemoveLinuxAcl(temporaryFile, LinuxAclAttribute);
            return;
        }

        var size = LinuxGetXAttr(sourceFile, LinuxAclAttribute, IntPtr.Zero, 0);
        if (size < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is LinuxErrorNoData or LinuxErrorNotSupported)
            {
                RemoveLinuxAcl(temporaryFile, LinuxAclAttribute);
                return;
            }

            throw new IOException("Die ACL der bestehenden Ausgabedatei konnte nicht gelesen werden.", new Win32Exception(error));
        }

        var acl = new byte[checked((int)size)];
        var bytesRead = LinuxGetXAttr(sourceFile, LinuxAclAttribute, acl, (nuint)acl.Length);
        if (bytesRead < 0)
        {
            throw new IOException("Die ACL der bestehenden Ausgabedatei konnte nicht gelesen werden.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (bytesRead != size)
        {
            Array.Resize(ref acl, checked((int)bytesRead));
        }

        if (LinuxSetXAttr(temporaryFile, LinuxAclAttribute, acl, (nuint)acl.Length, 0) != 0)
        {
            throw new IOException("Die ACL der bestehenden Ausgabedatei konnte nicht übernommen werden.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [SupportedOSPlatform("linux")]
    private static void RemoveLinuxAcl(string path, string aclAttribute)
    {
        if (LinuxRemoveXAttr(path, aclAttribute) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is not LinuxErrorNoData and not LinuxErrorNotSupported)
            {
                throw new IOException("Geerbte ACL-Einträge konnten nicht entfernt werden.", new Win32Exception(error));
            }
        }
    }

    [SupportedOSPlatform("macos")]
    [DllImport("libSystem.B.dylib", EntryPoint = "mkdir", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int MacCreateDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", EntryPoint = "mkdir", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int LinuxCreateDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_get_file", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern IntPtr MacAclGetFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);

    [DllImport("libSystem.B.dylib", EntryPoint = "pathconf", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern nint MacPathConf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_set_file", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int MacAclSetFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type, IntPtr acl);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_delete_file_np", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int MacAclDeleteFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_init", SetLastError = true)]
    private static extern IntPtr MacAclInit(int count);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_get_entry", SetLastError = true)]
    private static extern int MacAclGetEntry(IntPtr acl, int entryId, out IntPtr entry);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_free", SetLastError = true)]
    private static extern int MacAclFree(IntPtr acl);

    [DllImport("libc", EntryPoint = "getxattr", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern nint LinuxGetXAttr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        IntPtr value,
        nuint size);

    [DllImport("libc", EntryPoint = "getxattr", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern nint LinuxGetXAttr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [Out] byte[] value,
        nuint size);

    [DllImport("libc", EntryPoint = "setxattr", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int LinuxSetXAttr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [In] byte[] value,
        nuint size,
        int flags);

    [DllImport("libc", EntryPoint = "removexattr", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int LinuxRemoveXAttr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    private static string Combine(string? prefix, string segment)
    {
        return string.IsNullOrEmpty(prefix) ? segment : $"{prefix}{KeyDelimiter}{segment}";
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record ConfigurationEntry(string Key, string? Value);

    private sealed record RawFile(
        IReadOnlyDictionary<string, RawValue> Values,
        IReadOnlyDictionary<string, EmptyShape> EmptyShapes);

    private sealed record RawValue(JsonElement Value);

    private sealed record EmptyShape(JsonValueKind Kind);

    private sealed class MaterializedNode
    {
        private readonly Dictionary<string, MaterializedNode> children = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, MaterializedNode>> insertionOrder = new();

        public IReadOnlyDictionary<string, MaterializedNode> Children => children;

        public IReadOnlyList<KeyValuePair<string, MaterializedNode>> ChildrenInInsertionOrder => insertionOrder;

        public RawValue? Scalar { get; set; }

        public bool HasScalar => Scalar is not null;

        public EmptyShape? EmptyShape { get; set; }

        public MaterializedNode GetOrAddChild(string key)
        {
            if (children.TryGetValue(key, out var child))
            {
                return child;
            }

            child = new MaterializedNode();
            children.Add(key, child);
            insertionOrder.Add(new KeyValuePair<string, MaterializedNode>(key, child));
            return child;
        }
    }
}
