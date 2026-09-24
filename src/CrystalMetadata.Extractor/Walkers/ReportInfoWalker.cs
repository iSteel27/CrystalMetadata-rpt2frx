using System;
using System.IO;
using CrystalDecisions.CrystalReports.Engine;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>Report-level information and page setup (Engine API).</summary>
    internal static class ReportInfoWalker
    {
        public static ReportInfo ReadInfo(ReportDocument doc, string? filePath, bool isSubreport, ExtractionContext ctx)
        {
            var info = new ReportInfo { IsSubreport = isSubreport };

            if (!isSubreport && !string.IsNullOrEmpty(filePath))
            {
                ctx.Try("reportInfo.file", DiagnosticCodes.ReportInfoFailed, () =>
                {
                    var fi = new FileInfo(filePath!);
                    info.FilePath = fi.FullName;
                    info.FileName = fi.Name;
                    info.ReportName = Path.GetFileNameWithoutExtension(fi.Name);
                    if (fi.Exists)
                    {
                        info.FileSizeBytes = fi.Length;
                        info.CreatedUtc = fi.CreationTimeUtc;
                        info.ModifiedUtc = fi.LastWriteTimeUtc;
                    }
                });
            }
            else
            {
                var name = ctx.QuietRef(() => doc.Name) ?? string.Empty;
                info.ReportName = name;
                info.FileName = name;
                info.FilePath = "<embedded in: " + (filePath ?? "main report") + ">";
            }

            // SummaryInfo / ReportOptions are document-level: the SDK throws "Not supported within subreports"
            if (!isSubreport)
            {
                ctx.Try("reportInfo.summaryInfo", DiagnosticCodes.ReportInfoFailed, () =>
                {
                    var si = doc.SummaryInfo;
                    info.Title = Text.NullIfEmpty(si.ReportTitle);
                    info.Author = Text.NullIfEmpty(si.ReportAuthor);
                    info.Subject = Text.NullIfEmpty(si.ReportSubject);
                    info.Comments = Text.NullIfEmpty(si.ReportComments);
                    info.Keywords = Text.NullIfEmpty(si.KeywordsInReport);
                }, DiagnosticLevel.Info);

                ctx.Try("reportInfo.reportOptions", DiagnosticCodes.ReportInfoFailed, () =>
                {
                    var ro = doc.ReportOptions;
                    info.HasSavedData = ro.EnableSaveDataWithReport;
                    info.SelectDistinctRecords = Reflect.GetBool(ro, "EnableSelectDistinctRecords");
                }, DiagnosticLevel.Info);
            }

            ctx.Try("reportInfo.recordSelectionFormula", DiagnosticCodes.ReportInfoFailed, () =>
            {
                info.RecordSelectionFormula = doc.RecordSelectionFormula ?? string.Empty;
            });

            ctx.Try("reportInfo.groupSelectionFormula", DiagnosticCodes.ReportInfoFailed, () =>
            {
                info.GroupSelectionFormula = doc.DataDefinition.GroupSelectionFormula ?? string.Empty;
            }, DiagnosticLevel.Info);

            return info;
        }

        public static PageSetup ReadPageSetup(ReportDocument doc, bool isSubreport, ExtractionContext ctx)
        {
            var ps = new PageSetup();
            if (isSubreport)
            {
                // PrintOptions throws "Not supported within subreports"; a subreport inherits the parent's page.
                return ps;
            }

            ctx.Try("pageSetup", DiagnosticCodes.PageSetupFailed, () =>
            {
                var po = doc.PrintOptions;
                ps.PaperOrientation = Text.Of(po.PaperOrientation);
                ps.PaperSize = Text.Of(po.PaperSize);
                ps.PrinterName = Text.NullIfEmpty(ctx.QuietRef(() => po.PrinterName));
                ps.PrintableWidthTwips = ctx.Quiet(() => po.PageContentWidth);
                ps.PrintableHeightTwips = ctx.Quiet(() => po.PageContentHeight);
                ps.DissociatePageSizeFromPrinter = ctx.Quiet(() => po.DissociatePageSizeAndPrinterPaperSize);

                object margins = po.PageMargins;
                ps.Margins.LeftTwips = Reflect.GetInt(margins, "leftMargin") ?? Reflect.GetInt(margins, "LeftMargin");
                ps.Margins.RightTwips = Reflect.GetInt(margins, "rightMargin") ?? Reflect.GetInt(margins, "RightMargin");
                ps.Margins.TopTwips = Reflect.GetInt(margins, "topMargin") ?? Reflect.GetInt(margins, "TopMargin");
                ps.Margins.BottomTwips = Reflect.GetInt(margins, "bottomMargin") ?? Reflect.GetInt(margins, "BottomMargin");

                if (ps.PrintableWidthTwips.HasValue && ps.Margins.LeftTwips.HasValue && ps.Margins.RightTwips.HasValue)
                {
                    ps.PageWidthTwips = ps.PrintableWidthTwips + ps.Margins.LeftTwips + ps.Margins.RightTwips;
                }

                if (ps.PrintableHeightTwips.HasValue && ps.Margins.TopTwips.HasValue && ps.Margins.BottomTwips.HasValue)
                {
                    ps.PageHeightTwips = ps.PrintableHeightTwips + ps.Margins.TopTwips + ps.Margins.BottomTwips;
                }
            });
            return ps;
        }
    }
}
