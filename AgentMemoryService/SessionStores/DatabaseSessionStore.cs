using AgentMemoryService.Data;
using AgentMemoryService.Data.Entities;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AgentMemoryService.SessionStores;

public class DatabaseSessionStore(ApplicationDbContext dbContext, IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, conversationId);
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(c => c.UserName == userName && c.ConversationId == key, cancellationToken: cancellationToken);

        return conversation switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(conversation.Session, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, conversationId);
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(c => c.UserName == userName && c.ConversationId == key, cancellationToken: cancellationToken);

        if (conversation is null)
        {
            conversation = new UserConversation
            {
                UserName = userName!,
                ConversationId = key,
            };

            dbContext.Conversations.Add(conversation);
        }

        conversation.Date = DateTimeOffset.UtcNow;
        conversation.Session = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public override async ValueTask DeleteSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, conversationId);
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        await dbContext.Conversations.Where(c => c.UserName == userName && c.ConversationId == key).ExecuteDeleteAsync(cancellationToken);
    }

    private static string GetKey(AIAgent agent, string conversationId)
        => $"{agent.Id}:{conversationId}";
}
