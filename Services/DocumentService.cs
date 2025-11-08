using SDSChat.Models;

namespace SDSChat.Services;

public interface IDocumentService
{
    Task<List<Document>> GetDocumentsAsync(int page, int pageSize);
    Task<List<Document>> GetAllDocumentsAsync();
    Task<int> GetDocumentCountAsync();
    Task<Document?> GetDocumentByIdAsync(string documentId);
    Task<bool> FileNameExistsAsync(string fileName);
    Task<Document> SaveDocumentAsync(Stream fileStream, string fileName, long fileSize, string contentType);
    Task<bool> DeleteDocumentAsync(string documentId);
}

public class DocumentService : IDocumentService
{
    private readonly ISupabaseService _supabaseService;
    private readonly ITextExtractionService _textExtractionService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        ISupabaseService supabaseService,
        ITextExtractionService textExtractionService,
        IEmbeddingService embeddingService,
        ILogger<DocumentService> logger)
    {
        _supabaseService = supabaseService;
        _textExtractionService = textExtractionService;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<List<Document>> GetDocumentsAsync(int page, int pageSize)
    {
        var records = await _supabaseService.GetDocumentsAsync(page, pageSize);
        return records.Select(r => 
        {
            var storedFileName = r.Filename ?? string.Empty;
            // Extract original filename from stored filename (remove GUID prefix)
            var originalFileName = storedFileName.Contains('_') 
                ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
                : storedFileName;

            return new Document
            {
                Id = (int)r.Id,
                FileName = originalFileName,
                DateUploaded = r.CreatedAt,
                StoredFileName = storedFileName,
                FilePath = string.Empty, // Not used with Supabase
                UserId = string.Empty,
                FileSize = 0, // Could be retrieved from storage if needed
                ContentType = string.Empty
            };
        }).ToList();
    }

    public async Task<List<Document>> GetAllDocumentsAsync()
    {
        // Get all documents by using a large page size
        var records = await _supabaseService.GetDocumentsAsync(1, int.MaxValue);
        return records.Select(r => 
        {
            var storedFileName = r.Filename ?? string.Empty;
            // Extract original filename from stored filename (remove GUID prefix)
            var originalFileName = storedFileName.Contains('_') 
                ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
                : storedFileName;

            return new Document
            {
                Id = (int)r.Id,
                FileName = originalFileName,
                DateUploaded = r.CreatedAt,
                StoredFileName = storedFileName,
                FilePath = string.Empty,
                UserId = string.Empty,
                FileSize = 0,
                ContentType = string.Empty
            };
        }).ToList();
    }

    public async Task<int> GetDocumentCountAsync()
    {
        return await _supabaseService.GetDocumentCountAsync();
    }

    public async Task<Document?> GetDocumentByIdAsync(string documentId)
    {
        if (!long.TryParse(documentId, out var id))
            return null;

        var record = await _supabaseService.GetDocumentByIdAsync(id);
        if (record == null)
            return null;

        var storedFileName = record.Filename ?? string.Empty;
        // Extract original filename from stored filename (remove GUID prefix)
        var originalFileName = storedFileName.Contains('_') 
            ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
            : storedFileName;

        return new Document
        {
            Id = (int)record.Id,
            FileName = originalFileName,
            DateUploaded = record.CreatedAt,
            StoredFileName = storedFileName,
            FilePath = string.Empty,
            UserId = string.Empty,
            FileSize = 0,
            ContentType = string.Empty
        };
    }

    public async Task<bool> FileNameExistsAsync(string fileName)
    {
        // Check if any stored filename ends with the original filename
        // This is a simplified check - in production you might want a better approach
        var allRecords = await _supabaseService.GetDocumentsAsync(1, int.MaxValue);
        return allRecords.Any(r => 
        {
            var storedFileName = r.Filename ?? string.Empty;
            var originalFileName = storedFileName.Contains('_') 
                ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
                : storedFileName;
            return originalFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase);
        });
    }

    public async Task<Document> SaveDocumentAsync(Stream fileStream, string fileName, long fileSize, string contentType)
    {
        // Generate unique filename for storage
        var storedFileName = $"{Guid.NewGuid()}_{fileName}";

        try
        {
            // Upload file to Supabase storage
            fileStream.Position = 0; // Reset stream position
            await _supabaseService.UploadFileToStorageAsync(fileStream, storedFileName, contentType);

            // Insert document record into database
            // Store the stored filename (with GUID) so we can retrieve it from storage later
            var documentId = await _supabaseService.InsertDocumentAsync(storedFileName);

            // Extract text from the file
            // We need to download the file temporarily or extract from stream
            // For now, let's extract from a temporary file
            var tempFilePath = Path.Combine(Path.GetTempPath(), storedFileName);
            try
            {
                fileStream.Position = 0;
                using (var fileStream2 = new FileStream(tempFilePath, FileMode.Create))
                {
                    await fileStream.CopyToAsync(fileStream2);
                }

                var extractedText = await _textExtractionService.ExtractTextAsync(tempFilePath, contentType);

                if (!string.IsNullOrWhiteSpace(extractedText))
                {
                    // Create embeddings and chunks
                    var chunks = await _embeddingService.CreateEmbeddingsAsync(extractedText);

                    // Insert chunks into database
                    foreach (var chunk in chunks)
                    {
                        await _supabaseService.InsertDocumentChunkAsync(documentId, chunk.Content, chunk.Embedding);
                    }
                }
            }
            finally
            {
                // Clean up temporary file
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deleting temporary file: {FilePath}", tempFilePath);
                    }
                }
            }

            // Return document object
            var record = await _supabaseService.GetDocumentByIdAsync(documentId);
            if (record == null)
                throw new InvalidOperationException("Failed to retrieve created document");

            // Extract original filename from stored filename (remove GUID prefix)
            var originalFileName = storedFileName.Contains('_') 
                ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
                : storedFileName;

            return new Document
            {
                Id = (int)record.Id,
                FileName = originalFileName, // Original filename for display
                StoredFileName = storedFileName, // Stored filename for retrieval
                FilePath = string.Empty,
                DateUploaded = record.CreatedAt,
                UserId = string.Empty,
                FileSize = fileSize,
                ContentType = contentType
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving document: {FileName}", fileName);
            throw;
        }
    }

    public async Task<bool> DeleteDocumentAsync(string documentId)
    {
        if (!long.TryParse(documentId, out var id))
            return false;

        return await _supabaseService.DeleteDocumentAsync(id);
    }
}
