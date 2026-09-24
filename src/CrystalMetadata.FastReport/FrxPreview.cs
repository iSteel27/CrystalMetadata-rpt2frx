using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using CrystalMetadata.Core.Models;
using FastReport;
using FastReport.Export.Image;
using FastReport.Export.PdfSimple;

namespace CrystalMetadata.FastReport;

/// <summary>
/// Renders a generated template with the FastReport open source engine, using synthetic sample rows built from the
/// column names and types in the metadata (no real report data is read). Used to eyeball the result and as a smoke
/// test that the .frx loads on the open source edition.
/// </summary>
public static class FrxPreview
{
    /// <summary>Builds the sample data set the template's data source (Data.&lt;alias&gt;) is bound to.</summary>
    public static DataSet BuildSampleData(ReportMetadata metadata, int rows = 12)
    {
        var ds = new DataSet("Data");
        var groupFields = new HashSet<string>(metadata.Groups.Select(g => (g.ConditionField ?? "").Trim('{', '}').Split('.').Last()), StringComparer.OrdinalIgnoreCase);

        foreach (var t in metadata.DataSource.Tables.Take(1))
        {
            var table = ds.Tables.Add(t.Alias);
            foreach (var f in t.Fields)
            {
                table.Columns.Add(f.Name, ClrType(f.ValueType));
            }

            for (var i = 0; i < rows; i++)
            {
                var row = table.NewRow();
                foreach (var f in t.Fields)
                {
                    var type = table.Columns[f.Name]!.DataType;
                    // group columns cycle through 3 values so groups and group totals are visible
                    var n = groupFields.Contains(f.Name) ? i / Math.Max(1, rows / 3) + 1 : i + 1;
                    row[f.Name] = Sample(type, f.Name, n, i);
                }

                table.Rows.Add(row);
            }
        }

        return ds;
    }

    private static object Sample(Type type, string column, int n, int i)
    {
        if (type == typeof(double)) return Math.Round(10 + (i % 5) * 7.5 + i * 1.25, 2);
        if (type == typeof(decimal)) return Math.Round(100m + i * 12.5m, 2);
        if (type == typeof(int)) return i + 1;
        if (type == typeof(DateTime)) return new DateTime(2026, 9, 1).AddDays(i);
        if (type == typeof(bool)) return i % 2 == 0;
        // UOM-like columns alternate between litres and kilos so both branches of typical formulas run
        if (column.Contains("uom", StringComparison.OrdinalIgnoreCase)) return i % 2 == 0 ? "L" : "KG";
        return column + " " + n;
    }

    private static Type ClrType(string? crystalType)
    {
        var t = crystalType ?? string.Empty;
        if (t.Contains("Currency", StringComparison.OrdinalIgnoreCase)) return typeof(decimal);
        if (t.Contains("Number", StringComparison.OrdinalIgnoreCase)) return typeof(double);
        if (t.Contains("Int", StringComparison.OrdinalIgnoreCase)) return typeof(int);
        if (t.Contains("Date", StringComparison.OrdinalIgnoreCase)) return typeof(DateTime);
        if (t.Contains("Boolean", StringComparison.OrdinalIgnoreCase)) return typeof(bool);
        return typeof(string);
    }

    /// <summary>
    /// Renders a one-line test page in memory. Returns null when the engine works, otherwise the reason it does not
    /// (missing library, blocked file, no graphics support). Used by the "check my setup" command.
    /// </summary>
    public static string? SelfTest()
    {
        try
        {
            const string frx = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Report ScriptLanguage=\"CSharp\"><ReportPage Name=\"Page1\">" +
                               "<ReportTitleBand Name=\"Title1\" Width=\"718.2\" Height=\"37.8\"><TextObject Name=\"Text1\" Width=\"300\" Height=\"20\" " +
                               "Text=\"CrystalMetadata self-test\" AllowExpressions=\"false\"/></ReportTitleBand></ReportPage></Report>";
            using var report = new Report();
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(frx));
            report.Load(input);
            report.Prepare();
            using var output = new MemoryStream();
            report.Export(new PDFSimpleExport(), output);
            return output.Length > 1000 ? null : "the test page came out empty";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Loads the .frx, binds sample data, prepares the report and exports it. Returns the files written.</summary>
    public static IReadOnlyList<string> Render(string frxPath, ReportMetadata metadata, string outputBasePath, int sampleRows = 12, bool pdf = true, bool png = true)
    {
        var written = new List<string>();
        using var report = new Report();
        report.Load(frxPath);
        report.RegisterData(BuildSampleData(metadata, sampleRows), "Data");
        foreach (var t in metadata.DataSource.Tables.Take(1))
        {
            var source = report.GetDataSource(t.Alias);
            if (source != null) source.Enabled = true;
        }

        report.Prepare();

        if (pdf)
        {
            var path = outputBasePath + ".pdf";
            report.Export(new PDFSimpleExport(), path);
            written.Add(path);
        }

        if (png)
        {
            var export = new ImageExport { ImageFormat = ImageExportFormat.Png, SeparateFiles = true, ResolutionX = 110, ResolutionY = 110 };
            var pngPath = outputBasePath + ".png";
            report.Export(export, pngPath);
            written.AddRange(Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(pngPath))!, Path.GetFileNameWithoutExtension(pngPath) + "*.png"));
        }

        return written;
    }
}
