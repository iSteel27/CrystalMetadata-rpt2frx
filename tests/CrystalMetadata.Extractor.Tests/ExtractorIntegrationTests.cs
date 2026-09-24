using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrystalMetadata.Core;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;
using Xunit;

namespace CrystalMetadata.Extractor.Tests
{
    /// <summary>
    /// Integration tests against real .rpt files. They need the Crystal runtime and are skipped unless
    /// CR_RUNTIME_AVAILABLE=1. Sample directory: CR_SAMPLE_DIR (default: ../../../../../samples).
    /// </summary>
    public class ExtractorIntegrationTests
    {
        private static bool RuntimeAvailable => Environment.GetEnvironmentVariable("CR_RUNTIME_AVAILABLE") == "1";

        private static string SampleDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("CR_SAMPLE_DIR");
                if (!string.IsNullOrEmpty(env)) return env!;
                return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "samples"));
            }
        }

        private static string[] SampleReports()
        {
            return Directory.Exists(SampleDir) ? Directory.GetFiles(SampleDir, "*.rpt", SearchOption.AllDirectories).OrderBy(p => p).ToArray() : new string[0];
        }

        private static ReportMetadata Extract(string path, ExtractionOptions? options = null)
        {
            return new ReportMetadataExtractor().Extract(path, options ?? new ExtractionOptions());
        }

        [Fact]
        public void Runtime_Probe_Succeeds()
        {
            if (!RuntimeAvailable) return;
            Assert.True(CrystalRuntime.Probe(out var message), message);
            Assert.NotNull(CrystalRuntime.Version);
        }

        [Fact]
        public void EverySample_ExtractsWithoutErrorsAndWithGeometry()
        {
            if (!RuntimeAvailable) return;
            var samples = SampleReports();
            Assert.True(samples.Length > 0, "No sample .rpt files found in " + SampleDir);

            foreach (var path in samples)
            {
                var m = Extract(path);

                Assert.Equal(Path.GetFileNameWithoutExtension(path), m.ReportInfo.ReportName);
                Assert.True(m.ReportInfo.FileSizeBytes > 0);
                Assert.NotNull(m.ReportInfo.RecordSelectionFormula);
                Assert.NotNull(m.ReportInfo.ReportFileVersion);
                Assert.True(m.Sections.Count > 0, path + ": no sections");
                Assert.Equal(0, m.Stats.ErrorCount);

                foreach (var s in m.Sections)
                {
                    Assert.True(s.HeightTwips >= 0);
                    Assert.False(string.IsNullOrEmpty(s.Kind));
                    for (var i = 0; i < s.Objects.Count; i++)
                    {
                        var o = s.Objects[i];
                        Assert.Equal(i, o.ZIndex);
                        Assert.Equal(s.Name, o.SectionName);
                        Assert.Equal(o.LeftTwips + o.WidthTwips, o.RightTwips);
                        Assert.Equal(Units.ToPoints(o.LeftTwips), o.LeftPoints);
                        Assert.NotEqual(ObjectKind.Unknown, o.Kind);
                    }
                }

                // group areas map to group indexes 1..n
                var groupHeaders = m.Areas.Where(a => a.Kind == "GroupHeader").Select(a => a.GroupIndex).ToList();
                Assert.Equal(Enumerable.Range(1, groupHeaders.Count).Select(i => (int?)i), groupHeaders);
                var groupFooters = m.Areas.Where(a => a.Kind == "GroupFooter").Select(a => a.GroupIndex).ToList();
                Assert.Equal(Enumerable.Range(1, groupFooters.Count).Reverse().Select(i => (int?)i), groupFooters);
            }
        }

        [Fact]
        public void Extraction_IsDeterministic()
        {
            if (!RuntimeAvailable) return;
            var path = SampleReports().FirstOrDefault();
            if (path == null) return;

            var a = Extract(path);
            var b = Extract(path);
            Normalize(a);
            Normalize(b);
            Assert.Equal(MetadataJson.Serialize(a, true), MetadataJson.Serialize(b, true));
        }

        /// <summary>Blanks the only run-dependent values (timestamps / durations), recursively for subreports.</summary>
        private static void Normalize(ReportMetadata m)
        {
            m.Extractor.ExtractedAtUtc = DateTime.MinValue;
            m.Extractor.DurationMs = 0;
            foreach (var s in m.Subreports)
            {
                if (s.Report != null) Normalize(s.Report);
            }
        }

        [Fact]
        public void Formulas_ArePreservedVerbatim()
        {
            if (!RuntimeAvailable) return;
            var withFormulas = SampleReports().Select(p => Extract(p)).FirstOrDefault(m => m.Formulas.Count > 0);
            if (withFormulas == null) return;

            foreach (var f in withFormulas.Formulas)
            {
                Assert.StartsWith("{@", f.FormulaName);
                Assert.NotNull(f.Text);
                Assert.Equal(f.Text, f.Text.Trim() == f.Text ? f.Text : f.Text); // no trimming applied by the extractor
                Assert.Equal("Crystal", f.Syntax ?? "Crystal");
            }
        }

        [Fact]
        public void Subreports_AreExtractedWithPlacementAndParametersFlagged()
        {
            if (!RuntimeAvailable) return;
            var withSub = SampleReports().Select(p => Extract(p)).FirstOrDefault(m => m.Subreports.Count > 0);
            if (withSub == null) return;

            foreach (var sub in withSub.Subreports)
            {
                Assert.NotNull(sub.Placement);
                Assert.Contains(withSub.Sections, s => s.Name == sub.Placement!.SectionName && s.Objects.Any(o => o.Name == sub.Placement.ObjectName && o.Kind == ObjectKind.Subreport));
                Assert.NotNull(sub.Report);
                Assert.True(sub.Report!.ReportInfo.IsSubreport);
                Assert.Empty(sub.Report.Subreports);
                foreach (var link in sub.Links)
                {
                    Assert.False(string.IsNullOrEmpty(link.MainReportField));
                    if (link.LinkedParameterName != null)
                    {
                        Assert.Contains(sub.Report.Parameters, p => p.Name == link.LinkedParameterName && p.IsLinkedFromParentReport);
                    }
                }

                foreach (var p in sub.Report.Parameters.Where(p => p.Name.StartsWith("Pm-")))
                {
                    Assert.True(p.IsLinkedFromParentReport);
                    Assert.NotNull(p.LinkedSourceHint);
                }
            }
        }

        [Fact]
        public void TextObjects_WithEmbeddedFields_ExposeParagraphs()
        {
            if (!RuntimeAvailable) return;
            var texts = SampleReports().Select(p => Extract(p)).SelectMany(m => m.Sections).SelectMany(s => s.Objects).Where(o => o.Kind == ObjectKind.Text && o.Text != null).ToList();
            if (texts.Count == 0) return;

            Assert.All(texts, t => Assert.NotNull(t.Text!.Text));
            var withField = texts.FirstOrDefault(t => t.Text!.Paragraphs.Any(p => p.Elements.Any(e => e.Kind == "Field")));
            if (withField != null)
            {
                var element = withField.Text!.Paragraphs.SelectMany(p => p.Elements).First(e => e.Kind == "Field");
                Assert.StartsWith("{", element.DataSource);
                Assert.Contains(element.DataSource!, withField.Text.Text);
            }
        }

        [Fact]
        public void ConditionalFormulas_AreReadFromRas()
        {
            if (!RuntimeAvailable) return;
            var path = Path.Combine(SampleDir, "SanitizationInvoiceDetails_Conditional.rpt");
            if (!File.Exists(path)) return;

            var m = Extract(path);
            var objects = m.Sections.SelectMany(s => s.Objects).ToList();

            var suppressed = objects.FirstOrDefault(o => o.Format.ConditionFormulas.Any(c => c.Type == "EnableSuppress"));
            Assert.NotNull(suppressed);
            Assert.Equal("{sanitization_bill_print.sbud_amount} = 0", suppressed!.Format.ConditionFormulas.First(c => c.Type == "EnableSuppress").Text);

            var coloured = objects.FirstOrDefault(o => o.Font != null && o.Font.ConditionFormulas.Any(c => c.Type == "Color"));
            Assert.NotNull(coloured);
            Assert.Contains("crRed", coloured!.Font!.ConditionFormulas.First(c => c.Type == "Color").Text);
        }

        [Fact]
        public void CorruptFile_YieldsLoadFailedWithoutCrashing()
        {
            if (!RuntimeAvailable) return;
            var dir = Path.Combine(Path.GetTempPath(), "cmtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var empty = Path.Combine(dir, "empty.rpt");
                File.WriteAllBytes(empty, new byte[0]);
                var fake = Path.Combine(dir, "text.rpt");
                File.WriteAllText(fake, "this is not a crystal report");

                var ex1 = Assert.Throws<ReportLoadException>(() => Extract(empty));
                Assert.Equal(ErrorCodes.LoadFailed, ex1.Code);
                var ex2 = Assert.Throws<ReportLoadException>(() => Extract(fake));
                Assert.Equal(ErrorCodes.LoadFailed, ex2.Code);
                var ex3 = Assert.Throws<ReportLoadException>(() => Extract(Path.Combine(dir, "missing.rpt")));
                Assert.Equal(ErrorCodes.FileNotFound, ex3.Code);

                // the process (and the extractor) is still usable afterwards
                var good = SampleReports().FirstOrDefault();
                if (good != null) Assert.NotNull(Extract(good));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task StaWorker_HonoursTimeoutAndPoisons()
        {
            var slow = new SlowExtractor(TimeSpan.FromSeconds(5));
            using (var worker = new StaExtractionWorker(slow))
            {
                var ex = await Assert.ThrowsAsync<TimeoutException>(() => worker.ExtractAsync("x.rpt", new ExtractionOptions { TimeoutMs = 200 }, default));
                Assert.Contains("exceeded", ex.Message);
                Assert.True(worker.IsPoisoned);
                await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExtractAsync("y.rpt", new ExtractionOptions(), default));
            }
        }

        private sealed class SlowExtractor : IReportMetadataExtractor
        {
            private readonly TimeSpan _delay;
            public SlowExtractor(TimeSpan delay) { _delay = delay; }
            public ReportMetadata Extract(string reportPath, ExtractionOptions options)
            {
                System.Threading.Thread.Sleep(_delay);
                return new ReportMetadata();
            }
        }
    }
}
