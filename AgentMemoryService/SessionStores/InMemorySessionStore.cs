using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentMemoryService.SessionStores;

public class InMemorySessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        JsonElement? sessionContent = sessions.TryGetValue(conversationId, out var session) ? session : null;

        return sessionContent switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(sessionContent.Value, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var conversationId = GetKey(agent, key);
        sessions[conversationId] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
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
