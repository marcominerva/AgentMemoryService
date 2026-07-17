using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

public class InMemorySessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, conversationId);
        JsonElement? sessionContent = sessions.TryGetValue(key, out var session) ? session : null;

        return sessionContent switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(sessionContent.Value, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, conversationId);
        sessions[key] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
    }

    private static string GetKey(AIAgent agent, string conversationId)
        => $"{agent.Id}:{conversationId}";
}
