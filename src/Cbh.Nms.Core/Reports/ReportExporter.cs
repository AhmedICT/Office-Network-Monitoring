using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Cbh.Nms.Core.Reports;

/// <summary>FR-RP-05: every report exports to Excel and PDF from the same <see cref="ReportTable"/>.</summary>
public static class ReportExporter
{
    static ReportExporter()
    {
        if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public static byte[] ToXlsx(ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Report");
        ws.Cell(1, 1).Value = t.Title;
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).Value = t.Subtitle;
        ws.Cell(2, 1).Style.Font.SetItalic();
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var cell = ws.Cell(4, c + 1);
            cell.Value = t.Columns[c];
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#0F2747")).Font.SetFontColor(XLColor.White);
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var v = t.Rows[r][c];
                var cell = ws.Cell(r + 5, c + 1);
                cell.Value = v switch
                {
                    null => Blank.Value,
                    double d => d,
                    int i => i,
                    long l => l,
                    decimal m => m,
                    _ => v.ToString(),
                };
            }
            if (t.Flagged.Contains(r)) ws.Range(r + 5, 1, r + 5, t.Columns.Count).Style.Font.SetFontColor(XLColor.FromHtml("#C62828"));
        }
        ws.Range(4, 1, Math.Max(4, t.Rows.Count + 4), t.Columns.Count).SetAutoFilter();
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(4);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static byte[] ToPdf(ReportTable t)
    {
        var doc = new Document();
        doc.Info.Title = t.Title;
        doc.Styles[StyleNames.Normal]!.Font.Name = "Arial";
        doc.Styles[StyleNames.Normal]!.Font.Size = 8;
        var section = doc.AddSection();
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.2);

        var title = section.AddParagraph("CBH-NMS · " + t.Title);
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        var sub = section.AddParagraph(t.Subtitle);
        sub.Format.Font.Color = Colors.Gray;
        sub.Format.SpaceAfter = Unit.FromPoint(8);

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = new Color(0xE3, 0xE8, 0xEF);
        var usable = 27.7 - 2.4;
        foreach (var _ in t.Columns) table.AddColumn(Unit.FromCentimeter(usable / t.Columns.Count));
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = new Color(0x0F, 0x27, 0x47);
        head.Format.Font.Color = Colors.White;
        head.Format.Font.Bold = true;
        for (var c = 0; c < t.Columns.Count; c++) head.Cells[c].AddParagraph(t.Columns[c]);
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var row = table.AddRow();
            if (t.Flagged.Contains(r)) row.Format.Font.Color = new Color(0xC6, 0x28, 0x28);
            for (var c = 0; c < t.Columns.Count; c++)
                row.Cells[c].AddParagraph(t.Rows[r][c] switch { null => "", double d => d.ToString("0.##"), var v => v.ToString() ?? "" });
        }
        if (t.Rows.Count == 0) section.AddParagraph("No data for this period.");

        var footer = section.Footers.Primary.AddParagraph();
        footer.AddText("Popular Diagnostic Centre Ltd. · page ");
        footer.AddPageField();
        footer.Format.Alignment = ParagraphAlignment.Right;
        footer.Format.Font.Size = 7;

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }
}
