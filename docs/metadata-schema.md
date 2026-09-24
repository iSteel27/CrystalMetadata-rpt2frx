# ReportMetadata — property reference (schemaVersion 1.0)

One document per `.rpt`. `null` always means "not obtainable from Crystal for this report/object"; kind-specific
sub-objects of a report object (`field`, `text`, `line`, …) are **omitted** when they do not apply. Enum values are the
SDK's own enum member names (`Engine` = `CrystalDecisions.CrystalReports.Engine` / `CrystalDecisions.Shared`;
`RAS` = in-process `CrystalDecisions.ReportAppServer.*`). Positions/sizes are twips (1440 per inch, 20 per point);
every twips value has a `…Points` sibling. Colours are `#RRGGBB`. Field references use Crystal formula-name syntax:
`{Table.Field}`, `{@Formula}`, `{?Parameter}`, `{#RunningTotal}`, `{%SqlExpression}`, `{PageNumber}`.

A machine-readable JSON Schema can be generated with `CrystalMetadata.Console schema --out metadata.schema.json`.

## Root

| Property | Type | Origin | Notes |
|---|---|---|---|
| `schemaVersion` | string | — | `"1.0"` |
| `extractor` | object | — | `version`, `crystalRuntimeVersion`, `bitness`, `extractedAtUtc`, `durationMs` |
| `units` | object | — | `position: "twips"`, `font: "points"`, `twipsPerInch: 1440`, `twipsPerPoint: 20`, `twipsPerMm: 56.6929` |
| `reportInfo` | ReportInfo | Engine | see below |
| `pageSetup` | PageSetup | Engine (+RAS fallback) | subreports: all null (Crystal has no page setup inside subreports) |
| `dataSource` | DataSource | Engine + RAS | connections, tables, fields, joins |
| `parameters[]` | Parameter | Engine + RAS | main report includes linked subreport parameters (with `reportName`) |
| `formulas[]` | Formula | Engine + RAS | text verbatim |
| `sqlExpressions[]` | SqlExpression | Engine | |
| `customFunctions[]` | CustomFunction | RAS | report-embedded custom functions used by formulas; main report only |
| `summaryFields[]` | SummaryField | Engine | |
| `runningTotals[]` | RunningTotal | Engine | |
| `groups[]` | Group | Engine + RAS | 1-based `index` |
| `sortFields[]` | Sort | Engine | `sortType` `GroupSortField` / `RecordSortField` |
| `areas[]` | Area | Engine + RAS | band types with their section names |
| `sections[]` | Section | Engine + RAS | **print order; canonical home of every object** |
| `subreports[]` | Subreport | Engine + RAS | placement, links, full nested `report` |
| `derived` | DerivedLayout | client | present only when hints were computed |
| `diagnostics[]` | Diagnostic | — | `level` Info/Warning/Error, `code`, `scope` (Main or subreport name), `path` (JSON path), `message`, `exception` |
| `stats` | object | — | `sectionCount`, `objectCount`, `formulaCount`, `parameterCount`, `tableCount`, `subreportCount`, `warningCount`, `errorCount` (this report only; subreports carry their own) |

## reportInfo

| Property | Origin | Null when |
|---|---|---|
| `reportName`, `fileName`, `filePath`, `fileSizeBytes`, `createdUtc`, `modifiedUtc` | file system | subreport (`filePath` = `<embedded in: …>`, size/dates null) |
| `reportFileVersion` | RAS `MajorVersion.MinorVersion` | RAS unavailable |
| `title`, `author`, `subject`, `comments`, `keywords` | Engine `SummaryInfo` | empty in the file |
| `hasSavedData` | Engine `ReportOptions.EnableSaveDataWithReport` | |
| `selectDistinctRecords` | Engine `ReportOptions` (reflection; SP-dependent) | property missing |
| `recordSelectionFormula` | Engine `RecordSelectionFormula` | never (empty string when none) |
| `groupSelectionFormula` | Engine `DataDefinition.GroupSelectionFormula` | |
| `isSubreport` | — | |

## pageSetup

`paperOrientation`, `paperSize` (enum names), `printerName`, `pageWidthTwips`/`pageHeightTwips` (= printable + margins),
`margins.{left,right,top,bottom}Twips`, `printableWidthTwips`/`printableHeightTwips` (`PrintOptions.PageContentWidth/Height`),
`dissociatePageSizeFromPrinter`. Origin: Engine `PrintOptions`; RAS `PrintOutputController.GetPrintOptions()` fills gaps.

## dataSource

**connections[]** — deduplicated by (server, database, dll, provider, type). `id` (`c1`, `c2`…), `serverName`, `databaseName`,
`userId`, `integratedSecurity`, `connectionType` (Engine `ConnectionInfo.Type`), `databaseDll` (e.g. `crdb_ado.dll`,
`crdb_adoplus.dll` = ADO.NET DataSet), `provider` (from logon properties), `logonProperties{}` (Engine `LogonProperties`
+ RAS `QE_LogonProperties`), `attributes{}` (RAS attribute bag: `QE_DatabaseName`, `QE_ServerDescription`, `QE_SQLDB`, …).
Passwords and any key containing "password"/"pwd" are never emitted.

**tables[]** — `alias` (what `{Alias.Field}` refers to), `name`, `location` (qualified DB object, e.g.
`DB.dbo.Get_Sales`; the dataset name for ADO.NET sources), `qualifiedName` (RAS), `kind`
(`Table` / `View` / `StoredProcedure` / `Command` / `Unknown`), `connectionId`, `commandText` (verbatim SQL, Command objects
only), `commandParameters[]` (`name`, `valueType`, `defaultValue`; SP or command parameters), `fields[]`, `raw{}`.

Kind detection: `commandText` present → Command; RAS `Parameters` collection or class name containing "Procedure" → StoredProcedure;
otherwise Table. Views cannot be told apart from tables by the SDK.

**fields[]** — `name`, `formulaName` (`{Alias.Field}`), `tableAlias`, `valueType` (Engine `FieldValueType`: `StringField`,
`NumberField`, `CurrencyField`, `DateField`, `DateTimeField`, `TimeField`, `BooleanField`, `BlobField`, `PersistentMemoField`,
`Int32sField`…), `lengthBytes` (`NumberOfBytes`), `nullable` (**always null** — not exposed), `useCount`, `isUsedInReport`
(`useCount > 0`), `headingText`, `raw{}` (`RasType`, `RasLength` when RAS available). Unused fields are included unless the
request sets `includeUnusedFields=false`.

**links[]** — Engine `Database.Links`: `sourceTable`, `destinationTable`, `sourceFields[]`, `destinationFields[]`,
`joinType`, `linkOperator` (RAS), `raw{}`.

**generatedSql** — RAS `RowsetController.GetSQLStatement`; only when `includeGeneratedSql=true` (may try to connect).

## parameters[]

`name`, `formulaName` (`{?Name}`), `valueType` (`StringParameter`, `NumberParameter`, `CurrencyParameter`, `BooleanParameter`,
`DateParameter`, `DateTimeParameter`, `TimeParameter`), `parameterType` (`ReportParameter`, `QueryParameter`,
`StoreProcedureParameter`), `discreteOrRange` (`DiscreteValue`, `RangeValue`, `DiscreteAndRangeValue`), `allowMultipleValues`,
`allowNullValue`, `isOptionalPrompt`, `promptText`, `editMask`, `minimumValue`, `maximumValue`, `defaultValues[]` /
`currentValues[]` (`value`, `description`, or `rangeStart`/`rangeEnd`/`lowerBoundType`/`upperBoundType`), `hasCurrentValue`,
`valueListKind` (RAS; static vs dynamic LOV), `reportName` (empty for own parameters; subreport name for a linked subreport's
parameter surfaced in the main report), `isLinkedFromParentReport`, `linkedSourceHint` (parent-side field, from the `Pm-`
name or the RAS link), `usage` (Engine `ParameterFieldUsage2` flags, e.g. `InUse, DataFetching`), `raw{}`.

## formulas[] / sqlExpressions[] / customFunctions[]

`name`, `formulaName` (`{@Name}` / `{%Name}`), `text` (**verbatim, CRLF preserved, never parsed**), `syntax` (`Crystal` |
`Basic`, RAS), `valueType`, `useCount`. Custom functions: `name`, `text`, `syntax`.

## summaryFields[] / runningTotals[]

Summary: `name`, `formulaName` (e.g. `Sum ({Orders.Amount}, {Customer.Name})`), `operation` (`Sum`, `Count`, `Average`,
`DistinctCount`, `Maximum`, …), `operationParameter`, `summarizedField`, `secondarySummarizedField`, `groupIndex`
(1-based; null = grand total), `groupConditionField`, `valueType`.
Running total: `name`, `formulaName` (`{#Name}`), `operation`, `operationParameter`, `summarizedField`,
`secondarySummarizedField`, `evaluationConditionType`, `evaluationCondition` (field, `Group({field})` or formula text),
`resetConditionType`, `resetCondition`, `valueType`, `raw{}`.

## groups[] / sortFields[]

Group: `index`, `conditionField`, `conditionFieldKind`, `sortDirection` (from the matching `GroupSortField` entry:
`AscendingOrder`, `DescendingOrder`, `SpecifiedOrder`, `OriginalOrder`, `NoSortOrder`; null only when the report has no
sort entry for that group), `condition` (the SDK's numeric `GroupOptions.Condition` code as text; `0` = the default
"on any change of value" for non-date fields; date/time/boolean
grouping condition, e.g. `DailyGroupCondition`), `keepGroupTogether`, `repeatGroupHeader`, `customGroupNameFormula`,
`specifiedOrderNames[]`, `topN` (RAS; null when not set), `raw{}` (all scalar `GroupOptions` properties).
Sort: `field`, `direction`, `sortType`.

## areas[] / sections[]

Area: `name` (`ReportHeaderSection1`, `GroupHeaderSection2`, `DetailSection1`, …), `kind` (`ReportHeader`, `PageHeader`,
`GroupHeader`, `Detail`, `GroupFooter`, `PageFooter`, `ReportFooter`), `groupIndex` (GroupHeader areas 1..n in order,
GroupFooter areas n..1), `sectionNames[]`, `format` (`suppress`, `keepTogether`, `newPageBefore`, `newPageAfter`,
`printAtBottomOfPage`, `resetPageNumberAfter`, `hideForDrillDown`, `conditionFormulas[]`).

Section: `name` (Crystal's internal name, e.g. `Section3`, `GroupHeaderSection5`), `kind`, `areaName`, `groupIndex`,
`indexInArea` (0 = "a", 1 = "b", …), `heightTwips`, `heightPoints`, `format` (`suppress`, `suppressIfBlank`, `newPageBefore`,
`newPageAfter`, `keepTogether`, `printAtBottomOfPage`, `resetPageNumberAfter`, `underlayFollowingSections`, `backgroundColor`,
`cssClass`, `conditionFormulas[]`), `objects[]`.

`conditionFormulas[]` entries are `{ "type": "Suppress" | "EnableNewPageAfter" | "BackgroundColor" | …, "text": "<formula>" }`
— RAS `CrConditionFormulaTypeEnum` names with the `crConditionFormulaType` prefix removed; only formulas with text are listed.

## Reading suppress and background correctly

- **`format.suppress` alone does not mean "hidden".** In Crystal a suppress *formula* overrides the checkbox, yet the SDK
  keeps reporting the checkbox as `true`. An object is hidden only when `suppress == true` **and** `conditionFormulas`
  has no `EnableSuppress` entry; with such an entry the object prints unless the formula is true. The helpers
  `IsAlwaysSuppressed()` / `HasConditionalSuppress()` / `SuppressFormula()` in `CrystalMetadata.Core.Models.MetadataExtensions`
  implement this rule (same for `sections[].format`).
- **`border.backgroundColor: null` means transparent.** RAS marks "no background" with `0xFFFFFFFF`; the Engine API would
  report white. A real fill has a colour. (Metadata produced without RAS may still show `#FFFFFF` for transparent.)
- Box corner radius (`box.cornerEllipseWidthTwips` / `Height`) comes only from RAS.
- `zIndex` is the SDK list order, not a guaranteed paint order: Crystal paints filled boxes behind the text they frame.

## Report objects (`sections[].objects[]`)

Common (always present): `name` (Crystal object name — stable id), `kind` (`Field`, `FieldHeading`, `Text`, `Line`, `Box`,
`Picture`, `Blob`, `Subreport`, `Chart`, `CrossTab`, `Map`, `OlapGrid`, `Unknown`), `sectionName`, `zIndex` (position in
the section's object list = paint order), `leftTwips`, `topTwips`, `widthTwips`, `heightTwips`, `rightTwips`,
`bottomTwips`, `leftPoints`, `topPoints`, `widthPoints`, `heightPoints`,
`format` (`suppress`, `canGrow`, `keepTogether`, `closeAtPageBreak`, `horizontalAlignment` (`LeftAlign`, `HorizontalCenterAlign`,
`RightAlign`, `Justified`, `DefaultAlign`), `cssClass`, `hyperlinkType`, `hyperlinkText`, `conditionFormulas[]`),
`border` (`left`/`right`/`top`/`bottom` line styles `NoLine`/`SingleLine`/`DoubleLine`/`DashLine`/`DotLine`, `borderColor`,
`backgroundColor`, `hasDropShadow`, `conditionFormulas[]`), `font` (`name`, `sizePoints`, `bold`, `italic`, `underline`,
`strikeout`, `color`, `conditionFormulas[]` — null for lines/boxes/pictures), `raw{}` (unmapped scalar SDK properties,
RAS ones prefixed `Ras`).

Kind-specific:

| Kind | Member | Content |
|---|---|---|
| Field | `field` | `dataSource` (formula name), `fieldKind` (`DatabaseField`, `FormulaField`, `ParameterField`, `SummaryField`, `RunningTotalField`, `SpecialVarField`, `GroupNameField`, `SQLExpressionField`, `UnboundField`), `fieldName`, `tableAlias`, `valueType`, `fieldFormat` (`suppressIfDuplicated`; `numeric` {`decimalPlaces`, `thousandsSeparator`, `thousandsSeparatorSymbol`, `decimalSymbol`, `currencySymbol`, `currencySymbolFormat`, `currencyPositionFormat`, `negativeFormat`, `useLeadingZero`, `roundingFormat`, `raw`} for numeric/currency/summary fields; `date` {`dayFormat`, `monthFormat`, `yearFormat`, `dateOrder`, `separator`, `raw`}; `time` {raw}; `dateTime` {`order`, `raw`}; `boolean` {`outputType`}; `conditionFormulas[]` prefixed by part, e.g. `NumericFormat.DecimalPlaces`) |
| FieldHeading | `fieldHeading` + `text` | `forFieldObjectName` (the Field object this heading belongs to), `text`; `text.text` mirrors it |
| Text | `text` | `text` (embedded fields rendered as `{FormulaName}`), `paragraphs[]` (`alignment`, `elements[]` {`kind` Text/Field, `text`, `dataSource`, `font`}) from RAS; empty `paragraphs` when RAS unavailable |
| Line | `line` | `x1Twips`, `y1Twips`, `x2Twips`, `y2Twips` (RAS Right/Bottom when available), `orientation`, `lineStyle`, `thicknessTwips`, `color`, `extendToBottomOfSection`, `endSectionName` |
| Box | `box` | `lineStyle`, `thicknessTwips`, `lineColor`, `fillColor`, `cornerEllipseWidthTwips`, `cornerEllipseHeightTwips`, `extendToBottomOfSection`, `endSectionName` |
| Picture | `picture` | `originalWidthTwips`, `originalHeightTwips`, `xScaling`, `yScaling`, `cropTwips{}`, `imageBytesAvailable` (**always false** — the SDK does not expose embedded image bytes) |
| Blob | `blob` | `dataSource` (database BLOB field) |
| Subreport | `subreport` | `subreportName`, `isOnDemand` — details live in the root `subreports[]` |
| Chart | `chart` | `chartType` (best effort), `requiresManualReview: true`, `raw` |
| CrossTab | `crossTab` | `rows[]`, `columns[]`, `summaries[]` (best effort), `requiresManualReview: true`, `raw` |

## subreports[]

`name` (subreport name, usually `Something.rpt`), `placement` (`sectionName`, `objectName`, `leftTwips`, `topTwips`,
`widthTwips`, `heightTwips`, `zIndex`), `isOnDemand`, `links[]` (`mainReportField` e.g. `{Customer.Code}`,
`subreportParameter` e.g. `{?Pm-Customer.Code}`, `subreportField` (when linked to a field rather than only a parameter),
`linkedParameterName` bare name), `report` (complete `ReportMetadata` of the subreport: own `reportInfo` (`isSubreport=true`),
`dataSource`, `parameters` (linked ones flagged), `formulas`, `groups`, `sections`, `diagnostics`; `pageSetup` all null;
`subreports` always `[]`).

Origin: placement from the Engine `SubreportObject`; links from RAS `SubreportController.GetSubreportLinks`; the subreport
document from `ReportDocument.OpenSubreport` and RAS `SubreportController.GetSubreport` (`ISCRSubreportClientDocument`).

## derived (client-side, optional)

`columns[]`: for each **visible** (not suppressed) Detail-section `Field` object sorted by `leftTwips`: `header` / `headerObjectName` / `headerSectionName`
(the FieldHeading linked to it, else the header-band Text/FieldHeading object with the largest horizontal overlap ≥ 30 %),
`fieldObjectName`, `fieldDataSource`, `detailSectionName`, `leftTwips`, `widthTwips`, `alignment`, `numberFormat`, `font`.
`bands[]`: `sectionName`, `kind`, `groupIndex`, `heightTwips`, `objectCount`, `isEmpty`, `isSuppressed`.

## Diagnostic codes

`REPORT_INFO_FAILED`, `PAGE_SETUP_FAILED`, `DATABASE_INFO_FAILED`, `DB_DRIVER_MISSING`, `TABLE_FAILED`, `FIELD_FAILED`,
`LINK_FAILED`, `PARAMETER_FAILED`, `FORMULA_FAILED`, `SUMMARY_FAILED`, `RUNNING_TOTAL_FAILED`, `GROUP_FAILED`, `SORT_FAILED`,
`AREA_FAILED`, `SECTION_FAILED`, `OBJECT_FAILED`, `OBJECT_PROPERTY_UNAVAILABLE`, `UNKNOWN_OBJECT_KIND`,
`SUBREPORT_OPEN_FAILED`, `SUBREPORT_NOT_PLACED`, `SUBREPORT_LINKS_FAILED`, `NESTED_SUBREPORT_IGNORED`, `RAS_UNAVAILABLE`,
`RAS_ENRICHMENT_FAILED`, `GENERATED_SQL_FAILED`, `CUSTOM_FUNCTIONS_FAILED`, `MANUAL_REVIEW_REQUIRED` (charts, cross-tabs),
`GROUP_INDEX_MISMATCH`.
