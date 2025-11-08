using System.Text.Json;
using SDSChat.Models;

namespace SDSChat.Services;

public interface IDocumentService
{
    Task<List<Document>> GetDocumentsAsync(int page, int pageSize);
    Task<List<Document>> GetAllDocumentsAsync();
    Task<int> GetDocumentCountAsync();
    Task<Document?> GetDocumentByIdAsync(string documentId);
    Task<bool> FileNameExistsAsync(string fileName);
    Task<Document> SaveDocumentAsync(string fileName, string storedFileName, string filePath, long fileSize, string contentType);
    Task<bool> DeleteDocumentAsync(string documentId);
}

public class DocumentService : IDocumentService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(IConfiguration configuration, ILogger<DocumentService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetStoragePath()
    {
        return _configuration.GetValue<string>("DocumentSettings:StoragePath")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "Documents");
    }

    private string GetMetadataFilePath()
    {
        var storagePath = GetStoragePath();
        return Path.Combine(storagePath, "metadata.json");
    }

    private async Task<List<Document>> LoadMetadataAsync()
    {
        var metadataPath = GetMetadataFilePath();
        if (!File.Exists(metadataPath))
        {
            return new List<Document>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath);
            var documents = JsonSerializer.Deserialize<List<Document>>(json) ?? new List<Document>();
            // Verify files still exist and update paths
            var storagePathValue = GetStoragePath();
            foreach (var doc in documents)
            {
                var fullPath = Path.Combine(storagePathValue, doc.StoredFileName);
                if (File.Exists(fullPath))
                {
                    doc.FilePath = fullPath;
                }
            }
            return documents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading metadata");
            return new List<Document>();
        }
    }

    private async Task SaveMetadataAsync(List<Document> documents)
    {
        var metadataPath = GetMetadataFilePath();
        var storagePath = GetStoragePath();
        
        // Ensure storage directory exists
        Directory.CreateDirectory(storagePath);

        var json = JsonSerializer.Serialize(documents, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, json);
    }

    public async Task<List<Document>> GetDocumentsAsync(int page, int pageSize)
    {
        var documents = await LoadMetadataAsync();
        return documents
            .OrderByDescending(d => d.DateUploaded)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    public async Task<List<Document>> GetAllDocumentsAsync()
    {
        return await LoadMetadataAsync();
    }

    public async Task<int> GetDocumentCountAsync()
    {
        var documents = await LoadMetadataAsync();
        return documents.Count;
    }

    public async Task<Document?> GetDocumentByIdAsync(string documentId)
    {
        var documents = await LoadMetadataAsync();
        return documents.FirstOrDefault(d => d.Id.ToString() == documentId);
    }

    public async Task<bool> FileNameExistsAsync(string fileName)
    {
        var documents = await LoadMetadataAsync();
        return documents.Any(d => d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Document> SaveDocumentAsync(string fileName, string storedFileName, string filePath, long fileSize, string contentType)
    {
        var documents = await LoadMetadataAsync();
        
        var document = new Document
        {
            Id = documents.Count > 0 ? documents.Max(d => d.Id) + 1 : 1,
            FileName = fileName,
            StoredFileName = storedFileName,
            FilePath = filePath,
            DateUploaded = DateTime.UtcNow,
            UserId = string.Empty, // Not used anymore but keeping for model compatibility
            FileSize = fileSize,
            ContentType = contentType
        };

        documents.Add(document);
        await SaveMetadataAsync(documents);
        return document;
    }

    public async Task<bool> DeleteDocumentAsync(string documentId)
    {
        var documents = await LoadMetadataAsync();
        var document = documents.FirstOrDefault(d => d.Id.ToString() == documentId);

        if (document == null)
            return false;

        // Delete the physical file
        if (File.Exists(document.FilePath))
        {
            try
            {
                File.Delete(document.FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file {FilePath}", document.FilePath);
            }
        }

        // Remove from metadata
        documents.Remove(document);
        await SaveMetadataAsync(documents);
        return true;
    }
}
