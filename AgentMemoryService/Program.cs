using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using AgentBasicService.Settings;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using SimpleAuthentication;
using TinyHelpers.AspNetCore.Extensions;
using TinyHelpers.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

builder.Services.AddSimpleAuthentication(builder.Configuration);

var openAISettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;
builder.Services.AddChatClient(_ =>
{
    // Endpoint must end with /openai/v1 for Azure OpenAI
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.Deployment);
});

builder.Services.AddSingleton<InMemorySessionStore>();

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    var chatClient = services.GetRequiredService<IChatClient>();

    var chatHistoryProvider = new InMemoryChatHistoryProvider(new()
    {
        ChatReducer = new MessageCountingChatReducer(20), //new SummarizingChatReducer(chatClient, 1, 10)
        StorageInputRequestMessageFilter = messages =>
        {
            return messages.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory
                && m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider);
        }
    });

    return chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = "You are a helpful assistant that provides concise and accurate information."
        },
        AIContextProviders = [new RagProvider(httpContextAccessor)],
        ChatHistoryProvider = chatHistoryProvider
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
})
.WithSessionStore((services, key) =>
{
    var agentSessionStore = services.GetRequiredService<InMemorySessionStore>();
    return agentSessionStore;
}, withIsolation: false);

builder.Services.AddDefaultProblemDetails();
builder.Services.AddDefaultExceptionHandler();

builder.Services.AddOpenApi(options =>
{
    options.RemoveServerList();
    options.AddDefaultProblemDetailsResponse();

    options.AddSimpleAuthentication(builder.Configuration);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.UseStatusCodePages();
app.UseExceptionHandler();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/chat", async (ChatRequest request, [FromKeyedServices("Default")] AIAgent agent, [FromKeyedServices("Default")] AgentSessionStore store,
    ClaimsPrincipal user) =>
{
    var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
    var session = await store.GetSessionAsync(agent, conversationId);

    var response = await agent.RunAsync(request.Message, session);

    await store.SaveSessionAsync(agent, conversationId, session);

    return TypedResults.Ok(new ChatResponse(conversationId, response.Text));
})
.RequireAuthorization();

app.MapPost("/api/chat/streaming", async (ChatRequest request, [FromKeyedServices("Default")] AIAgent agent, [FromKeyedServices("Default")] AgentSessionStore store, CancellationToken cancellationToken) =>
{
    async IAsyncEnumerable<SseItem<ChatResponse>> StreamAsync([EnumeratorCancellation] CancellationToken innerCancellationToken)
    {
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
        var session = await store.GetSessionAsync(agent, conversationId, innerCancellationToken);

        var updates = new List<AgentResponseUpdate>();

        yield return new SseItem<ChatResponse>(new ChatResponse(conversationId, null), "start");

        await foreach (var update in agent.RunStreamingAsync(request.Message, session, cancellationToken: innerCancellationToken))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new SseItem<ChatResponse>(new ChatResponse(null, update.Text), "delta");
            }
        }

        await store.SaveSessionAsync(agent, conversationId, session, innerCancellationToken);
        var response = updates.ToAgentResponse();

        yield return new SseItem<ChatResponse>(new ChatResponse(null, null, response.Usage?.TotalTokenCount), "metadata");
    }

    return TypedResults.ServerSentEvents(StreamAsync(cancellationToken));
})
.RequireAuthorization();

app.Run();

public record class ChatRequest(string? ConversationId, string Message);

public record class ChatResponse(string? ConversationId, string? Response, long? TotalTokenCount = null);

public class InMemorySessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        JsonElement? sessionContent = sessions.TryGetValue(conversationId, out var session) ? session : null;

        return sessionContent switch
        {
            null => await agent.CreateSessionAsync(cancellationToken),
            _ => await agent.DeserializeSessionAsync(sessionContent.Value, cancellationToken: cancellationToken),
        };
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
        => sessions[conversationId] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
}

public class RagProvider(IHttpContextAccessor httpContextAccessor) : MessageAIContextProvider
{
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Get relevant information from a knowledge base or other source. Here we hardcode it for simplicity.
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(
            [new(ChatRole.User, "My name is Marco"), new(ChatRole.User, $"Today is {DateTime.Now}")]
        );
    }
}