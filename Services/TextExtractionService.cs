using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SDSChat.Services;

public interface ITextExtractionService
{
    Task<string> ExtractTextAsync(string filePath, string contentType);
}

public class TextExtractionService : ITextExtractionService
{
    private readonly ILogger<TextExtractionService> _logger;

    public TextExtractionService(ILogger<TextExtractionService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExtractTextAsync(string filePath, string contentType)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        try
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            return extension switch
            {
                ".txt" => await File.ReadAllTextAsync(filePath),
                ".pdf" => ExtractTextFromPdf(filePath),
                ".docx" => ExtractTextFromDocx(filePath),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from file {FilePath}", filePath);
            return string.Empty;
        }
    }

    private string ExtractTextFromPdf(string filePath)
    {
        try
        {
            using var document = PdfDocument.Open(filePath);
            var text = new System.Text.StringBuilder();
            
            foreach (Page page in document.GetPages())
            {
                text.AppendLine(page.Text);
            }
            
            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from PDF {FilePath}", filePath);
            return string.Empty;
        }
    }

    private string ExtractTextFromDocx(string filePath)
    {
        try
        {
            using var wordDocument = WordprocessingDocument.Open(filePath, false);
            var body = wordDocument.MainDocumentPart?.Document?.Body;
            
            if (body == null)
            {
                return string.Empty;
            }

            var text = new System.Text.StringBuilder();
            foreach (var paragraph in body.Elements<Paragraph>())
            {
                foreach (var run in paragraph.Elements<Run>())
                {
                    foreach (var textElement in run.Elements<Text>())
                    {
                        text.Append(textElement.Text);
                    }
                }
                text.AppendLine();
            }
            
            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from DOCX {FilePath}", filePath);
            return string.Empty;
        }
    }
}

