using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentBasicService.Settings;
using AgentMemoryService.Data;
using AgentMemoryService.Data.Entities;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddSqlServer<ApplicationDbContext>(builder.Configuration.GetConnectionString("SqlConnection")!);

var openAISettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;

builder.Services.AddKeyedChatClient("Default", _ =>
{
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.DefaultDeployment);
});

builder.Services.AddKeyedChatClient("Memory", _ =>
{
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.MemoryDeployment);
});

builder.Services.AddSingleton<InMemorySessionStore>();
builder.Services.AddScoped<UserMemoryContextProvider>();

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var chatClient = services.GetRequiredKeyedService<IChatClient>(key);

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
            Instructions = "You are a helpful assistant that provides concise and accurate information.",
            Tools = [AIFunctionFactory.Create(DateTimeTools.GetCurrentDateTime)]
        },
        AIContextProviders = [services.GetRequiredService<UserMemoryContextProvider>()],
        ChatHistoryProvider = chatHistoryProvider
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
}, ServiceLifetime.Scoped)
.WithSessionStore((services, key) =>
{
    var agentSessionStore = services.GetRequiredService<InMemorySessionStore>();
    return agentSessionStore;
}, withIsolation: false);

builder.Services.AddAIAgent("Memory", (services, key) =>
{
    var chatClient = services.GetRequiredKeyedService<IChatClient>(key);

    return chatClient.AsAIAgent(new()
    {
        Name = key,
        ChatOptions = new()
        {
            Instructions = """
                Look at the user message and update the user's memories.

                The known facts about the user are provided in the system instructions.
                Extract only stable user facts from the user message, such as names, places, likes, dislikes, current state, preferences, relationships, roles, or anything the user asks to remember.

                Add a new memory only when the user message contains a stable fact that is not already present.

                Remove an existing memory only when all of these are true:
                - the user message provides a newer fact about the same subject;
                - the newer fact refers to the same underlying attribute, property, preference, state, relationship, or commitment;
                - the existing fact and the newer fact cannot both be true at the same time;
                - the user message clearly replaces, negates, corrects, or invalidates the existing fact.

                Do not remove an existing memory when the user message merely adds related information.
                Do not remove an existing memory when both facts can reasonably be true together.
                Do not remove an existing memory when the relationship between the two facts is ambiguous.
                Prefer keeping memories over removing them when unsure.
                When removing memories, return the exact known fact text from the provided list.

                Examples:
                - Known: "The user lives in London." Message: "I now live in Paris." Add the Paris memory and remove the London memory.
                - Known: "The user is from Turin." Message: "I moved to Milan." Add the Milan memory and do not remove Turin, because origin and current residence can both be true.
                - Known: "The user likes tea." Message: "I do not like tea anymore." Add the new preference if useful and remove the old tea preference.
                - Known: "The user likes tea." Message: "I also like coffee." Add coffee and do not remove tea.
                - Known: "The user works as a developer." Message: "I am now an engineering manager." Add the manager role and remove the developer role only if the user message clearly indicates the role changed.

                If there are no new facts and no clearly invalidated facts, return empty collections.
                """
        }
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
});

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

app.MapPost("/api/chat", async (ChatRequest request, [FromKeyedServices("Default")] AIAgent agent, [FromKeyedServices("Default")] AgentSessionStore store) =>
{
    var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
    var session = await store.GetSessionAsync(agent, conversationId);

    var response = await agent.RunAsync(request.Message, session);

    await store.SaveSessionAsync(agent, conversationId, session);

    return TypedResults.Ok(new ChatResponse(conversationId, response.Text, response.Usage?.TotalTokenCount));
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

public static class DateTimeTools
{
    [Description("""
        Returns the current date and time.
        ALWAYS call this tool FIRST when the question involves ANY time reference, including: current date/time ('today', 'now', 'current year'),
        relative periods ('recent', 'last/past X days/months/years', 'in the last decade'), time ranges that depend on today's date ('from 2020 to now', 'since January'),
        time calculations ('how long since', 'time elapsed'), or temporal filtering ('latest', 'newest', 'most recent'). You do NOT know the current date - you MUST call this tool to determine it.
        """)]
    public static DateTimeOffset GetCurrentDateTime() => DateTimeOffset.UtcNow;
}

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

internal class UserMemoryContextProvider([FromKeyedServices("Memory")] AIAgent memoryExtractorAgent, ApplicationDbContext dbContext, IHttpContextAccessor httpContextAccessor) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        var aiContext = new AIContext();
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var memory = await dbContext.Memories.FirstOrDefaultAsync(x => x.UserName == userName, cancellationToken);

        if (memory?.Facts.Count is not > 0)
        {
            // There is nothing to provide, so we can return an empty context.
            return aiContext;
        }

        var knownFacts = string.Join("\n", memory.Facts.Select(fact => $"- {fact}"));
        var instructions = $"""
            ## User Memories
            {knownFacts}
            """;

        aiContext.Instructions = instructions;
        return aiContext;
    }

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        var userName = httpContextAccessor.HttpContext?.User?.Identity?.Name;
        var memory = await dbContext.Memories.FirstOrDefaultAsync(x => x.UserName == userName, cancellationToken);

        if (memory is null)
        {
            memory = new UserMemory { UserName = userName! };
            dbContext.Memories.Add(memory);
        }

        var knownFacts = string.Join("\n", memory.Facts.Select(fact => $"- {fact}"));
        var options = new ChatClientAgentRunOptions(new()
        {
            Instructions = $"""
                ## Known facts about the user:
                {knownFacts}

                Use this list to avoid adding duplicates and to return exact known fact texts when a memory is invalidated.
                """
        });

        var response = await memoryExtractorAgent.RunAsync<MemoryUpdate>(context.RequestMessages.Last(), options: options, cancellationToken: cancellationToken);
        var memoryUpdate = response.Result;

        if (!memoryUpdate.FactsToAdd.Any() && !memoryUpdate.FactsToRemove.Any())
        {
            // There is nothing to update, so we can return early.
            return;
        }

        foreach (var factToRemove in memoryUpdate.FactsToRemove)
        {
            var factToRemoveTrimmed = factToRemove.TrimEnd('.');
            var existingMemory = memory.Facts.FirstOrDefault(fact => string.Equals(fact, factToRemoveTrimmed, StringComparison.OrdinalIgnoreCase));

            if (existingMemory is not null)
            {
                memory.Facts.Remove(existingMemory);
            }
        }

        foreach (var factToAdd in memoryUpdate.FactsToAdd)
        {
            var factToAddTrimmed = factToAdd.TrimEnd('.');

            if (!memory.Facts.Contains(factToAddTrimmed, StringComparer.OrdinalIgnoreCase))
            {
                memory.Facts.Add(factToAddTrimmed);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public record class MemoryUpdate(
    [property: Description("New stable user facts to add. Do not include facts already present in the known facts list.")] IEnumerable<string> FactsToAdd,
    [property: Description("Exact known fact texts to remove. Include a known fact only when it is clearly contradicted, replaced, negated, corrected, or invalidated by the latest user message.")] IEnumerable<string> FactsToRemove);
