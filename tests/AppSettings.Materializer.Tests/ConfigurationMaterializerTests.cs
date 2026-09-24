using System.Diagnostics;
using System.Text.Json;
using AppSettings.Materializer;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AppSettings.Materializer.Tests;

public sealed class ConfigurationMaterializerTests
{
    [Fact]
    public void Materialize_preserves_configuration_values_and_array_inheritance()
    {
        using var files = new TemporaryFiles();
        var baseFile = files.Write(
            "appsettings.json",
            """
            {
              // JSON comments are accepted by the configuration provider.
              "AppConfig": {
                "Enabled": true,
                "RetryCount": 3,
                "Threshold": 1.5,
                "ApiKeyUsers": [
                  { "UserName": "admin", "Role": "base" },
                  { "UserName": "guest" }
                ]
              },
              "Keep": "base",
              "NullValue": null,
              "EmptyObject": {},
              "EmptyArray": [],
              "Unicode": "Grüße"
            }
            """);
        var overrideFile = files.Write(
            "appsettings.Integration.json",
            """
            {
              "AppConfig": {
                "Enabled": false,
                "RetryCount": 5,
                "ApiKeyUsers": [
                  { "Role": "override" },
                ]
              },
              "NewValue": "new"
            }
            """);
        var outputFile = files.PathFor("effective.json");

        var result = new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [baseFile, overrideFile],
                OutputFile = outputFile,
                PrettyPrint = true
            });

        Assert.True(result.WasWritten);
        Assert.True(result.EffectiveKeyCount > 0);
        var source = BuildConfiguration(baseFile, overrideFile);
        var materialized = new ConfigurationBuilder()
            .AddJsonFile(outputFile, optional: false, reloadOnChange: false)
            .Build();

        AssertConfigurationEqual(source, materialized);
        Assert.Equal("Grüße", materialized["Unicode"]);
        Assert.Equal(JsonValueKind.False, ReadJson(outputFile).RootElement.GetProperty("AppConfig").GetProperty("Enabled").ValueKind);
        Assert.Equal(JsonValueKind.Number, ReadJson(outputFile).RootElement.GetProperty("AppConfig").GetProperty("RetryCount").ValueKind);
        Assert.Equal(JsonValueKind.Array, ReadJson(outputFile).RootElement.GetProperty("AppConfig").GetProperty("ApiKeyUsers").ValueKind);
        Assert.Equal(JsonValueKind.Object, ReadJson(outputFile).RootElement.GetProperty("EmptyObject").ValueKind);
        Assert.Equal(JsonValueKind.Array, ReadJson(outputFile).RootElement.GetProperty("EmptyArray").ValueKind);
        Assert.Empty(Directory.GetFiles(files.Directory, ".effective.json.*.tmp"));
    }

    [Fact]
    public void Materialize_rejects_scalar_object_conflicts_that_json_cannot_represent()
    {
        using var files = new TemporaryFiles();
        var baseFile = files.Write("base.json", "{ \"Section\": { \"Child\": \"base\" } }");
        var overrideFile = files.Write("override.json", "{ \"Section\": \"scalar\" }");

        var exception = Assert.Throws<MaterializerException>(() => new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [baseFile, overrideFile],
                OutputFile = files.PathFor("effective.json")
            }));

        Assert.Contains("Skalar-/Objektkonflikt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"Section\": \"base\" }", "{ \"Section\": {} }", JsonValueKind.Object)]
    [InlineData("{ \"Section\": \"base\" }", "{ \"Section\": [] }", JsonValueKind.Array)]
    [InlineData("{ \"Section\": {} }", "{ \"Section\": \"override\" }", JsonValueKind.String)]
    [InlineData("{ \"Section\": [] }", "{ \"Section\": \"override\" }", JsonValueKind.String)]
    public void Materialize_roundtrips_latest_scalar_or_empty_shape_at_same_key(
        string baseJson,
        string overrideJson,
        JsonValueKind expectedKind)
    {
        using var files = new TemporaryFiles();
        var baseFile = files.Write("base.json", baseJson);
        var overrideFile = files.Write("override.json", overrideJson);
        var outputFile = files.PathFor("effective.json");

        new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [baseFile, overrideFile],
                OutputFile = outputFile
            });

        var source = BuildConfiguration(baseFile, overrideFile);
        var materialized = new ConfigurationBuilder()
            .AddJsonFile(outputFile, optional: false, reloadOnChange: false)
            .Build();

        AssertConfigurationEqual(source, materialized);
        Assert.Equal(expectedKind, ReadJson(outputFile).RootElement.GetProperty("Section").ValueKind);
    }

    [Fact]
    public void Materialize_rejects_array_gaps()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Items\": { \"1\": \"only index one\" } }");

        var exception = Assert.Throws<MaterializerException>(() => new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = files.PathFor("effective.json")
            }));

        Assert.Contains("Lücke", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_honors_case_insensitive_configuration_keys()
    {
        using var files = new TemporaryFiles();
        var baseFile = files.Write("base.json", "{ \"Section\": { \"Value\": \"base\" } }");
        var overrideFile = files.Write("override.json", "{ \"section\": { \"value\": \"override\" } }");
        var outputFile = files.PathFor("effective.json");

        new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [baseFile, overrideFile],
                OutputFile = outputFile
            });

        var output = new ConfigurationBuilder().AddJsonFile(outputFile).Build();
        Assert.Equal("override", output["SECTION:VALUE"]);
        Assert.Single(ReadJson(outputFile).RootElement.EnumerateObject());
    }

    [Fact]
    public void Materialize_check_only_does_not_write_and_returns_hash()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.PathFor("effective.json");

        var result = new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output,
                CheckOnly = true
            });

        Assert.False(result.WasWritten);
        Assert.False(File.Exists(output));
        Assert.Equal(64, result.OutputSha256.Length);
    }

    [Fact]
    public void Materialize_does_not_overwrite_without_explicit_permission()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.Write("effective.json", "{ \"Value\": 99 }");

        Assert.Throws<MaterializerException>(() => new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output
            }));
        Assert.Equal("{ \"Value\": 99 }", File.ReadAllText(output));
    }

    [Fact]
    public void Materialize_preserves_existing_unix_mode_when_overwriting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.Write("effective.json", "{ \"Value\": 99 }");
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(output, expectedMode);

        new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output,
                Overwrite = true
            });

        Assert.Equal(expectedMode, File.GetUnixFileMode(output));
    }

    [Fact]
    public void Materialize_creates_new_unix_output_as_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.PathFor("effective.json");

        new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output
            });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
    }

    [Fact]
    public void Materialize_preserves_existing_macos_acl_when_overwriting()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.Write("effective.json", "{ \"Value\": 99 }");
        File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using (var addAcl = Process.Start(new ProcessStartInfo("/bin/chmod")
               {
                   UseShellExecute = false,
                   ArgumentList = { "+a", "everyone allow read", output }
               })!)
        {
            addAcl.WaitForExit();
            Assert.Equal(0, addAcl.ExitCode);
        }

        new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output,
                Overwrite = true
            });

        using var listAcl = Process.Start(new ProcessStartInfo("/bin/ls")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            ArgumentList = { "-le", output }
        })!;
        var listing = listAcl.StandardOutput.ReadToEnd();
        listAcl.WaitForExit();
        Assert.Equal(0, listAcl.ExitCode);
        Assert.Contains("everyone allow read", listing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Materialize_reports_missing_files_without_writing()
    {
        using var files = new TemporaryFiles();

        var exception = Assert.Throws<MaterializerException>(() => new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [files.PathFor("missing.json")],
                OutputFile = files.PathFor("effective.json")
            }));

        Assert.Contains("nicht gefunden", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(files.PathFor("effective.json")));
    }

    [Fact]
    public void Materialize_rejects_invalid_json_without_writing()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("invalid.json", "{ \"Value\": ");
        var output = files.PathFor("effective.json");

        var exception = Assert.Throws<MaterializerException>(() => new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input],
                OutputFile = output
            }));

        Assert.Contains("ungültiges JSON", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Materialize_can_skip_missing_files_when_explicitly_allowed()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.PathFor("effective.json");

        var result = new ConfigurationMaterializer().Materialize(
            new MaterializerOptions
            {
                InputFiles = [input, files.PathFor("optional.json")],
                OutputFile = output,
                AllowMissingFiles = true
            });

        Assert.Single(result.Warnings);
        Assert.Equal("1", new ConfigurationBuilder().AddJsonFile(output).Build()["Value"]);
    }

    private static IConfigurationRoot BuildConfiguration(params string[] files)
    {
        var builder = new ConfigurationBuilder();
        foreach (var file in files)
        {
            builder.AddJsonFile(file, optional: false, reloadOnChange: false);
        }

        return builder.Build();
    }

    private static Dictionary<string, string?> Read(IConfiguration configuration)
    {
        return configuration.AsEnumerable(true)
            .Where(entry => !string.IsNullOrEmpty(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertConfigurationEqual(IConfiguration expected, IConfiguration actual)
    {
        Assert.Equal(Read(expected), Read(actual));
    }

    private static JsonDocument ReadJson(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"appsettings-materializer-{Guid.NewGuid():N}");

        public TemporaryFiles()
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        public string PathFor(string name) => Path.Combine(directory, name);

        public string Directory => directory;

        public string Write(string name, string content)
        {
            var path = PathFor(name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}
