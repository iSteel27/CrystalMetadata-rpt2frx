using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Client;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using NJsonSchema.Generation;
using NJsonSchema.NewtonsoftJson.Generation;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace CrystalMetadata.ConsoleApp;

/// <summary>
/// CrystalMetadata.Console extract --in FILE.rpt [--out FILE.json] [--hints] [--generated-sql]
/// CrystalMetadata.Console batch   --in DIR|LIST.txt|FILE.rpt [--in …] --out DIR [--workers N] [--recursive] [--force] [--hints]
/// CrystalMetadata.Console ping | info
/// CrystalMetadata.Console schema  --out FILE.json
/// Global: --pipe NAME --server-exe PATH --attach --timeout MS --log-level LEVEL
/// Exit codes: 0 ok, 1 partial failures, 2 usage error, 3 server unavailable.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Started without arguments from a real terminal (e.g. F5 in Visual Studio): show the guided menu.
        if ((args.Length == 0 && !Console.IsInputRedirected) || (args.Length == 1 && args[0] == "--menu"))
        {
            return await InteractiveMenu.RunAsync();
        }

        var code = await RunAsync(args);

        // --pause keeps the window open when the program was started from a shortcut or a right-click menu
        if (args.Contains("--pause") && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press any key to close this window...");
            Console.ReadKey(intercept: true);
        }

        return code;
    }

    /// <summary>Runs one command line (used by <see cref="Main"/> and by the interactive menu).</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "help" or "/?")
        {
            PrintUsage();
            return 0;
        }

        if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
        {
            var v = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
            Console.Out.WriteLine("rpt2frx " + v.Split('+')[0]);
            return 0;
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        args = ExpandShortcut(args);
        var command = args[0].ToLowerInvariant();
        var sw = ParseSwitches(args.Skip(1));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("CRYSTALMETA_")
            .Build();

        // logs go to the user's own app-data folder: an installed program may not be allowed to write next to itself
        var logDir = configuration["Logging:LogDirectory"] ?? "logs";
        logDir = Path.IsPathRooted(logDir) ? logDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystalMetadata", logDir);
        Directory.CreateDirectory(logDir);
        var level = Enum.TryParse<LogEventLevel>(sw.Get("log-level") ?? configuration["Logging:MinimumLevel"] ?? "Information", true, out var l) ? l : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDir, "console-.json"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
            .CreateLogger();

        try
        {
            if (command == "schema")
            {
                return WriteSchema(sw.Get("out") ?? "metadata.schema.json");
            }

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(Log.Logger, dispose: false));
            services.AddCrystalMetadataClient(configuration, o =>
            {
                if (sw.Get("pipe") is { } pipe) o.PipeName = pipe;
                if (sw.Get("server-exe") is { } exe) o.ServerExePath = exe;
                if (sw.Has("attach")) o.AttachToExistingServer = true;
                if (int.TryParse(sw.Get("workers"), out var w) && w > 0) o.WorkerCount = w;
                if (o.WorkerCount <= 0) o.WorkerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
                if (int.TryParse(sw.Get("timeout"), out var t) && t > 0) o.RequestTimeoutMs = t;
                if (sw.Has("no-current-user-only")) o.CurrentUserOnly = false;
            });

            await using var provider = services.BuildServiceProvider();
            await using var host = provider.GetRequiredService<ExtractorProcessHost>();
            var client = provider.GetRequiredService<ICrystalMetadataClient>();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            switch (command)
            {
                case "extract":
                    return await ExtractAsync(client, sw, configuration, cts.Token);
                case "batch":
                    return await BatchAsync(provider.GetRequiredService<BatchOrchestrator>(), sw, configuration, cts.Token);
                case "convert":
                    return await ConvertAsync(client, sw, cts.Token);
                case "doctor":
                    return OperatingSystem.IsWindows()
                        ? await Doctor.RunAsync(() => client.PingAsync(cts.Token))
                        : await Task.FromResult(Fail("CrystalMetadata runs on Windows only."));
                case "ping":
                case "info":
                    return await PingAsync(client, cts.Token);
                default:
                    Console.Error.WriteLine("Unknown command '" + command + "'.");
                    PrintUsage();
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Cancelled");
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Log.Error("{Message}", ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Shortcut mode: <c>CrystalMetadata.Console "C:\Reports\Sales.rpt"</c> or <c>... "C:\Reports"</c>
    /// (no command word needed). A file becomes <c>extract</c> writing <c>Sales.metadata.json</c> next
    /// to it; a folder becomes a recursive <c>batch</c> writing into <c>&lt;folder&gt;\metadata</c>. Layout hints are on.
    /// </summary>
    private static string[] ExpandShortcut(string[] args)
    {
        var first = args[0];
        if (first.StartsWith("-", StringComparison.Ordinal) ||
            first.ToLowerInvariant() is "extract" or "batch" or "convert" or "doctor" or "ping" or "info" or "schema")
        {
            return args;
        }

        var rest = args.Skip(1);
        if (File.Exists(first))
        {
            var full = Path.GetFullPath(first);
            // a .rpt or an existing .metadata.json becomes a FastReport template (.frx) next to it; use "extract" for JSON only
            return new[] { "convert", "--in", full, "--font-scale", "0.87" }.Concat(rest).ToArray();
        }

        if (Directory.Exists(first))
        {
            var full = Path.GetFullPath(first);
            return new[] { "batch", "--in", full, "--out", Path.Combine(full, "metadata"), "--recursive", "--hints" }.Concat(rest).ToArray();
        }

        return args;
    }

    private static ExtractionOptions BuildExtractionOptions(Switches sw)
    {
        return new ExtractionOptions
        {
            IncludeUnusedFields = !sw.Has("no-unused-fields"),
            IncludeRawBags = !sw.Has("no-raw"),
            IncludeGeneratedSql = sw.Has("generated-sql"),
            TimeoutMs = int.TryParse(sw.Get("timeout"), out var t) && t > 0 ? t : ProtocolConstants.DefaultTimeoutMs
        };
    }

    private static async Task<int> ExtractAsync(ICrystalMetadataClient client, Switches sw, IConfiguration configuration, CancellationToken ct)
    {
        var input = sw.Get("in");
        if (string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("extract requires --in FILE.rpt");
            return 2;
        }

        var result = await client.ExtractAsync(Path.GetFullPath(input), BuildExtractionOptions(sw), ct);
        if (!result.Success || result.Metadata == null)
        {
            Log.Error("{Code}: {Message}", result.ErrorCode, result.ErrorMessage);
            return result.ErrorCode is ErrorCodes.ServerUnavailable ? 3 : 1;
        }

        var hints = sw.Has("hints") || (bool.TryParse(configuration["Batch:Hints"], out var h) && h && !sw.Has("no-hints"));
        if (hints)
        {
            LayoutHints.Apply(result.Metadata);
        }

        var outPath = sw.Get("out");
        if (string.IsNullOrEmpty(outPath))
        {
            Console.Out.WriteLine(MetadataJson.Serialize(result.Metadata, indented: true));
        }
        else
        {
            MetadataJson.WriteFile(outPath, result.Metadata);
            Log.Information("Wrote {Out}", Path.GetFullPath(outPath));
        }

        PrintSummary(result.Metadata);
        return 0;
    }

    private static async Task<int> BatchAsync(BatchOrchestrator orchestrator, Switches sw, IConfiguration configuration, CancellationToken ct)
    {
        var inputs = sw.GetAll("in");
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("batch requires at least one --in DIR|LIST.txt|FILE.rpt");
            return 2;
        }

        var options = new BatchOptions
        {
            Inputs = inputs,
            OutputDirectory = sw.Get("out") ?? configuration["Batch:OutputDirectory"] ?? "out",
            Recursive = sw.Has("recursive"),
            Force = sw.Has("force"),
            ComputeHints = sw.Has("hints") || (bool.TryParse(configuration["Batch:Hints"], out var h) && h && !sw.Has("no-hints")),
            Extraction = BuildExtractionOptions(sw)
        };

        var progress = new Progress<BatchProgress>(p => Console.Error.Write($"\r{p.Completed}/{p.Total} done, {p.Failed} failed   "));
        var manifest = await orchestrator.RunAsync(options, progress, ct);
        Console.Error.WriteLine();

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{"Report",-60} {"Status",-8} {"Objs",5} {"Subs",4} {"Warn",4} {"ms",7}");
        foreach (var e in manifest.Entries)
        {
            var status = e.SkippedByResume ? "skipped" : e.Success ? "ok" : e.ErrorCode ?? "failed";
            Console.Out.WriteLine($"{Truncate(e.RelativePath, 60),-60} {status,-8} {e.ObjectCount,5} {e.SubreportCount,4} {e.WarningCount,4} {e.DurationMs,7}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"Total {manifest.Total}: {manifest.Succeeded} ok, {manifest.Failed} failed, {manifest.Skipped} skipped, {manifest.Retried} retried, {manifest.TotalDurationMs} ms. Manifest: {Path.Combine(Path.GetFullPath(options.OutputDirectory), BatchOrchestrator.ManifestFileName)}");
        return manifest.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// convert --in FILE.metadata.json|FILE.rpt [--out FILE.frx] [--preview]
    /// Generates a FastReport (open source) template. With --preview it also renders it with synthetic sample rows to
    /// FILE.preview.pdf and FILE.preview*.png so the result can be inspected.
    /// </summary>
    private static async Task<int> ConvertAsync(ICrystalMetadataClient client, Switches sw, CancellationToken ct)
    {
        var input = sw.Get("in");
        if (string.IsNullOrEmpty(input) || !File.Exists(input))
        {
            Console.Error.WriteLine("convert requires --in FILE.metadata.json (or a .rpt file that exists).");
            return 2;
        }

        input = Path.GetFullPath(input);
        ReportMetadata? metadata;
        if (input.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase))
        {
            var result = await client.ExtractAsync(input, BuildExtractionOptions(sw), ct);
            if (!result.Success || result.Metadata == null)
            {
                Log.Error("{Code}: {Message}", result.ErrorCode, result.ErrorMessage);
                return 1;
            }

            metadata = result.Metadata;
            CrystalMetadata.Client.LayoutHints.Apply(metadata);
        }
        else
        {
            metadata = MetadataJson.ReadFile<ReportMetadata>(input);
            if (metadata == null)
            {
                Log.Error("{Input} is not a valid CrystalMetadata JSON file.", input);
                return 1;
            }
        }

        var name = Path.GetFileName(input);
        foreach (var suffix in new[] { ".metadata.json", ".rpt", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        }

        var outPath = sw.Get("out") ?? Path.Combine(Path.GetDirectoryName(input)!, name + ".frx");
        if (!Path.IsPathRooted(outPath)) outPath = Path.Combine(Path.GetDirectoryName(input)!, outPath);
        if (!outPath.EndsWith(".frx", StringComparison.OrdinalIgnoreCase)) outPath += ".frx";

        var picture = sw.Get("picture");
        var fontScale = double.TryParse(sw.Get("font-scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fs) && fs > 0 ? fs : 1.0;
        var generated = new CrystalMetadata.FastReport.FrxGenerator().Generate(metadata, new CrystalMetadata.FastReport.FrxOptions { PicturePath = string.IsNullOrEmpty(picture) ? null : Path.GetFullPath(picture), FontScale = fontScale });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, generated.Xml, new System.Text.UTF8Encoding(false), ct);
        Log.Information("Wrote FastReport template {Out}", outPath);

        if (generated.Warnings.Count == 0)
        {
            Log.Information("Converted with no warnings.");
        }
        else
        {
            Log.Warning("{Count} item(s) need attention:", generated.Warnings.Count);
            foreach (var w in generated.Warnings) Console.Out.WriteLine("  - " + w);
        }

        if (sw.Has("preview"))
        {
            try
            {
                var basePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, Path.GetFileNameWithoutExtension(outPath) + ".preview");
                var files = CrystalMetadata.FastReport.FrxPreview.Render(outPath, metadata, basePath);
                foreach (var f in files) Log.Information("Preview: {File}", f);
                Log.Information("The preview uses SAMPLE rows generated from the column names, so numbers and text are placeholders; check the layout, fonts, groups and totals.");
            }
            catch (Exception ex)
            {
                Log.Error("Preview failed (the .frx was still written): {Message}", ex.Message);
                return 1;
            }
        }

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static async Task<int> PingAsync(ICrystalMetadataClient client, CancellationToken ct)
    {
        try
        {
            var info = await client.PingAsync(ct);
            Console.Out.WriteLine(MetadataJson.Serialize(info, indented: true));
            return 0;
        }
        catch (Exception ex) when (ex is TransportException or TimeoutException or InvalidOperationException)
        {
            Log.Error("Server unavailable: {Message}", ex.Message);
            return 3;
        }
    }

    private static int WriteSchema(string outPath)
    {
        var settings = new NewtonsoftJsonSchemaGeneratorSettings
        {
            SerializerSettings = MetadataJson.CreateSettings(indented: false),
            SchemaType = SchemaType.JsonSchema,
            DefaultReferenceTypeNullHandling = ReferenceTypeNullHandling.Null,
            GenerateAbstractProperties = false,
            FlattenInheritanceHierarchy = true
        };
        var schema = JsonSchema.FromType<ReportMetadata>(settings);
        schema.Title = "CrystalMetadata ReportMetadata";
        schema.Description = "Design metadata of one Crystal Reports .rpt file (schemaVersion " + ReportMetadata.CurrentSchemaVersion + "). Positions in twips (1440/inch), fonts in points. null = not available from Crystal.";
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, schema.ToJson(), MetadataJson.Utf8NoBom);
        Log.Information("Wrote JSON Schema to {Out}", Path.GetFullPath(outPath));
        return 0;
    }

    private static void PrintSummary(ReportMetadata m)
    {
        Log.Information("{Report}: {Sections} sections, {Objects} objects, {Tables} tables, {Params} parameters, {Formulas} formulas, {Groups} groups, {Subreports} subreports, {Warnings} warnings, {Errors} errors, {Ms} ms",
            m.ReportInfo.ReportName, m.Stats.SectionCount, m.Stats.ObjectCount, m.Stats.TableCount, m.Stats.ParameterCount, m.Stats.FormulaCount, m.Groups.Count, m.Stats.SubreportCount, m.Stats.WarningCount, m.Stats.ErrorCount, m.Extractor.DurationMs);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];

    internal static void PrintUsage()
    {
        Console.Out.WriteLine(@"rpt2frx — convert Crystal Reports (.rpt) to FastReport templates (.frx)

  SIMPLEST:  rpt2frx ""C:\Reports\Sales.rpt""        -> writes Sales.frx next to it
             rpt2frx ""C:\Reports""                  -> every .rpt in the folder tree (JSON metadata) into C:\Reports\metadata
             rpt2frx ""C:\Reports\Sales.rpt"" --preview   -> also renders Sales.preview.pdf/.png to look at
             rpt2frx --version | rpt2frx doctor
             (run it with no arguments for a guided menu)

  Full commands:

  extract --in FILE.rpt [--out FILE.json] [--hints|--no-hints] [--generated-sql] [--no-raw] [--no-unused-fields]
  batch   --in DIR|LIST.txt|FILE.rpt [--in ...] [--out DIR] [--workers N] [--recursive] [--force] [--hints|--no-hints]
  convert --in FILE.rpt|FILE.metadata.json [--out FILE.frx] [--preview] [--font-scale 0.87] [--picture logo.png]
  doctor   (check my setup: what is missing and how to fix it)
  ping | info
  schema  [--out FILE.json]

Global switches: --pipe NAME  --server-exe PATH  --attach  --timeout MS  --log-level Verbose|Debug|Information|Warning
Exit codes: 0 ok, 1 failures, 2 usage, 3 server unavailable");
    }

    private sealed class Switches
    {
        private readonly Dictionary<string, List<string?>> _values = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, string? value)
        {
            if (!_values.TryGetValue(name, out var list))
            {
                list = new List<string?>();
                _values[name] = list;
            }

            list.Add(value);
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public string? Get(string name) => _values.TryGetValue(name, out var l) ? l.LastOrDefault() : null;
        public List<string> GetAll(string name) => _values.TryGetValue(name, out var l) ? l.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList() : new List<string>();
    }

    private static Switches ParseSwitches(IEnumerable<string> args)
    {
        var result = new Switches();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var name = a[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq > 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = list[++i];
            }

            result.Add(name, value);
        }

        return result;
    }
}
