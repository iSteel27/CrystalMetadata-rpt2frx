using System;
using System.Collections.Generic;
using CrystalDecisions.CrystalReports.Engine;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Ras;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>
    /// Orchestrates extraction of one <see cref="ReportDocument"/> (main report or subreport). Every top-level part of the
    /// JSON is independent: a failure in one never prevents the others.
    /// </summary>
    internal static class ReportWalker
    {
        public static ReportMetadata Extract(ReportDocument doc, RasEnricher? ras, ExtractionContext ctx, string? filePath, bool isSubreport, int depth)
        {
            var m = new ReportMetadata();
            m.Extractor.CrystalRuntimeVersion = CrystalRuntime.Version;
            m.Extractor.Version = typeof(ReportWalker).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            if (ras == null)
            {
                ctx.Warn(DiagnosticCodes.RasUnavailable, "$", "In-process RAS document unavailable; conditional formulas, text paragraphs, command SQL and subreport links will be missing.");
            }

            m.ReportInfo = ReportInfoWalker.ReadInfo(doc, filePath, isSubreport, ctx);
            if (ras != null)
            {
                m.ReportInfo.ReportFileVersion = ras.GetFileVersion();
            }

            m.PageSetup = ReportInfoWalker.ReadPageSetup(doc, isSubreport, ctx);
            if (!isSubreport)
            {
                ras?.EnrichPageSetup(m.PageSetup);
            }

            m.DataSource = DataSourceWalker.Read(doc, ctx);
            ras?.EnrichDataSource(m.DataSource);
            if (ctx.Options.IncludeGeneratedSql && ras != null)
            {
                m.DataSource.GeneratedSql = ras.GetGeneratedSql();
            }

            m.Parameters = DataDefinitionWalker.ReadParameters(doc, ctx);
            ras?.EnrichParameters(m.Parameters);

            m.Formulas = DataDefinitionWalker.ReadFormulas(doc, ctx);
            ras?.EnrichFormulas(m.Formulas);
            ctx.Logger.LogDebug("FormulasExtracted count={Count}", m.Formulas.Count);
            foreach (var f in m.Formulas)
            {
                ctx.Logger.LogTrace("Formula {Formula}", f.FormulaName);
            }

            m.SqlExpressions = DataDefinitionWalker.ReadSqlExpressions(doc, ctx);
            m.CustomFunctions = ras?.GetCustomFunctions() ?? new List<CustomFunctionMetadata>();

            var engineGroups = DataDefinitionWalker.EngineGroups(doc, ctx);
            m.SortFields = DataDefinitionWalker.ReadSortFields(doc, ctx);
            m.Groups = DataDefinitionWalker.ReadGroups(engineGroups, m.SortFields, ctx);
            ras?.EnrichGroups(m.Groups);
            m.SummaryFields = DataDefinitionWalker.ReadSummaries(doc, engineGroups, ctx);
            m.RunningTotals = DataDefinitionWalker.ReadRunningTotals(doc, ctx);

            m.Areas = LayoutWalker.ReadAreas(doc, m.Groups.Count, ras, ctx, out var placements);
            m.Sections = LayoutWalker.ReadSections(doc, placements, ras, ctx);
            foreach (var s in m.Sections)
            {
                ctx.Logger.LogDebug("SectionExtracted {Section} kind={Kind} objects={Objects}", s.Name, s.Kind, s.Objects.Count);
            }

            m.Subreports = SubreportWalker.Read(doc, m, ras, ctx, filePath, depth);

            m.Diagnostics = new List<Diagnostic>(ctx.Diagnostics.Items);
            ComputeStats(m);
            return m;
        }

        public static void ComputeStats(ReportMetadata m)
        {
            var s = m.Stats;
            s.SectionCount = m.Sections.Count;
            s.ObjectCount = 0;
            foreach (var sec in m.Sections)
            {
                s.ObjectCount += sec.Objects.Count;
            }

            s.FormulaCount = m.Formulas.Count;
            s.ParameterCount = m.Parameters.Count;
            s.TableCount = m.DataSource.Tables.Count;
            s.SubreportCount = m.Subreports.Count;
            s.WarningCount = 0;
            s.ErrorCount = 0;
            foreach (var d in m.Diagnostics)
            {
                if (d.Level == DiagnosticLevel.Warning) s.WarningCount++;
                else if (d.Level == DiagnosticLevel.Error) s.ErrorCount++;
            }
        }
    }
}
