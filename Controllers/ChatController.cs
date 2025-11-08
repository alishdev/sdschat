using Microsoft.AspNetCore.Mvc;
using SDSChat.Services;

namespace SDSChat.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(IChatService chatService, ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    [HttpPost("search")]
    public async Task<ActionResult<ChatResponse>> Search([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new ChatResponse { Success = false, Message = "Search phrase cannot be empty." });
        }

        try
        {
            var result = await _chatService.SearchDocumentsAsync(request.Message);
            
            if (result.Found)
            {
                var documentList = string.Join("\n", result.MatchingDocuments.Select(doc => $"• {doc}"));
                return Ok(new ChatResponse
                {
                    Success = true,
                    Message = $"Found \"{request.Message}\"",
                    DocumentNames = result.MatchingDocuments
                });
            }
            else
            {
                return Ok(new ChatResponse
                {
                    Success = true,
                    Message = "Sorry, I couldn't find that phrase in any of your documents.",
                    DocumentNames = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching documents");
            return StatusCode(500, new ChatResponse { Success = false, Message = "An error occurred while searching documents." });
        }
    }
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
}

public class ChatResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DocumentNames { get; set; } = new();
}

