using Npgsql;
using Supabase;
using Supabase.Storage;

namespace SDSChat.Services;

public interface ISupabaseService
{
    Task<long> InsertDocumentAsync(string filename);
    Task UploadFileToStorageAsync(Stream fileStream, string fileName, string contentType);
    Task InsertDocumentChunkAsync(long documentId, string content, float[] embedding);
    Task<List<DocumentRecord>> GetDocumentsAsync(int page, int pageSize);
    Task<int> GetDocumentCountAsync();
    Task<DocumentRecord?> GetDocumentByIdAsync(long id);
    Task<bool> FileNameExistsAsync(string fileName);
    Task<bool> DeleteDocumentAsync(long id);
    Task<byte[]?> DownloadFileFromStorageAsync(string fileName);
    Task<List<DocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, int limit = 5, double similarityThreshold = 0.7);
}

public class SupabaseService : ISupabaseService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupabaseService> _logger;
    private readonly string _connectionString;
    private readonly Supabase.Client _supabaseClient;

    public SupabaseService(IConfiguration configuration, ILogger<SupabaseService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection connection string not found");
        
        // Use ServiceRoleKey for direct server-side Storage operations (bypasses RLS)
        var supabaseUrl = configuration["SupabaseUrl"] 
            ?? throw new InvalidOperationException("SupabaseUrl not configured");
        var serviceRoleKey = configuration["SupabaseServiceRoleKey"] 
            ?? throw new InvalidOperationException("SupabaseServiceRoleKey not configured");
        
        // Validate that ServiceRoleKey is not a placeholder
        if (serviceRoleKey.Contains("YOUR_") || serviceRoleKey.Length < 20)
        {
            throw new InvalidOperationException(
                "Supabase:ServiceRoleKey appears to be a placeholder. Please set your actual ServiceRoleKey in appsettings.json. " +
                "You can find it in Supabase Dashboard > Settings > API > service_role (secret key)");
        }
        
        _supabaseClient = new Supabase.Client(supabaseUrl, serviceRoleKey);
        _logger.LogInformation("Supabase client initialized with URL: {Url}", supabaseUrl);
    }

    public async Task<long> InsertDocumentAsync(string filename)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = new NpgsqlCommand(
            "INSERT INTO data.documents (filename, created_at) VALUES (@filename, NOW()) RETURNING id",
            connection);
        command.Parameters.AddWithValue("filename", filename);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task UploadFileToStorageAsync(Stream fileStream, string fileName, string contentType)
    {
        try
        {
            // Convert stream to byte array
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream);
            var fileBytes = memoryStream.ToArray();

            _logger.LogDebug("Uploading file {FileName} ({Size} bytes) to Supabase Storage bucket 'files'", fileName, fileBytes.Length);
            
            var bucket = _supabaseClient.Storage.From("files");
            await bucket.Upload(fileBytes, fileName, new Supabase.Storage.FileOptions
            {
                ContentType = contentType,
                Upsert = false
            });
            
            _logger.LogInformation("Successfully uploaded file {FileName} to Supabase Storage", fileName);
        }
        catch (Supabase.Storage.Exceptions.SupabaseStorageException ex)
        {
            _logger.LogError(ex, "Supabase Storage error uploading file {FileName}. Error: {Message}", fileName, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error uploading file {FileName} to Supabase storage", fileName);
            throw;
        }
    }

    private async Task EnsureVectorExtensionAsync(NpgsqlConnection connection)
    {
        try
        {
            var enableCommand = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", connection);
            await enableCommand.ExecuteNonQueryAsync();
            _logger.LogDebug("pgvector extension ensured");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable pgvector extension. It may already be enabled or you may need to enable it manually in Supabase Dashboard > Database > Extensions");
        }
    }

    public async Task InsertDocumentChunkAsync(long documentId, string content, float[] embedding)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Ensure pgvector extension is enabled
        await EnsureVectorExtensionAsync(connection);

        // Convert float[] to PostgreSQL vector format
        var vectorString = "[" + string.Join(",", embedding) + "]";

        var command = new NpgsqlCommand(
            "INSERT INTO data.document_chunks (document_id, content, embedding) VALUES (@documentId, @content, @embedding::vector)",
            connection);
        command.Parameters.AddWithValue("documentId", documentId);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("embedding", vectorString);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<DocumentRecord>> GetDocumentsAsync(int page, int pageSize)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = new NpgsqlCommand(
            "SELECT id, filename, created_at FROM data.documents ORDER BY created_at DESC OFFSET @offset LIMIT @limit",
            connection);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("limit", pageSize);

        var documents = new List<DocumentRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            documents.Add(new DocumentRecord
            {
                Id = reader.GetInt64(0),
                Filename = reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt = reader.GetDateTime(2)
            });
        }

        return documents;
    }

    public async Task<int> GetDocumentCountAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = new NpgsqlCommand("SELECT COUNT(*) FROM data.documents", connection);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<DocumentRecord?> GetDocumentByIdAsync(long id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = new NpgsqlCommand(
            "SELECT id, filename, created_at FROM data.documents WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DocumentRecord
            {
                Id = reader.GetInt64(0),
                Filename = reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt = reader.GetDateTime(2)
            };
        }

        return null;
    }

    public async Task<bool> FileNameExistsAsync(string fileName)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM data.documents WHERE filename = @filename",
            connection);
        command.Parameters.AddWithValue("filename", fileName);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    public async Task<bool> DeleteDocumentAsync(long id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Get filename first to delete from storage
        var document = await GetDocumentByIdAsync(id);
        if (document == null)
            return false;

        // Delete from database (CASCADE will handle chunks)
        var command = new NpgsqlCommand("DELETE FROM data.documents WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var rowsAffected = await command.ExecuteNonQueryAsync();

        // Delete from storage if exists
        if (document.Filename != null)
        {
            try
            {
                var bucket = _supabaseClient.Storage.From("files");
                await bucket.Remove(document.Filename);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting file from storage: {FileName}", document.Filename);
            }
        }

        return rowsAffected > 0;
    }

    public async Task<byte[]?> DownloadFileFromStorageAsync(string fileName)
    {
        try
        {
            _logger.LogDebug("Downloading file {FileName} from Supabase Storage bucket 'files'", fileName);
            
            var bucket = _supabaseClient.Storage.From("files");
            var fileData = await bucket.Download(fileName, null);
            
            if (fileData == null)
            {
                _logger.LogWarning("File {FileName} not found in Supabase Storage bucket 'files'", fileName);
                return null;
            }
            
            _logger.LogInformation("Successfully downloaded file {FileName} from Supabase Storage ({Size} bytes)", fileName, fileData.Length);
            return fileData;
        }
        catch (Supabase.Storage.Exceptions.SupabaseStorageException ex)
        {
            _logger.LogError(ex, "Supabase Storage error downloading file {FileName}. Error: {Message}", fileName, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading file {FileName} from Supabase Storage", fileName);
            return null;
        }
    }

    public async Task<List<DocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, int limit = 5, double similarityThreshold = 0.5)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Ensure pgvector extension is enabled
        await EnsureVectorExtensionAsync(connection);

        // Convert float[] to PostgreSQL vector format
        var vectorString = "[" + string.Join(",", queryEmbedding) + "]";

        _logger.LogDebug("Searching for chunks with query vector dimension: {Dimension}, threshold: {Threshold}", 
            queryEmbedding.Length, similarityThreshold);

        // Use cosine similarity (1 - cosine distance) for vector search
        // pgvector uses cosine distance, so we subtract from 1 to get similarity
        // If threshold is 0, don't filter by threshold
        string sql;
        if (similarityThreshold > 0)
        {
            sql = @"SELECT id, document_id, content, 1 - (embedding <=> @queryVector::vector) as similarity
                    FROM data.document_chunks
                    WHERE 1 - (embedding <=> @queryVector::vector) >= @threshold
                    ORDER BY embedding <=> @queryVector::vector
                    LIMIT @limit";
        }
        else
        {
            sql = @"SELECT id, document_id, content, 1 - (embedding <=> @queryVector::vector) as similarity
                    FROM data.document_chunks
                    ORDER BY embedding <=> @queryVector::vector
                    LIMIT @limit";
        }

        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("queryVector", vectorString);
        command.Parameters.AddWithValue("limit", limit);
        if (similarityThreshold > 0)
        {
            command.Parameters.AddWithValue("threshold", similarityThreshold);
        }

        var chunks = new List<DocumentChunk>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            chunks.Add(new DocumentChunk
            {
                Id = reader.GetGuid(0),
                DocumentId = reader.GetInt64(1),
                Content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Similarity = (float)reader.GetDouble(3)
            });
        }

        _logger.LogDebug("Found {Count} chunks with similarity >= {Threshold}", chunks.Count, similarityThreshold);
        return chunks;
    }
}

public class DocumentRecord
{
    public long Id { get; set; }
    public string? Filename { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DocumentChunk
{
    public Guid Id { get; set; }
    public long DocumentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public float Similarity { get; set; }
}

