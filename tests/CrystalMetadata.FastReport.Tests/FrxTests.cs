using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.FastReport;
using Xunit;

namespace CrystalMetadata.FastReport.Tests;

public class FormulaTranslatorTests
{
    private static string? None(string _) => null;

    [Fact]
    public void IfThenElse_BecomesIIf_WithFieldsAndOperators()
    {
        var info = CrystalFormulaTranslator.Analyze("If({T.uom}=\"L\" or {T.uom}=\"ML\")Then\r\n{T.vol}*{T.nop}\r\nelse\r\n0", None);
        Assert.Equal(FormulaKind.Pure, info.Kind);
        Assert.Equal("(([T.uom]==\"L\" || [T.uom]==\"ML\") ? [T.vol]*[T.nop] : 0)", info.Expression);
    }

    [Fact]
    public void NotEqualAndAnd_AreTranslated()
    {
        var info = CrystalFormulaTranslator.Analyze("If {T.a}<>\"L\" and {T.a}<>\"ML\" Then 1 else 0", None);
        Assert.Equal("([T.a]!=\"L\" && [T.a]!=\"ML\" ? 1 : 0)", info.Expression);
    }

    [Fact]
    public void RunningSumHelpers_AreClassified()
    {
        string? Resolve(string n) => n == "LTR" ? "{T.x}*2" : null;

        var acc = CrystalFormulaTranslator.Analyze("whileprintingrecords;\r\nnumbervar totLtr;\r\ntotLtr:=totLtr+{@LTR}", Resolve);
        Assert.Equal(FormulaKind.Accumulator, acc.Kind);
        Assert.Equal("totLtr", acc.Variable);
        Assert.Equal("([T.x]*2)", acc.Expression);

        var show = CrystalFormulaTranslator.Analyze("whileprintingrecords;\r\nnumbervar totLtr;\r\ntotLtr", Resolve);
        Assert.Equal(FormulaKind.Show, show.Kind);

        var reset = CrystalFormulaTranslator.Analyze("whileprintingrecords;\r\nnumbervar totLtr;\r\n\r\ntotLtr:=0;", Resolve);
        Assert.Equal(FormulaKind.Reset, reset.Kind);
    }

    [Theory]
    [InlineData("ToText({T.a}, 2)")]
    [InlineData("stringvar s; s := \"x\"; s")]
    [InlineData("For i := 1 to 3 do 1")]
    public void Unsupported_IsReportedNotGuessed(string formula)
    {
        var info = CrystalFormulaTranslator.Analyze(formula, None);
        Assert.Equal(FormulaKind.Unsupported, info.Kind);
        Assert.False(string.IsNullOrEmpty(info.Reason));
    }

    [Fact]
    public void KeywordsInsideStringLiterals_AreIgnored()
    {
        var info = CrystalFormulaTranslator.Analyze("\"select for while\"", None);
        Assert.Equal(FormulaKind.Pure, info.Kind);
    }
}

public class FrxGeneratorTests
{
    private static ReportMetadata Depot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Depot_Despatch_Unitwise_Report.metadata.json");
        return MetadataJson.ReadFile<ReportMetadata>(path)!;
    }

    [Fact]
    public void DepotReport_ConvertsWithOnlyThePictureWarning()
    {
        var result = new FrxGenerator().Generate(Depot());
        Assert.Single(result.Warnings);
        Assert.Contains("Picture", result.Warnings[0]);
    }

    [Fact]
    public void DepotReport_ProducesExpectedStructure()
    {
        var doc = XDocument.Parse(new FrxGenerator().Generate(Depot()).Xml);
        var report = doc.Root!;

        // page: Letter landscape from twips
        var page = report.Element("ReportPage")!;
        Assert.Equal("true", (string?)page.Attribute("Landscape"));
        Assert.Equal("279.4", (string?)page.Attribute("PaperWidth"));
        Assert.Equal("215.9", (string?)page.Attribute("PaperHeight"));

        // one group on the vendor unit, data band inside it, footer inside it
        var group = page.Element("GroupHeaderBand")!;
        Assert.Equal("[Depot_Despatched_Unitwise_Rpt.load_vend_unit]", (string?)group.Attribute("Condition"));
        Assert.NotNull(group.Element("DataBand"));
        Assert.NotNull(group.Element("GroupFooterBand"));

        // the three Crystal accumulator helpers became three native Totals reset per group
        var totals = report.Element("Dictionary")!.Elements("Total").ToList();
        Assert.Equal(3, totals.Count);
        Assert.All(totals, t => Assert.Equal("true", (string?)t.Attribute("ResetAfterPrint")));
        Assert.Contains(totals, t => ((string?)t.Attribute("Expression"))!.Contains(" ? ", StringComparison.Ordinal));

        // hidden helper fields are not emitted; row counter and page numbering are translated
        var texts = report.Descendants("TextObject").Select(t => (string?)t.Attribute("Text")).ToList();
        Assert.DoesNotContain(texts, t => t != null && t.Contains("numbervar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[Tot_totLtr_GroupFooter1]", texts);
        Assert.Contains("[Row#]", texts);
        Assert.Contains("Page [Page#] of [TotalPages#]", texts);
    }

    [Fact]
    public void FilledBoxes_AreEmittedBeforeTheTextTheyFrame()
    {
        var doc = XDocument.Parse(new FrxGenerator().Generate(Depot()).Xml);
        var header = doc.Descendants("GroupHeaderBand").First();
        var children = header.Elements().Where(e => e.Name == "ShapeObject" || e.Name == "TextObject").ToList();
        var firstText = children.FindIndex(e => e.Name == "TextObject");
        var lastFilledShape = children.FindLastIndex(e => e.Name == "ShapeObject" && e.Attribute("Fill.Color") != null);
        Assert.True(lastFilledShape < firstText, "filled boxes must be painted before text");
    }

    [Fact]
    public void ConditionalSuppress_BecomesVisibleExpression_NotHidden()
    {
        // LTR/KG have static Suppress=true but a formula ({@LTR}=0) overriding it: visible unless the value is zero
        var doc = XDocument.Parse(new FrxGenerator().Generate(Depot()).Xml);
        var conditional = doc.Descendants("TextObject").Where(t => t.Attribute("VisibleExpression") != null).ToList();
        Assert.Equal(2, conditional.Count);
        Assert.All(conditional, t => Assert.Null(t.Attribute("Visible")));
        Assert.All(conditional, t => Assert.StartsWith("!(", (string)t.Attribute("VisibleExpression")!));
    }

    [Fact]
    public void TransparentBackgrounds_AreNotFilledWhite_AndCornersAreRounded()
    {
        var doc = XDocument.Parse(new FrxGenerator().Generate(Depot()).Xml);
        Assert.DoesNotContain(doc.Descendants("TextObject"), t => (string?)t.Attribute("Fill.Color") == "#FFFFFF");
        var rounded = doc.Descendants("ShapeObject").Single(s => (string?)s.Attribute("Shape") == "RoundRectangle");
        Assert.NotNull(rounded.Attribute("Curve"));
    }

    [Fact]
    public void FontScale_ScalesEverySize_AndDefaultsToExactSizes()
    {
        var exact = XDocument.Parse(new FrxGenerator().Generate(Depot()).Xml);
        var scaled = XDocument.Parse(new FrxGenerator().Generate(Depot(), new FrxOptions { FontScale = 0.5 }).Xml);
        Assert.Contains(exact.Descendants("TextObject"), t => ((string?)t.Attribute("Font"))!.Contains("16pt"));
        Assert.Contains(scaled.Descendants("TextObject"), t => ((string?)t.Attribute("Font"))!.Contains("8pt"));
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        var a = new FrxGenerator().Generate(Depot()).Xml;
        var b = new FrxGenerator().Generate(Depot()).Xml;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Template_LoadsAndRendersOnFastReportOpenSource()
    {
        var metadata = Depot();
        var dir = Path.Combine(Path.GetTempPath(), "frx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var frx = Path.Combine(dir, "depot.frx");
            File.WriteAllText(frx, new FrxGenerator().Generate(metadata).Xml);

            var files = FrxPreview.Render(frx, metadata, Path.Combine(dir, "depot.preview"), sampleRows: 9, pdf: true, png: true);

            Assert.Contains(files, f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 10_000);
            Assert.Contains(files, f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 5_000);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
