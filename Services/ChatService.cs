using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SDSChat.Models;

namespace SDSChat.Services;

public interface IChatService
{
    Task<ChatResponse> GetChatResponseAsync(string userMessage);
}

public class ChatService : IChatService
{
    private readonly ISupabaseService _supabaseService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatService> _logger;
    private readonly Kernel _kernel;

    public ChatService(
        ISupabaseService supabaseService,
        IEmbeddingService embeddingService,
        IConfiguration configuration,
        ILogger<ChatService> logger)
    {
        _supabaseService = supabaseService;
        _embeddingService = embeddingService;
        _configuration = configuration;
        _logger = logger;

        var apiKey = configuration["OpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddOpenAIChatCompletion(
            modelId: "gpt-4o-mini",
            apiKey: apiKey);

        _kernel = kernelBuilder.Build();
    }

    public async Task<ChatResponse> GetChatResponseAsync(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return new ChatResponse
            {
                Success = false,
                Message = "Please provide a question or message."
            };
        }

        try
        {
            // Step 1: Create embedding for the user's question
            _logger.LogDebug("Creating embedding for user message");
            var queryChunks = await _embeddingService.CreateEmbeddingsAsync(userMessage);
            
            if (queryChunks.Count == 0 || queryChunks[0].Embedding.Length == 0)
            {
                _logger.LogWarning("Failed to create embedding for user message");
                return new ChatResponse
                {
                    Success = true,
                    Message = "No information found."
                };
            }

            var queryEmbedding = queryChunks[0].Embedding;

            // Step 2: Search for similar chunks in the database
            _logger.LogDebug("Searching for similar document chunks. Query embedding dimension: {Dimension}", queryEmbedding.Length);
            
            // Lower threshold to 0.5 to be more permissive, or try without threshold first
            var similarChunks = await _supabaseService.SearchSimilarChunksAsync(queryEmbedding, limit: 5, similarityThreshold: 0.5);

            _logger.LogInformation("Found {Count} similar chunks", similarChunks.Count);
            if (similarChunks.Count > 0)
            {
                _logger.LogInformation("Similarity scores: {Scores}", 
                    string.Join(", ", similarChunks.Select(c => c.Similarity.ToString("F3"))));
            }

            if (similarChunks.Count == 0)
            {
                _logger.LogWarning("No similar chunks found for user query. Checking if any chunks exist in database...");
                
                // Try with lower threshold or no threshold to see if chunks exist
                var allChunks = await _supabaseService.SearchSimilarChunksAsync(queryEmbedding, limit: 10, similarityThreshold: 0.0);
                _logger.LogInformation("With threshold 0.0, found {Count} chunks", allChunks.Count);
                
                if (allChunks.Count == 0)
                {
                    _logger.LogWarning("No chunks found in database at all. Documents may not have been processed with embeddings.");
                    return new ChatResponse
                    {
                        Success = true,
                        Message = "No information found. Please ensure documents have been uploaded and processed."
                    };
                }
                else
                {
                    _logger.LogWarning("Chunks exist but similarity is too low. Highest similarity: {MaxSimilarity}", 
                        allChunks.Max(c => c.Similarity));
                    // Use the chunks anyway with lower threshold
                    similarChunks = allChunks.Take(5).ToList();
                }
            }

            // Step 3: Get unique document IDs and fetch document names
            var uniqueDocumentIds = similarChunks.Select(c => c.DocumentId).Distinct().ToList();
            var documentNames = new List<string>();
            
            foreach (var docId in uniqueDocumentIds)
            {
                var document = await _supabaseService.GetDocumentByIdAsync(docId);
                if (document != null && !string.IsNullOrEmpty(document.Filename))
                {
                    // Extract original filename from stored filename (remove GUID prefix)
                    var storedFileName = document.Filename;
                    var originalFileName = storedFileName.Contains('_') 
                        ? storedFileName.Substring(storedFileName.IndexOf('_') + 1) 
                        : storedFileName;
                    documentNames.Add(originalFileName);
                }
            }

            // Step 4: Build context from retrieved chunks
            var context = string.Join("\n\n", similarChunks.Select((chunk, index) => 
                $"[Document {index + 1}]\n{chunk.Content}"));

            _logger.LogDebug("Found {Count} relevant chunks from {DocCount} documents, generating response with OpenAI", 
                similarChunks.Count, documentNames.Count);

            // Step 5: Use OpenAI to generate response based on the context
            var chatCompletionService = _kernel.GetRequiredService<IChatCompletionService>();
            
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(@"You are a helpful assistant that answers questions based ONLY on the provided document context. 
If the context does not contain information to answer the question, respond with 'No information found.'
Do not use any knowledge outside of the provided context. Be concise and accurate.");

            chatHistory.AddUserMessage($"Context from documents:\n\n{context}\n\n\nUser question: {userMessage}");

            var response = await chatCompletionService.GetChatMessageContentAsync(chatHistory);
            var answer = response.Content ?? "No information found.";

            return new ChatResponse
            {
                Success = true,
                Message = answer,
                DocumentNames = documentNames.Distinct().ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat message");
            return new ChatResponse
            {
                Success = false,
                Message = "An error occurred while processing your question. Please try again."
            };
        }
    }
}

public class ChatResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DocumentNames { get; set; } = new();
}

