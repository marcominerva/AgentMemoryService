using AgentMemoryService.Data;
using AgentMemoryService.Data.Entities;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

namespace AgentMemoryService.SessionStores;

public class DatabaseSessionStore(ApplicationDbContext dbContext, IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(c => c.UserName == userName && c.ConversationId == conversationId, cancellationToken: cancellationToken);

        return conversation switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(conversation.Session, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(c => c.UserName == userName && c.ConversationId == conversationId, cancellationToken: cancellationToken);

        if (conversation is null)
        {
            conversation = new UserConversation
            {
                UserName = userName!,
                ConversationId = conversationId,
            };

            dbContext.Conversations.Add(conversation);
        }

        conversation.Date = DateTimeOffset.UtcNow;
        conversation.Session = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public string GetKey(AIAgent agent, AgentSessionStoreKey key)
    {
        if (key.Partitions?.TryGetValue("isolation", out var isolationKey) == true)
        {
            return $"{agent.Id}:{isolationKey}:{key.SessionId}";
        }

        return $"{agent.Id}:{key.SessionId}";
    }
}
