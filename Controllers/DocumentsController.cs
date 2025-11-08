using Microsoft.AspNetCore.Mvc;
using SDSChat.Services;

namespace SDSChat.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly ISupabaseService _supabaseService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentService documentService,
        ISupabaseService supabaseService,
        IConfiguration configuration,
        ILogger<DocumentsController> logger)
    {
        _documentService = documentService;
        _supabaseService = supabaseService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<DocumentsResponse>> GetDocuments([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        try
        {
            _logger.LogInformation("Getting documents - Page: {Page}, PageSize: {PageSize}", page, pageSize);
            
            var documents = await _documentService.GetDocumentsAsync(page, pageSize);
            _logger.LogDebug("Retrieved {Count} documents from service", documents.Count);
            
            var totalCount = await _documentService.GetDocumentCountAsync();
            _logger.LogDebug("Total document count: {Count}", totalCount);

            var response = new DocumentsResponse
            {
                Documents = documents.Select(d => new DocumentDto
                {
                    Id = d.Id,
                    FileName = d.FileName,
                    DateUploaded = d.DateUploaded,
                    FileSize = d.FileSize
                }).ToList(),
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            };

            _logger.LogInformation("Successfully retrieved {Count} documents (page {Page} of {TotalPages})", 
                response.Documents.Count, page, (int)Math.Ceiling(totalCount / (double)pageSize));
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            var errorDetails = $"Exception Type: {ex.GetType().FullName}\n" +
                             $"Message: {ex.Message}\n" +
                             $"Stack Trace:\n{ex.StackTrace}";
            
            if (ex.InnerException != null)
            {
                errorDetails += $"\n\nInner Exception:\n" +
                               $"Type: {ex.InnerException.GetType().FullName}\n" +
                               $"Message: {ex.InnerException.Message}\n" +
                               $"Stack Trace:\n{ex.InnerException.StackTrace}";
            }

            _logger.LogError(ex, "Error getting documents. Full details:\n{ErrorDetails}", errorDetails);
            
            return StatusCode(500, new DocumentsResponse
            {
                Documents = new List<DocumentDto>(),
                TotalCount = 0,
                Page = page,
                PageSize = pageSize,
                ErrorMessage = errorDetails
            });
        }
    }

    [HttpPost("upload")]
    public async Task<ActionResult<UploadResponse>> UploadDocument(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new UploadResponse { Success = false, Message = "No file provided." });
        }

        var maxFileSize = _configuration.GetValue<long>("DocumentSettings:MaxFileSizeBytes", 100 * 1024 * 1024); // 100MB default
        if (file.Length > maxFileSize)
        {
            return BadRequest(new UploadResponse { Success = false, Message = $"File size exceeds the limit of {maxFileSize / (1024 * 1024)}MB." });
        }

        // Check if a file with the same name already exists
        var fileNameExists = await _documentService.FileNameExistsAsync(file.FileName);
        if (fileNameExists)
        {
            return BadRequest(new UploadResponse { Success = false, Message = $"A file with the name '{file.FileName}' already exists. Please rename the file or delete the existing one first." });
        }

        try
        {
            using var stream = file.OpenReadStream();
            var document = await _documentService.SaveDocumentAsync(
                stream,
                file.FileName,
                file.Length,
                file.ContentType ?? "application/octet-stream");

            return Ok(new UploadResponse
            {
                Success = true,
                Message = "File uploaded successfully.",
                DocumentId = document.Id
            });
        }
        catch (Exception ex)
        {
            var errorDetails = $"Exception Type: {ex.GetType().FullName}\n" +
                             $"Message: {ex.Message}\n" +
                             $"Stack Trace:\n{ex.StackTrace}";
            
            if (ex.InnerException != null)
            {
                errorDetails += $"\n\nInner Exception:\n" +
                               $"Type: {ex.InnerException.GetType().FullName}\n" +
                               $"Message: {ex.InnerException.Message}\n" +
                               $"Stack Trace:\n{ex.InnerException.StackTrace}";
            }

            _logger.LogError(ex, "Error uploading document. Full details:\n{ErrorDetails}", errorDetails);
            return StatusCode(500, new UploadResponse 
            { 
                Success = false, 
                Message = $"An error occurred while uploading the file.\n\n{errorDetails}" 
            });
        }
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadDocument(string id)
    {
        try
        {
            var document = await _documentService.GetDocumentByIdAsync(id);

            if (document == null)
            {
                _logger.LogWarning("Document not found for download: {Id}", id);
                return NotFound("Document not found.");
            }

            // Download from Supabase Storage "files" bucket
            _logger.LogDebug("Downloading document {Id} with stored filename: {StoredFileName}", id, document.StoredFileName);
            var fileBytes = await _supabaseService.DownloadFileFromStorageAsync(document.StoredFileName);
            
            if (fileBytes == null)
            {
                _logger.LogWarning("File not found in Supabase Storage: {StoredFileName} for document {Id}", document.StoredFileName, id);
                return NotFound("File not found in storage.");
            }

            var contentType = !string.IsNullOrEmpty(document.ContentType) 
                ? document.ContentType 
                : "application/octet-stream";

            _logger.LogInformation("Successfully downloaded document {Id}: {FileName} ({Size} bytes)", id, document.FileName, fileBytes.Length);
            return File(fileBytes, contentType, document.FileName);
        }
        catch (Exception ex)
        {
            var errorDetails = $"Exception Type: {ex.GetType().FullName}\n" +
                             $"Message: {ex.Message}\n" +
                             $"Stack Trace:\n{ex.StackTrace}";
            
            if (ex.InnerException != null)
            {
                errorDetails += $"\n\nInner Exception:\n" +
                               $"Type: {ex.InnerException.GetType().FullName}\n" +
                               $"Message: {ex.InnerException.Message}\n" +
                               $"Stack Trace:\n{ex.InnerException.StackTrace}";
            }

            _logger.LogError(ex, "Error downloading document {Id}. Full details:\n{ErrorDetails}", id, errorDetails);
            return StatusCode(500, $"An error occurred while downloading the file.\n\n{errorDetails}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<DeleteResponse>> DeleteDocument(string id)
    {
        try
        {
            var deleted = await _documentService.DeleteDocumentAsync(id);
            if (!deleted)
            {
                return NotFound(new DeleteResponse { Success = false, Message = "Document not found." });
            }

            return Ok(new DeleteResponse { Success = true, Message = "Document deleted successfully." });
        }
        catch (Exception ex)
        {
            var errorDetails = $"Exception Type: {ex.GetType().FullName}\n" +
                             $"Message: {ex.Message}\n" +
                             $"Stack Trace:\n{ex.StackTrace}";
            
            if (ex.InnerException != null)
            {
                errorDetails += $"\n\nInner Exception:\n" +
                               $"Type: {ex.InnerException.GetType().FullName}\n" +
                               $"Message: {ex.InnerException.Message}\n" +
                               $"Stack Trace:\n{ex.InnerException.StackTrace}";
            }

            _logger.LogError(ex, "Error deleting document. Full details:\n{ErrorDetails}", errorDetails);
            return StatusCode(500, new DeleteResponse 
            { 
                Success = false, 
                Message = $"An error occurred while deleting the document.\n\n{errorDetails}" 
            });
        }
    }
}

// DTOs
public class DocumentsResponse
{
    public List<DocumentDto> Documents { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DocumentDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTime DateUploaded { get; set; }
    public long FileSize { get; set; }
}

public class UploadResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? DocumentId { get; set; }
}

public class DeleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
