using ClosedXML.Excel;
using CakeShopWorkforce.Web.Shared;
using Microsoft.AspNetCore.Authorization;

namespace CakeShopWorkforce.Web.Features.Reports;

/// <summary>
/// Excel downloads: GET /reports/{type}.xlsx?from=2026-11-01&amp;to=2026-11-30
/// A normal link on the Reports page points here; the browser downloads the file.
/// </summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapGet("/reports/{type}.xlsx", async (string type, string? from, string? to, BuildReport.Handler builder, Clock clock) =>
        {
            var fromDate = DateTime.TryParse(from, out var f) ? f : clock.Today;
            var toDate = DateTime.TryParse(to, out var t) ? t : fromDate;

            var table = await builder.HandleAsync(type, fromDate, toDate);
            if (table is null) return Results.NotFound();

            var bytes = ToExcel(table);
            var fileName = $"{table.Title.Replace(' ', '-')}_{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.xlsx";
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = Roles.Managers });
    }

    /// <summary>Writes a ReportTable to an .xlsx file: title, subtitle, header row, data, totals and notes.</summary>
    public static byte[] ToExcel(ReportTable table)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(table.Title));

        sheet.Cell(1, 1).Value = table.Title;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).Value = table.Subtitle;
        sheet.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        const int headerRow = 4;
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.Value = table.Columns[c].Name;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EBFA");
        }

        var row = headerRow + 1;
        foreach (var values in table.Rows)
            WriteRow(sheet, row++, values, table.Columns, bold: false);

        if (table.Totals is not null)
            WriteRow(sheet, row++, table.Totals, table.Columns, bold: true);

        row++;
        foreach (var note in table.Notes)
        {
            sheet.Cell(row, 1).Value = note;
            sheet.Cell(row++, 1).Style.Font.Italic = true;
        }

        sheet.Range(headerRow, 1, Math.Max(headerRow, row - 1), table.Columns.Count).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, table.Columns.Count).AdjustToContents(headerRow, row, 8, 60);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteRow(IXLWorksheet sheet, int row, object?[] values, List<ReportColumn> columns, bool bold)
    {
        for (var c = 0; c < values.Length && c < columns.Count; c++)
        {
            var cell = sheet.Cell(row, c + 1);
            switch (values[c])
            {
                case null: break;
                case int i: cell.Value = i; break;
                case decimal d: cell.Value = d; break;
                case double d: cell.Value = d; break;
                case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "ddd yyyy-mm-dd"; break;
                default: cell.Value = values[c]!.ToString(); break;
            }

            cell.Style.NumberFormat.Format = columns[c].Kind switch
            {
                ColumnKind.Money => "₱#,##0.00",
                ColumnKind.Number => "#,##0",
                ColumnKind.Decimal => "#,##0.0",
                ColumnKind.Hours => "0.##",
                ColumnKind.Percent => "0\"%\"",
                _ => cell.Style.NumberFormat.Format
            };
            if (bold) cell.Style.Font.Bold = true;
        }
    }

    private static string SafeSheetName(string title)
    {
        var name = new string(title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return name.Length > 31 ? name[..31] : name;
    }
}
