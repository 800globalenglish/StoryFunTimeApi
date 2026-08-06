using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StoryFunTimeApi.Models;

namespace StoryFunTimeApi.Services;

public class PdfService
{
    public byte[] GenerateBookPdf(Book book, List<Page> pages, string uploadsBasePath)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var document = Document.Create(container =>
        {
            foreach (var page in pages.OrderBy(p => p.PageNumber))
            {
                container.Page(pdfPage =>
                {
                    pdfPage.Size(PageSizes.A5);
                    pdfPage.Margin(30);
                    pdfPage.Content().Column(column =>
                    {
                        if (!string.IsNullOrWhiteSpace(page.CartoonImageUrl))
                        {
                            var imagePath = Path.Combine(uploadsBasePath, page.CartoonImageUrl.TrimStart('/').Replace("uploads/", ""));
                            if (File.Exists(imagePath))
                            {
                                column.Item().Image(imagePath).FitArea();
                            }
                        }
                        column.Item().PaddingTop(15).Text(page.ScriptText ?? "").FontSize(16).AlignCenter();
                    });
                });
            }
        });

        return document.GeneratePdf();
    }
}