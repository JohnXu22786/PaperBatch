namespace PaperBatch.CompatibilityProbe;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var command = args[0];
            var options = ParseOptions(command, args[1..]);
            var output = command switch
            {
                "collect" => ProbeJson.Serialize(
                    ProbeCollector.Collect(
                        options.GetValueOrDefault("--pdfium-path"),
                        options.ContainsKey("--include-local-queues"),
                        options.ContainsKey("--include-device-identifiers"),
                        options.GetValueOrDefault("--libreoffice-engine-path"),
                        options.GetValueOrDefault("--libreoffice-engine-sha256"))),
                "simulate" => ProbeJson.Serialize(PrintJobSimulator.RunGdiJobIdScenario()),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };

            if (options.TryGetValue("--output", out var outputPath) && outputPath is not null)
            {
                File.WriteAllText(Path.GetFullPath(outputPath), output + Environment.NewLine);
            }
            else
            {
                Console.WriteLine(output);
            }

            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Could not write probe output: {exception.Message}");
            return 3;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Could not write probe output: {exception.Message}");
            return 3;
        }
    }

    private static Dictionary<string, string?> ParseOptions(string command, string[] args)
    {
        var allowedOptions = command switch
        {
            "collect" => new HashSet<string>(["--pdfium-path", "--libreoffice-engine-path", "--libreoffice-engine-sha256", "--include-local-queues", "--include-device-identifiers", "--output"], StringComparer.Ordinal),
            "simulate" => new HashSet<string>(["--output"], StringComparer.Ordinal),
            _ => throw new ArgumentException($"Unknown command '{command}'.")
        };
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowedOptions.Contains(name))
            {
                throw new ArgumentException($"Unknown option '{name}' for command '{command}'.");
            }

            if (name is "--include-local-queues" or "--include-device-identifiers")
            {
                options[name] = null;
                continue;
            }

            if (name is "--pdfium-path" or "--libreoffice-engine-path" or "--libreoffice-engine-sha256" or "--output")
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Option {name} requires a value.");
                }

                options[name] = args[++index];
                continue;
            }

            throw new ArgumentException($"Unknown option '{name}'.");
        }

        if (options.TryGetValue("--output", out var output) && string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("Option --output requires a non-empty path.");
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PaperBatch U0 compatibility probe (.NET 10)");
        Console.WriteLine("  collect [--pdfium-path <explicit-pdfium-dll>] [--libreoffice-engine-path <direct-engine-binary>] [--libreoffice-engine-sha256 <trusted-engine-sha256>] [--include-local-queues] [--include-device-identifiers] [--output <json-path>]");
        Console.WriteLine("  simulate [--output <json-path>]");
        Console.WriteLine("The collector never loads PDFium, automates Microsoft Office/WPS, submits a print job, or changes printer defaults.");
        Console.WriteLine("LibreOffice launch requires a direct engine-binary path and its trusted SHA-256 pin; discovered launcher files are never executed.");
    }
}
