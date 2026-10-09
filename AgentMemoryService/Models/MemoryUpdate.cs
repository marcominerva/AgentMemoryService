using System.ComponentModel;

namespace AgentMemoryService.Models;

public record class MemoryUpdate(
    [property: Description("New stable user facts to add. Do not include facts already present in the known facts list.")] IEnumerable<string> FactsToAdd,
    [property: Description("Exact known fact texts to remove. Keep this empty unless the latest user message explicitly discusses the same attribute and clearly contradicts, replaces, negates, corrects, or invalidates the known fact.")] IEnumerable<string> FactsToRemove);
