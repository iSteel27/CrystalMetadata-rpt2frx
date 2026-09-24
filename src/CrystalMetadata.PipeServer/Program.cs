using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using CrystalMetadata.Extractor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace CrystalMetadata.PipeServer
{
    /// <summary>
    /// Entry point.
    ///   CrystalMetadata.PipeServer.exe [--pipe NAME] [--max-reports N] [--once] [--allow-output-path] [--log-dir DIR]
    ///   CrystalMetadata.PipeServer.exe --extract REPORT.rpt [--out FILE.json]      (direct mode, no pipe)
    /// Exit codes: 0 ok, 1 extraction failed (direct mode), 3 Crystal runtime missing, 10 recycle requested.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var switches = ParseSwitches(args);

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables("CRYSTALMETA_")
                .AddInMemoryCollection(ToConfigOverrides(switches))
                .Build();

            var serverOptions = new ServerOptions();
            configuration.GetSection("Server").Bind(serverOptions);
            var extractionDefaults = new ExtractionDefaults();
            configuration.GetSection("Extraction").Bind(extractionDefaults);

            // per-user folder: an installed program may not be allowed to write next to its own files
            var logDir = Path.IsPathRooted(serverOptions.LogDirectory) ? serverOptions.LogDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystalMetadata", serverOptions.LogDirectory);
            Directory.CreateDirectory(logDir);
            var minimumLevel = ParseLevel(configuration["Logging:MinimumLevel"]);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Process", "CrystalMetadata.PipeServer")
                .Enrich.WithProperty("Pipe", serverOptions.PipeName)
                .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose, outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDir, "pipeserver-.json"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
                .CreateLogger();

            try
            {
                if (!CrystalRuntime.Probe(out var probeMessage))
                {
                    Log.Fatal("{Message}", probeMessage);
                    Console.Error.WriteLine(probeMessage);
                    return ProtocolConstants.RuntimeMissingExitCode;
                }

                Log.Information("{Message}", probeMessage);

                if (switches.TryGetValue("extract", out var directPath) && !string.IsNullOrEmpty(directPath))
                {
                    switches.TryGetValue("out", out var outPath);
                    return RunDirect(directPath!, outPath, extractionDefaults);
                }

                return RunServer(configuration, serverOptions, extractionDefaults);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static int RunServer(IConfiguration configuration, ServerOptions serverOptions, ExtractionDefaults extractionDefaults)
        {
            var host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(cfg => cfg.Sources.Clear())
                .ConfigureAppConfiguration(cfg => cfg.AddConfiguration(configuration))
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(serverOptions);
                    services.AddSingleton(extractionDefaults);
                    services.AddSingleton<ServerState>();
                    services.AddSingleton<IReportMetadataExtractor, ReportMetadataExtractor>();
                    services.AddSingleton<StaExtractionWorker>();
                    services.AddSingleton<RequestDispatcher>();
                    services.AddSingleton<PipeServerService>();
                    services.AddHostedService(sp => sp.GetRequiredService<PipeServerService>());
                })
                .Build();

            host.Run();
            var service = host.Services.GetRequiredService<PipeServerService>();
            return service.ExitCode;
        }

        private static int RunDirect(string reportPath, string? outPath, ExtractionDefaults defaults)
        {
            using (var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false)))
            {
                var extractor = new ReportMetadataExtractor(loggerFactory.CreateLogger<ReportMetadataExtractor>());
                using (var worker = new StaExtractionWorker(extractor, loggerFactory.CreateLogger<StaExtractionWorker>()))
                {
                    var options = new ExtractionOptions
                    {
                        IncludeUnusedFields = defaults.IncludeUnusedFields,
                        IncludeRawBags = defaults.IncludeRawBags,
                        IncludeGeneratedSql = defaults.IncludeGeneratedSql
                    };

                    try
                    {
                        var metadata = worker.ExtractAsync(reportPath, options, CancellationToken.None).GetAwaiter().GetResult();
                        var json = MetadataJson.Serialize(metadata, indented: true);
                        if (string.IsNullOrEmpty(outPath))
                        {
                            Console.Out.WriteLine(json);
                        }
                        else
                        {
                            MetadataJson.WriteFile(outPath!, metadata);
                            Log.Information("Wrote {Out} ({Objects} objects, {Warnings} warnings, {Errors} errors)", outPath, metadata.Stats.ObjectCount, metadata.Stats.WarningCount, metadata.Stats.ErrorCount);
                        }

                        return 0;
                    }
                    catch (ReportLoadException ex)
                    {
                        Log.Error("{Code}: {Message}", ex.Code, ex.Message);
                        Console.Error.WriteLine(ex.Code + ": " + ex.Message);
                        return 1;
                    }
                }
            }
        }

        private static Dictionary<string, string?> ParseSwitches(string[] args)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
                var name = a.Substring(2);
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    value = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                result[name] = value;
            }

            return result;
        }

        private static IEnumerable<KeyValuePair<string, string?>> ToConfigOverrides(Dictionary<string, string?> switches)
        {
            var list = new List<KeyValuePair<string, string?>>();
            if (switches.TryGetValue("pipe", out var pipe) && !string.IsNullOrEmpty(pipe)) list.Add(new KeyValuePair<string, string?>("Server:PipeName", pipe));
            if (switches.TryGetValue("max-reports", out var max) && !string.IsNullOrEmpty(max)) list.Add(new KeyValuePair<string, string?>("Server:MaxReportsPerProcess", max));
            if (switches.TryGetValue("log-dir", out var logDir) && !string.IsNullOrEmpty(logDir)) list.Add(new KeyValuePair<string, string?>("Server:LogDirectory", logDir));
            if (switches.TryGetValue("timeout", out var timeout) && !string.IsNullOrEmpty(timeout)) list.Add(new KeyValuePair<string, string?>("Server:RequestTimeoutMs", timeout));
            if (switches.ContainsKey("once")) list.Add(new KeyValuePair<string, string?>("Server:Once", "true"));
            if (switches.ContainsKey("allow-output-path")) list.Add(new KeyValuePair<string, string?>("Server:AllowOutputPath", "true"));
            if (switches.TryGetValue("log-level", out var level) && !string.IsNullOrEmpty(level)) list.Add(new KeyValuePair<string, string?>("Logging:MinimumLevel", level));
            return list;
        }

        private static LogEventLevel ParseLevel(string? text)
        {
            return Enum.TryParse<LogEventLevel>(text ?? "Information", true, out var level) ? level : LogEventLevel.Information;
        }
    }
}
