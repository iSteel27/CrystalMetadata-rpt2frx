using System;
using System.Diagnostics;
using System.IO;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.Shared;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;
using CrystalMetadata.Extractor.Ras;
using CrystalMetadata.Extractor.Walkers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Extractor
{
    /// <summary>
    /// Loads a .rpt file with the Crystal Engine API and walks it (plus the in-process RAS document) into a
    /// <see cref="ReportMetadata"/>. Thread-agnostic and synchronous; the pipe server calls it on a dedicated STA thread.
    /// Never touches report data.
    /// </summary>
    public sealed class ReportMetadataExtractor : IReportMetadataExtractor
    {
        private readonly ILogger _logger;

        public ReportMetadataExtractor(ILogger<ReportMetadataExtractor>? logger = null)
        {
            _logger = logger ?? NullLogger<ReportMetadataExtractor>.Instance;
        }

        /// <inheritdoc />
        public ReportMetadata Extract(string reportPath, ExtractionOptions options)
        {
            if (string.IsNullOrWhiteSpace(reportPath)) throw new ReportLoadException(ErrorCodes.FileNotFound, "reportPath is empty.");
            options = options ?? new ExtractionOptions();

            var fullPath = Path.GetFullPath(reportPath);
            if (!File.Exists(fullPath)) throw new ReportLoadException(ErrorCodes.FileNotFound, "Report file not found: " + fullPath);
            EnsureOleCompoundFile(fullPath);

            var sw = Stopwatch.StartNew();
            var ctx = new ExtractionContext("Main", options, _logger);
            _logger.LogInformation("ReportLoadStarted {Path}", fullPath);

            ReportDocument? doc = null;
            try
            {
                doc = new ReportDocument();
                try
                {
                    doc.Load(fullPath, OpenReportMethod.OpenReportByTempCopy);
                }
                catch (Exception ex)
                {
                    throw LoadFailureClassifier.Classify(fullPath, ex);
                }

                _logger.LogInformation("ReportLoaded {Path} in {Ms} ms", fullPath, sw.ElapsedMilliseconds);

                var ras = RasEnricher.TryCreate(doc, ctx);
                var metadata = ReportWalker.Extract(doc, ras, ctx, fullPath, isSubreport: false, depth: 0);

                metadata.Extractor.DurationMs = sw.ElapsedMilliseconds;
                _logger.LogInformation("ExtractionCompleted {Path} in {Ms} ms sections={Sections} objects={Objects} subreports={Subreports} warnings={Warnings} errors={Errors}",
                    fullPath, sw.ElapsedMilliseconds, metadata.Stats.SectionCount, metadata.Stats.ObjectCount, metadata.Stats.SubreportCount,
                    metadata.Stats.WarningCount, metadata.Stats.ErrorCount);
                return metadata;
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Close failed for {Path}", fullPath); }
                    try { doc.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Dispose failed for {Path}", fullPath); }
                }
            }
        }

        private static readonly byte[] OleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        /// <summary>
        /// .rpt files are OLE compound documents. The Crystal runtime reports any unreadable file with the misleading
        /// "processed by the JRC engine" message, so the header is checked first to classify empty/garbage files correctly.
        /// </summary>
        private static void EnsureOleCompoundFile(string path)
        {
            byte[] header;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    header = new byte[OleMagic.Length];
                    var read = fs.Read(header, 0, header.Length);
                    if (read < header.Length)
                    {
                        throw new ReportLoadException(ErrorCodes.LoadFailed, "Not a Crystal Reports file (" + read + " bytes): " + path);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ReportLoadException(ErrorCodes.AccessDenied, "Access denied: " + path, ex);
            }
            catch (IOException ex)
            {
                throw new ReportLoadException(ErrorCodes.AccessDenied, "Cannot read " + path + ": " + ex.Message, ex);
            }

            for (var i = 0; i < OleMagic.Length; i++)
            {
                if (header[i] != OleMagic[i])
                {
                    throw new ReportLoadException(ErrorCodes.LoadFailed, "Not a Crystal Reports file (missing OLE compound document header): " + path);
                }
            }
        }
    }
}
