using AgentMemoryService.Data;
using AgentMemoryService.Data.Entities;
using AgentMemoryService.Models;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

namespace AgentMemoryService.ContextProviders;

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
