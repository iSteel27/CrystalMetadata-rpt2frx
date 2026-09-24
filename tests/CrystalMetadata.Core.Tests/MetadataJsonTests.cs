using System;
using System.Collections.Generic;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CrystalMetadata.Core.Tests;

public class MetadataJsonTests
{
    public static ReportMetadata BuildFullSample()
    {
        var m = new ReportMetadata();
        m.ReportInfo = new ReportInfo { ReportName = "SalesRegister", FileName = "SalesRegister.rpt", FilePath = @"C:\Reports\SalesRegister.rpt", FileSizeBytes = 1234, RecordSelectionFormula = "{Orders.Date} >= {?FromDate}", CreatedUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        m.PageSetup = new PageSetup { PaperOrientation = "Portrait", PaperSize = "PaperA4", PageWidthTwips = 11906, Margins = new PageMargins { LeftTwips = 720 } };
        m.DataSource.Connections.Add(new ConnectionMetadata { Id = "c1", ServerName = "srv", DatabaseName = "db", LogonProperties = { ["Data Source"] = "srv" }, Attributes = { ["QE_SQLDB"] = true } });
        m.DataSource.Tables.Add(new TableMetadata
        {
            Alias = "Get_Sales", Kind = TableKind.StoredProcedure, ConnectionId = "c1",
            Fields = { new FieldMetadata { Name = "InvoiceNo", FormulaName = "{Get_Sales.InvoiceNo}", TableAlias = "Get_Sales", ValueType = "StringField", UseCount = 2, IsUsedInReport = true } }
        });
        m.DataSource.Links.Add(new TableLinkMetadata { SourceTable = "A", DestinationTable = "B", SourceFields = { "{A.Id}" }, DestinationFields = { "{B.AId}" }, JoinType = "LeftOuterJoin" });
        m.Parameters.Add(new ParameterMetadata { Name = "FromDate", FormulaName = "{?FromDate}", ValueType = "DateParameter", DefaultValues = { new ParameterValueMetadata { Value = "2024-01-01" } } });
        m.Parameters.Add(new ParameterMetadata { Name = "Pm-Customer.Code", FormulaName = "{?Pm-Customer.Code}", IsLinkedFromParentReport = true, LinkedSourceHint = "{Customer.Code}", ReportName = "Sub.rpt" });
        m.Formulas.Add(new FormulaMetadata { Name = "NetAmount", FormulaName = "{@NetAmount}", Text = "{Invoice.Amount}\r\n-\r\n{Invoice.Discount}", Syntax = "Crystal", ValueType = "NumberField" });
        m.SummaryFields.Add(new SummaryFieldMetadata { Name = "Sum of Amount", FormulaName = "Sum ({Orders.Amount}, {Customer.Name})", Operation = "Sum", SummarizedField = "{Orders.Amount}", GroupIndex = 1 });
        m.RunningTotals.Add(new RunningTotalMetadata { Name = "RT", FormulaName = "{#RT}", Operation = "Sum", EvaluationConditionType = "OnEachRecord" });
        m.Groups.Add(new GroupMetadata { Index = 1, ConditionField = "{Customer.Name}", SortDirection = "AscendingOrder" });
        m.SortFields.Add(new SortMetadata { Field = "{Customer.Name}", Direction = "AscendingOrder", SortType = "GroupSortField" });
        m.Areas.Add(new AreaMetadata { Name = "GroupHeaderSection1", Kind = "GroupHeader", GroupIndex = 1, SectionNames = { "Section3" } });

        var header = new SectionMetadata { Name = "Section2", Kind = "PageHeader", HeightTwips = 600, HeightPoints = 30 };
        header.Objects.Add(NewObject("Text1", ObjectKind.Text, "Section2", 0, 100, 100, 2000, 300, o =>
        {
            o.Text = new TextObjectMetadata { Text = "Page {PageNumber}", Paragraphs = { new ParagraphMetadata { Alignment = "Left", Elements = { new ParagraphElementMetadata { Kind = "Text", Text = "Page " }, new ParagraphElementMetadata { Kind = "Field", DataSource = "{PageNumber}" } } } } };
            o.Font = new FontMetadata { Name = "Arial", SizePoints = 9, Bold = true, Color = "#000000" };
            o.Format.ConditionFormulas.Add(new ConditionFormulaMetadata { Type = "Suppress", Text = "PageNumber = 1" });
        }));
        header.Objects.Add(NewObject("Heading1", ObjectKind.FieldHeading, "Section2", 1, 100, 400, 1500, 200, o => o.FieldHeading = new FieldHeadingMetadata { ForFieldObjectName = "Field1", Text = "Invoice No" }));
        header.Objects.Add(NewObject("Line1", ObjectKind.Line, "Section2", 2, 0, 590, 11000, 0, o => o.Line = new LineObjectMetadata { X1Twips = 0, Y1Twips = 590, X2Twips = 11000, Y2Twips = 590, Orientation = "Horizontal", ThicknessTwips = 15, Color = "#000000" }));
        header.Objects.Add(NewObject("Box1", ObjectKind.Box, "Section2", 3, 0, 0, 11000, 600, o => o.Box = new BoxObjectMetadata { LineStyle = "SingleLine", ThicknessTwips = 15 }));
        header.Objects.Add(NewObject("Picture1", ObjectKind.Picture, "Section2", 4, 9000, 0, 2000, 500, o => o.Picture = new PictureObjectMetadata { OriginalWidthTwips = 2000 }));
        m.Sections.Add(header);

        var detail = new SectionMetadata { Name = "Section3", Kind = "Detail", HeightTwips = 300, HeightPoints = 15 };
        detail.Objects.Add(NewObject("Field1", ObjectKind.Field, "Section3", 0, 100, 0, 1500, 280, o =>
        {
            o.Field = new FieldObjectMetadata { DataSource = "{Get_Sales.InvoiceNo}", FieldKind = "DatabaseField", FieldName = "InvoiceNo", TableAlias = "Get_Sales", ValueType = "StringField" };
            o.Format.HorizontalAlignment = "LeftAlign";
        }));
        detail.Objects.Add(NewObject("Field2", ObjectKind.Field, "Section3", 1, 2000, 0, 1500, 280, o =>
        {
            o.Field = new FieldObjectMetadata { DataSource = "{Get_Sales.Amount}", FieldKind = "DatabaseField", ValueType = "CurrencyField", FieldFormat = new FieldFormatMetadata { Numeric = new NumericFormatMetadata { DecimalPlaces = 2, ThousandsSeparator = true } } };
            o.Format.HorizontalAlignment = "RightAlign";
        }));
        detail.Objects.Add(NewObject("Subreport1", ObjectKind.Subreport, "Section3", 2, 0, 300, 11000, 1000, o => o.Subreport = new SubreportObjectMetadata { SubreportName = "Sub.rpt", IsOnDemand = false }));
        detail.Objects.Add(NewObject("Blob1", ObjectKind.Blob, "Section3", 3, 0, 0, 100, 100, o => o.Blob = new BlobObjectMetadata { DataSource = "{Emp.Photo}" }));
        detail.Objects.Add(NewObject("Chart1", ObjectKind.Chart, "Section3", 4, 0, 0, 100, 100, o => o.Chart = new ChartObjectMetadata { ChartType = "Bar" }));
        detail.Objects.Add(NewObject("CrossTab1", ObjectKind.CrossTab, "Section3", 5, 0, 0, 100, 100, o => o.CrossTab = new CrossTabObjectMetadata { Rows = { "{A.B}" } }));
        m.Sections.Add(detail);

        m.Subreports.Add(new SubreportMetadata
        {
            Name = "Sub.rpt",
            Placement = new SubreportPlacement { SectionName = "Section3", ObjectName = "Subreport1", WidthTwips = 11000, HeightTwips = 1000, ZIndex = 2 },
            Links = { new SubreportLinkMetadata { MainReportField = "{Customer.Code}", SubreportParameter = "{?Pm-Customer.Code}", LinkedParameterName = "Pm-Customer.Code" } },
            Report = new ReportMetadata { ReportInfo = new ReportInfo { ReportName = "Sub.rpt", IsSubreport = true } }
        });
        m.Diagnostics.Add(new Diagnostic { Level = DiagnosticLevel.Warning, Code = DiagnosticCodes.ObjectPropertyUnavailable, Scope = "Main", Path = "sections[0].objects[4]", Message = "x" });
        m.Stats = new ExtractionStats { SectionCount = 2, ObjectCount = 11, SubreportCount = 1, WarningCount = 1 };
        return m;
    }

    private static ObjectMetadata NewObject(string name, ObjectKind kind, string section, int z, int left, int top, int width, int height, Action<ObjectMetadata> configure)
    {
        var o = new ObjectMetadata { Name = name, Kind = kind, SectionName = section, ZIndex = z, LeftTwips = left, TopTwips = top, WidthTwips = width, HeightTwips = height };
        o.ComputeDerivedGeometry();
        configure(o);
        return o;
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var original = BuildFullSample();
        var json = MetadataJson.Serialize(original, indented: true);
        var back = MetadataJson.Deserialize<ReportMetadata>(json)!;
        var json2 = MetadataJson.Serialize(back, indented: true);
        Assert.Equal(json, json2);
        Assert.Equal("{Invoice.Amount}\r\n-\r\n{Invoice.Discount}", back.Formulas[0].Text);
        Assert.Equal(TableKind.StoredProcedure, back.DataSource.Tables[0].Kind);
        Assert.Equal("{PageNumber}", back.Sections[0].Objects[0].Text!.Paragraphs[0].Elements[1].DataSource);
        Assert.Equal(11, back.Sections[0].Objects.Count + back.Sections[1].Objects.Count);
        Assert.True(back.Subreports[0].Report!.ReportInfo.IsSubreport);
    }

    [Fact]
    public void Serialization_UsesCamelCaseEnumsAsStringsAndExplicitNulls()
    {
        var json = MetadataJson.Serialize(BuildFullSample());
        var root = JObject.Parse(json);

        Assert.NotNull(root["reportInfo"]);
        Assert.Null(root["ReportInfo"]);
        Assert.Equal("StoredProcedure", root["dataSource"]!["tables"]![0]!["kind"]!.Value<string>());
        Assert.Equal("Warning", root["diagnostics"]![0]!["level"]!.Value<string>());

        // explicit null = "not obtainable" must be present
        var field = root["dataSource"]!["tables"]![0]!["fields"]![0]!;
        Assert.Equal(JTokenType.Null, field["nullable"]!.Type);
        Assert.Equal(JTokenType.Null, root["reportInfo"]!["title"]!.Type);

        // dictionary keys keep their original casing
        Assert.NotNull(root["dataSource"]!["connections"]![0]!["logonProperties"]!["Data Source"]);
        Assert.NotNull(root["dataSource"]!["connections"]![0]!["attributes"]!["QE_SQLDB"]);

        // dates are ISO-8601 UTC (checked on the raw text: JToken would re-parse the value as a DateTime)
        Assert.Contains("\"createdUtc\":\"2024-01-02T03:04:05.000Z\"", json);
    }

    [Fact]
    public void KindSpecificMembers_AreOmittedWhenNotApplicable()
    {
        var json = MetadataJson.Serialize(BuildFullSample());
        var root = JObject.Parse(json);
        var textObject = (JObject)root["sections"]![0]!["objects"]![0]!;
        Assert.NotNull(textObject["text"]);
        Assert.Null(textObject["field"]);
        Assert.Null(textObject["line"]);
        Assert.Null(textObject["subreport"]);

        var fieldObject = (JObject)root["sections"]![1]!["objects"]![0]!;
        Assert.NotNull(fieldObject["field"]);
        Assert.Null(fieldObject["text"]);
        // common members are always present even when null
        Assert.True(fieldObject.ContainsKey("border"));
        Assert.True(fieldObject.ContainsKey("font"));
    }

    [Fact]
    public void Derived_IsOmittedUntilComputed()
    {
        var m = BuildFullSample();
        Assert.DoesNotContain("\"derived\"", MetadataJson.Serialize(m));
        m.Derived = new DerivedLayout();
        Assert.Contains("\"derived\"", MetadataJson.Serialize(m));
    }

    [Fact]
    public void ComputeDerivedGeometry_ProducesPointsAndRightBottom()
    {
        var o = new ObjectMetadata { LeftTwips = 1440, TopTwips = 720, WidthTwips = 2880, HeightTwips = 20 };
        o.ComputeDerivedGeometry();
        Assert.Equal(4320, o.RightTwips);
        Assert.Equal(740, o.BottomTwips);
        Assert.Equal(72.0, o.LeftPoints);
        Assert.Equal(36.0, o.TopPoints);
        Assert.Equal(144.0, o.WidthPoints);
        Assert.Equal(1.0, o.HeightPoints);
    }

    [Theory]
    [InlineData(0x0000FF, "#FF0000")] // COLORREF is BGR
    [InlineData(0x00FF00, "#00FF00")]
    [InlineData(0xFF0000, "#0000FF")]
    public void FormatColorRef_ConvertsBgrToRgbHex(int colorRef, string expected)
    {
        Assert.Equal(expected, MetadataJson.FormatColorRef(colorRef));
    }

    [Fact]
    public void Units_ConvertTwips()
    {
        Assert.Equal(72.0, Units.ToPoints(1440));
        Assert.Equal(25.4, Units.ToMm(1440));
        Assert.Equal(1.0, Units.ToInches(1440));
    }
}
