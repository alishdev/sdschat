using SDSChat.Models;

namespace SDSChat.Services;

public interface IChatService
{
    Task<ChatSearchResult> SearchDocumentsAsync(string searchPhrase);
}

public class ChatService : IChatService
{
    private readonly IDocumentService _documentService;
    private readonly ITextExtractionService _textExtractionService;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IDocumentService documentService,
        ITextExtractionService textExtractionService,
        ILogger<ChatService> logger)
    {
        _documentService = documentService;
        _textExtractionService = textExtractionService;
        _logger = logger;
    }

    public async Task<ChatSearchResult> SearchDocumentsAsync(string searchPhrase)
    {
        if (string.IsNullOrWhiteSpace(searchPhrase))
        {
            return new ChatSearchResult
            {
                Found = false,
                MatchingDocuments = new List<string>()
            };
        }

        try
        {
            // Get all documents (no pagination for search)
            var allDocuments = await _documentService.GetAllDocumentsAsync();
            var matchingDocuments = new List<string>();

            foreach (var document in allDocuments)
            {
                if (!File.Exists(document.FilePath))
                {
                    continue;
                }

                try
                {
                    var textContent = await _textExtractionService.ExtractTextAsync(document.FilePath, document.ContentType);
                    
                    // Case-insensitive search for the exact phrase
                    if (textContent.Contains(searchPhrase, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingDocuments.Add(document.FileName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching in document {FileName}", document.FileName);
                }
            }

            return new ChatSearchResult
            {
                Found = matchingDocuments.Count > 0,
                MatchingDocuments = matchingDocuments
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching documents");
            return new ChatSearchResult
            {
                Found = false,
                MatchingDocuments = new List<string>()
            };
        }
    }
}

public class ChatSearchResult
{
    public bool Found { get; set; }
    public List<string> MatchingDocuments { get; set; } = new();
}

