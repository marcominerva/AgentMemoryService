using System.ClientModel;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AgentMemoryService.ContextProviders;
using AgentMemoryService.Data;
using AgentMemoryService.Models;
using AgentMemoryService.SessionStores;
using AgentMemoryService.Settings;
using AgentMemoryService.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using SimpleAuthentication;
using TinyHelpers.AspNetCore.Extensions;
using TinyHelpers.AspNetCore.OpenApi;
using ChatResponse = AgentMemoryService.Models.ChatResponse;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

builder.Services.AddSimpleAuthentication(builder.Configuration);

builder.Services.AddSqlServer<ApplicationDbContext>(builder.Configuration.GetConnectionString("SqlConnection")!);

var openAISettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;

builder.Services.AddKeyedChatClient("Default", _ =>
{
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new()
    {
        Endpoint = new(openAISettings.Endpoint),
        //Transport = new HttpClientPipelineTransport(new(new TraceHttpClientHandler()))
    });

    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.DefaultDeployment);
});

builder.Services.AddKeyedChatClient("Memory", _ =>
{
    var openAIClient = new OpenAIClient(new ApiKeyCredential(openAISettings.ApiKey), new() { Endpoint = new(openAISettings.Endpoint) });
    return openAIClient.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(openAISettings.MemoryDeployment);
});

builder.Services.UseClaimsBasedAgentIsolation(new()
{
    ClaimType = ClaimTypes.Name
});

builder.Services.AddScoped<UserMemoryContextProvider>();

//builder.Services.AddSingleton<InMemorySessionStore>();

//builder.Services.AddHybridCache(options =>
//{
//    options.DefaultEntryOptions = new()
//    {
//        LocalCacheExpiration = TimeSpan.FromHours(4)
//    };
//});
//builder.Services.AddSingleton<HybridCacheSessionStore>();

builder.Services.AddScoped<DatabaseSessionStore>();

builder.Services.AddAIAgent("Default", (services, key) =>
{
    var chatClient = services.GetRequiredKeyedService<IChatClient>(key);

    var chatHistoryProvider = new InMemoryChatHistoryProvider(new()
    {
        ChatReducer = new MessageCountingChatReducer(20), //new SummarizingChatReducer(chatClient, 1, 10)
        ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
        StorageInputRequestMessageFilter = messages =>
        {
            return messages.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory
                && m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider);
        }
    });

    return chatClient.AsAIAgent(new()
    {
        Id = key.ToLowerInvariant(),
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
    var agentSessionStore = services.GetRequiredService<DatabaseSessionStore>();
    return agentSessionStore;
}, ServiceLifetime.Scoped);

builder.Services.AddAIAgent("Memory", (services, key) =>
{
    var chatClient = services.GetRequiredKeyedService<IChatClient>(key);

    return chatClient.AsAIAgent(new()
    {
        Id = key.ToLowerInvariant(),
        Name = key,
        ChatOptions = new()
        {
            Instructions = """
                You are a memory management agent responsible for maintaining accurate, concise, and useful facts about the user.

                Look at the user message and update the user's memories.

                The known facts about the user are provided in the system instructions.
                Use that list to avoid adding duplicates and to identify memories invalidated by newer information.
                Extract only stable user facts from the user message, such as names, places, likes, dislikes, current state, preferences, relationships, roles, or anything the user asks to remember.

                Write each memory as a self-contained, precise, and concise statement.
                Include only the information needed to preserve the fact accurately.
                Do not add explanations, assumptions, conversational context, or redundant details.

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
await ConfigureDatabaseAsync(app.Services);

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
    var session = await store.GetOrCreateSessionAsync(agent, new(conversationId));

    var response = await agent.RunAsync(request.Message, session);

    await store.SaveSessionAsync(agent, new(conversationId), session);

    return TypedResults.Ok(new ChatResponse(conversationId, response.Text, response.Usage?.TotalTokenCount));
})
.RequireAuthorization();

app.MapPost("/api/chat/streaming", async (ChatRequest request, [FromKeyedServices("Default")] AIAgent agent, [FromKeyedServices("Default")] AgentSessionStore store, CancellationToken cancellationToken) =>
{
    async IAsyncEnumerable<SseItem<ChatResponse>> StreamAsync([EnumeratorCancellation] CancellationToken innerCancellationToken)
    {
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
        var session = await store.GetOrCreateSessionAsync(agent, new(conversationId), innerCancellationToken);

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

        await store.SaveSessionAsync(agent, new(conversationId), session, innerCancellationToken);
        var response = updates.ToAgentResponse();

        yield return new SseItem<ChatResponse>(new ChatResponse(null, null, response.Usage?.TotalTokenCount), "metadata");
    }

    return TypedResults.ServerSentEvents(StreamAsync(cancellationToken));
})
.RequireAuthorization();

app.Run();

static async Task ConfigureDatabaseAsync(IServiceProvider serviceProvider)
{
    await using var scope = serviceProvider.CreateAsyncScope();

    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    await EnsureDatabaseAsync();
    await RunMigrationsAsync();

    async Task EnsureDatabaseAsync()
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Create the database if it does not exist.
            // Do this first so there is then a database to start a transaction against.
            if (!await dbCreator.ExistsAsync())
            {
                await dbCreator.CreateAsync();
            }
        });
    }

    async Task RunMigrationsAsync()
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Run migration in a transaction to avoid partial migration if it fails.
            await dbContext.Database.MigrateAsync();
        });
    }
}
