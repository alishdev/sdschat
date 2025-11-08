using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;

namespace SDSChat.Services;

public interface IEmbeddingService
{
    Task<List<TextChunk>> CreateEmbeddingsAsync(string text);
}

public class EmbeddingService : IEmbeddingService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly Kernel _kernel;

    public EmbeddingService(IConfiguration configuration, ILogger<EmbeddingService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        var apiKey = configuration["OpenAIApiKey"] 
            ?? throw new InvalidOperationException("OpenAIApiKey not configured");

        var kernelBuilder = Kernel.CreateBuilder();
#pragma warning disable SKEXP0011
        kernelBuilder.AddOpenAITextEmbeddingGeneration(
            modelId: "text-embedding-3-small",
            apiKey: apiKey);
#pragma warning restore SKEXP0011

        _kernel = kernelBuilder.Build();
    }

    public async Task<List<TextChunk>> CreateEmbeddingsAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<TextChunk>();
        }

        // Split text into chunks (simple approach - can be improved)
        var chunks = SplitIntoChunks(text, maxChunkSize: 1000, overlap: 200);
        var textChunks = new List<TextChunk>();

#pragma warning disable SKEXP0001
        var embeddingGenerationService = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();

        foreach (var chunk in chunks)
        {
            try
            {
                var embedding = await embeddingGenerationService.GenerateEmbeddingAsync(chunk, _kernel);
                textChunks.Add(new TextChunk
                {
                    Content = chunk,
                    Embedding = embedding.ToArray()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating embedding for chunk");
            }
        }
#pragma warning restore SKEXP0001

        return textChunks;
    }

    private List<string> SplitIntoChunks(string text, int maxChunkSize, int overlap)
    {
        var chunks = new List<string>();
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (words.Length == 0)
            return chunks;

        var currentChunk = new List<string>();
        var currentLength = 0;

        foreach (var word in words)
        {
            var wordLength = word.Length + 1; // +1 for space

            if (currentLength + wordLength > maxChunkSize && currentChunk.Count > 0)
            {
                chunks.Add(string.Join(" ", currentChunk));
                
                // Overlap: keep last N words for context
                var overlapWords = Math.Min(overlap / 10, currentChunk.Count); // Rough estimate
                currentChunk = currentChunk.TakeLast(overlapWords).ToList();
                currentLength = currentChunk.Sum(w => w.Length + 1);
            }

            currentChunk.Add(word);
            currentLength += wordLength;
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(string.Join(" ", currentChunk));
        }

        return chunks;
    }
}

public class TextChunk
{
    public string Content { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
}

