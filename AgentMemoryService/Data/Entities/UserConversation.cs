using System.Text.Json;

namespace AgentMemoryService.Data.Entities;

public class UserConversation
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = null!;

    public string ConversationId { get; set; } = null!;

    public DateTimeOffset Date { get; set; }

    public JsonElement Session { get; set; }
}
