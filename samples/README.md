# Sample reports

Three real `.rpt` files used by `scripts/smoke.ps1` and `CrystalMetadata.Extractor.Tests`
(set `CR_RUNTIME_AVAILABLE=1`; point `CR_SAMPLE_DIR` elsewhere to use a different folder).

| File | What it exercises |
|------|-------------------|
| `SanitizationInvoiceDetails.rpt` | Single dataset table, one group, a formula, a column-style Details band (small file). |
| `Consumer_app_product_details_report.rpt` | A subreport **linked by parameter** (`Pm-…`), a group, page header. Validates `subreports[].links` and `isLinkedFromParentReport`. |
| `LeadQuotationReportTemplateNew.rpt` | Four subreports, five group levels, nine formulas, many report-footer sections, images. Validates multi-subreport extraction and group index mapping. |
| `SanitizationInvoiceDetails_Conditional.rpt` | Copy of the first file with a **Suppress** condition formula and a **font colour** formula added to a Details field through the RAS API. Validates `conditionFormulas[]` extraction. |

The two larger files contain saved data (`reportInfo.hasSavedData = true`); the extractor never reads it.

To add your own samples: copy `.rpt` files here (or into a folder passed to `--in`). No database connection is required.
