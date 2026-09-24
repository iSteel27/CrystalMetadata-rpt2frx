using System;
using System.Collections.Generic;
using CrystalDecisions.CrystalReports.Engine;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;
using CrystalMetadata.Extractor.Ras;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>
    /// Opens every subreport placed in the report and extracts it as a complete report (recursion through
    /// <see cref="ReportWalker"/>). Crystal does not allow nested subreports, so recursion depth is 1 in practice.
    /// </summary>
    internal static class SubreportWalker
    {
        public static List<SubreportMetadata> Read(ReportDocument doc, ReportMetadata parent, RasEnricher? ras, ExtractionContext ctx, string? filePath, int depth)
        {
            var list = new List<SubreportMetadata>();
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in parent.Sections)
            {
                foreach (var obj in section.Objects)
                {
                    if (obj.Kind != ObjectKind.Subreport || obj.Subreport == null)
                    {
                        continue;
                    }

                    var name = obj.Subreport.SubreportName;
                    var index = list.Count;
                    var path = "subreports[" + index + "]";
                    placed.Add(name);

                    var sm = new SubreportMetadata
                    {
                        Name = name,
                        IsOnDemand = obj.Subreport.IsOnDemand,
                        Placement = new SubreportPlacement
                        {
                            SectionName = section.Name,
                            ObjectName = obj.Name,
                            LeftTwips = obj.LeftTwips,
                            TopTwips = obj.TopTwips,
                            WidthTwips = obj.WidthTwips,
                            HeightTwips = obj.HeightTwips,
                            ZIndex = obj.ZIndex
                        }
                    };

                    if (depth >= 1)
                    {
                        // Crystal forbids this; if a future runtime ever allows it we still extract, but flag it.
                        ctx.Warn(DiagnosticCodes.NestedSubreportIgnored, path, "Subreport '" + name + "' is nested inside a subreport (depth " + depth + "). Extracting anyway.");
                    }

                    if (ras != null)
                    {
                        sm.Links = ctx.Try(path + ".links", DiagnosticCodes.SubreportLinksFailed, () => ras.GetSubreportLinks(name)) ?? new List<SubreportLinkMetadata>();
                    }
                    else
                    {
                        ctx.Warn(DiagnosticCodes.RasUnavailable, path + ".links", "RAS unavailable; subreport links for '" + name + "' could not be read.");
                    }

                    ReportDocument? sub = null;
                    try
                    {
                        sub = ctx.Try(path, DiagnosticCodes.SubreportOpenFailed, () => doc.OpenSubreport(name));
                        if (sub != null)
                        {
                            var subCtx = ctx.ForSubreport(name);
                            var subRas = ras?.ForSubreport(name, subCtx);
                            ctx.Logger.LogInformation("Extracting subreport {Subreport} placed in {Section}", name, section.Name);
                            sm.Report = ReportWalker.Extract(sub, subRas, subCtx, filePath, isSubreport: true, depth: depth + 1);
                            // one timestamp per document: keeps repeated extractions byte-identical apart from the root stamp
                            sm.Report.Extractor.ExtractedAtUtc = parent.Extractor.ExtractedAtUtc;
                            ctx.Logger.LogInformation("SubreportExtracted {Subreport} links={Links} objects={Objects}", name, sm.Links.Count, sm.Report.Stats.ObjectCount);
                        }
                    }
                    finally
                    {
                        // subreport documents share the parent's lifetime; disposing them explicitly is harmless and keeps the job count low
                        try { sub?.Close(); } catch { /* ignore */ }
                        try { sub?.Dispose(); } catch { /* ignore */ }
                    }

                    // mark linked parameters on the subreport side using the RAS links
                    if (sm.Report != null)
                    {
                        foreach (var link in sm.Links)
                        {
                            var pname = link.LinkedParameterName;
                            if (string.IsNullOrEmpty(pname)) continue;
                            foreach (var p in sm.Report.Parameters)
                            {
                                if (string.Equals(p.Name, pname, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(p.FormulaName, link.SubreportParameter, StringComparison.OrdinalIgnoreCase))
                                {
                                    p.IsLinkedFromParentReport = true;
                                    p.LinkedSourceHint = link.MainReportField ?? p.LinkedSourceHint;
                                }
                            }
                        }
                    }

                    list.Add(sm);
                }
            }

            // any subreport in the collection that is not placed in a section
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.Subreports)))
            {
                var subName = Reflect.GetString(item, "Name");
                if (!string.IsNullOrEmpty(subName) && !placed.Contains(subName!))
                {
                    ctx.Warn(DiagnosticCodes.SubreportNotPlaced, "subreports", "Subreport '" + subName + "' exists in the document but is not placed in any section.");
                }
            }

            return list;
        }
    }
}
