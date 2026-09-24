using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Client;

public sealed class BatchOptions
{
    /// <summary>Files, directories, or .txt list files (one path per line).</summary>
    public List<string> Inputs { get; set; } = new();
    public string OutputDirectory { get; set; } = "out";
    public bool Recursive { get; set; }
    public bool Force { get; set; }
    public bool ComputeHints { get; set; }
    public ExtractionOptions Extraction { get; set; } = new();
}

public sealed class ManifestEntry
{
    public string ReportPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string? OutputFile { get; set; }
    public string InputSha256 { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
    public int Attempts { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public int ObjectCount { get; set; }
    public int SubreportCount { get; set; }
    public bool SkippedByResume { get; set; }
    public DateTime CompletedUtc { get; set; }
}

public sealed class BatchManifest
{
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Retried { get; set; }
    public long TotalDurationMs { get; set; }
    public List<ManifestEntry> Entries { get; set; } = new();
}

/// <summary>
/// Runs a batch: resolves inputs, applies resume (skip when output exists and the input hash is unchanged), extracts
/// with the configured worker count, writes <c>{name}.metadata.json</c> files and a deterministic <c>_manifest.json</c>.
/// </summary>
public sealed class BatchOrchestrator
{
    public const string ManifestFileName = "_manifest.json";

    private readonly ICrystalMetadataClient _client;
    private readonly ILogger _logger;

    public BatchOrchestrator(ICrystalMetadataClient client, ILogger<BatchOrchestrator>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<BatchOrchestrator>.Instance;
    }

    public async Task<BatchManifest> RunAsync(BatchOptions options, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        var outputDir = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDir);

        var inputs = ResolveInputs(options);
        var commonRoot = CommonRoot(inputs.Select(i => i.Path).ToList());
        var manifest = new BatchManifest { StartedUtc = DateTime.UtcNow, Total = inputs.Count };
        var previous = LoadPrevious(outputDir);
        var entries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var toRun = new List<string>();

        foreach (var input in inputs)
        {
            var relative = MakeRelative(commonRoot, input.Path);
            var outputFile = Path.Combine(outputDir, Path.ChangeExtension(relative, null) + ".metadata.json");
            var hash = Sha256(input.Path);
            var entry = new ManifestEntry { ReportPath = input.Path, RelativePath = relative, OutputFile = outputFile, InputSha256 = hash };
            entries[input.Path] = entry;

            if (!options.Force && previous.TryGetValue(input.Path, out var prev) && prev.Success && prev.InputSha256 == hash && File.Exists(outputFile))
            {
                entry.Success = true;
                entry.SkippedByResume = true;
                entry.WarningCount = prev.WarningCount;
                entry.ErrorCount = prev.ErrorCount;
                entry.ObjectCount = prev.ObjectCount;
                entry.SubreportCount = prev.SubreportCount;
                entry.CompletedUtc = prev.CompletedUtc;
                entry.DurationMs = prev.DurationMs;
                entry.Attempts = prev.Attempts;
                manifest.Skipped++;
                continue;
            }

            toRun.Add(input.Path);
        }

        _logger.LogInformation("Batch: {Total} reports, {Run} to extract, {Skipped} skipped by resume, output {Out}", inputs.Count, toRun.Count, manifest.Skipped, outputDir);

        await foreach (var result in _client.ExtractManyAsync(toRun, options.Extraction, progress, ct).ConfigureAwait(false))
        {
            var entry = entries[result.ReportPath];
            entry.Success = result.Success;
            entry.ErrorCode = result.ErrorCode;
            entry.ErrorMessage = result.ErrorMessage;
            entry.DurationMs = result.DurationMs;
            entry.Attempts = result.Attempts;
            entry.CompletedUtc = DateTime.UtcNow;
            if (result.Attempts > 1) manifest.Retried++;

            if (result.Success && result.Metadata != null)
            {
                if (options.ComputeHints)
                {
                    LayoutHints.Apply(result.Metadata);
                }

                MetadataJson.WriteFile(entry.OutputFile!, result.Metadata);
                entry.WarningCount = result.Metadata.Stats.WarningCount;
                entry.ErrorCount = result.Metadata.Stats.ErrorCount;
                entry.ObjectCount = result.Metadata.Stats.ObjectCount;
                entry.SubreportCount = result.Metadata.Stats.SubreportCount;
                _logger.LogInformation("OK  {Relative} ({Objects} objects, {Subreports} subreports, {Warnings} warnings, {Ms} ms)", entry.RelativePath, entry.ObjectCount, entry.SubreportCount, entry.WarningCount, entry.DurationMs);
            }
            else if (result.Success && result.ServerOutputPath != null)
            {
                entry.OutputFile = result.ServerOutputPath;
                _logger.LogInformation("OK  {Relative} (written by server to {Out})", entry.RelativePath, result.ServerOutputPath);
            }
            else
            {
                entry.OutputFile = null;
                _logger.LogError("FAIL {Relative}: {Code} {Message}", entry.RelativePath, result.ErrorCode, result.ErrorMessage);
            }
        }

        manifest.Entries = entries.Values.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        manifest.Succeeded = manifest.Entries.Count(e => e.Success);
        manifest.Failed = manifest.Entries.Count(e => !e.Success);
        manifest.CompletedUtc = DateTime.UtcNow;
        manifest.TotalDurationMs = (long)(manifest.CompletedUtc - manifest.StartedUtc).TotalMilliseconds;
        MetadataJson.WriteFile(Path.Combine(outputDir, ManifestFileName), manifest);

        _logger.LogInformation("Batch complete: {Succeeded} ok, {Failed} failed, {Skipped} skipped, {Retried} retried in {Ms} ms", manifest.Succeeded, manifest.Failed, manifest.Skipped, manifest.Retried, manifest.TotalDurationMs);
        return manifest;
    }

    private sealed record InputFile(string Path);

    private static List<InputFile> ResolveInputs(BatchOptions options)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in options.Inputs)
        {
            var input = Path.GetFullPath(raw);
            if (Directory.Exists(input))
            {
                foreach (var f in Directory.EnumerateFiles(input, "*.rpt", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    set.Add(Path.GetFullPath(f));
                }
            }
            else if (File.Exists(input) && input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadAllLines(input))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#')) continue;
                    set.Add(Path.GetFullPath(t));
                }
            }
            else if (File.Exists(input))
            {
                set.Add(input);
            }
            else
            {
                throw new FileNotFoundException("Input not found: " + input);
            }
        }

        return set.Select(p => new InputFile(p)).ToList();
    }

    private static Dictionary<string, ManifestEntry> LoadPrevious(string outputDir)
    {
        var path = Path.Combine(outputDir, ManifestFileName);
        if (!File.Exists(path)) return new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var m = MetadataJson.ReadFile<BatchManifest>(path);
            return m?.Entries.ToDictionary(e => e.ReportPath, e => e, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CommonRoot(List<string> paths)
    {
        if (paths.Count == 0) return string.Empty;
        var dirs = paths.Select(p => Path.GetDirectoryName(p) ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = dirs[0].Split(Path.DirectorySeparatorChar);
        var common = parts.Length;
        foreach (var d in dirs.Skip(1))
        {
            var other = d.Split(Path.DirectorySeparatorChar);
            var n = 0;
            while (n < common && n < other.Length && string.Equals(parts[n], other[n], StringComparison.OrdinalIgnoreCase)) n++;
            common = n;
        }

        return string.Join(Path.DirectorySeparatorChar, parts.Take(common));
    }

    private static string MakeRelative(string root, string path)
    {
        if (string.IsNullOrEmpty(root)) return Path.GetFileName(path);
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(path) : rel;
    }
}
