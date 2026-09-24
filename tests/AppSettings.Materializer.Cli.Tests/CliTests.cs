using AppSettings.Materializer.Cli;
using Xunit;

namespace AppSettings.Materializer.Cli.Tests;

public sealed class CliTests
{
    [Fact]
    public void Merge_supports_directory_and_environment_convention()
    {
        using var files = new TemporaryFiles();
        files.Write("appsettings.json", "{ \"Value\": \"base\", \"Keep\": true }");
        files.Write("appsettings.Integration.json", "{ \"Value\": \"integration\" }");
        var output = files.PathFor("appsettings.json");

        var exitCode = Program.Main(
        [
            "merge",
            "--directory", files.Directory,
            "--environment", "Integration",
            "--output", output,
            "--overwrite",
            "--pretty"
        ]);

        Assert.Equal(0, exitCode);
        Assert.Contains("integration", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_requires_overwrite_for_existing_output()
    {
        using var files = new TemporaryFiles();
        var input = files.Write("input.json", "{ \"Value\": 1 }");
        var output = files.Write("effective.json", "{ \"Value\": 99 }");

        Assert.Equal(1, Program.Main(["merge", "--input", input, "--output", output]));
        Assert.Equal(0, Program.Main(["merge", "--input", input, "--output", output, "--overwrite"]));
        Assert.Contains("1", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_rejects_unknown_options_with_nonzero_exit_code()
    {
        Assert.Equal(1, Program.Main(["merge", "--input", "input.json", "--output", "output.json", "--unknown"]));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"appsettings-materializer-cli-{Guid.NewGuid():N}");

        public TemporaryFiles()
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        public string Directory => directory;

        public string PathFor(string name) => Path.Combine(directory, name);

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
