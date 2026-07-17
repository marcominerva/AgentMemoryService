using System.ComponentModel;

namespace AgentMemoryService.Models;

public record class MemoryUpdate(
    [property: Description("New stable user facts to add. Do not include facts already present in the known facts list.")] IEnumerable<string> FactsToAdd,
    [property: Description("Exact known fact texts to remove. Include a known fact only when it is clearly contradicted, replaced, negated, corrected, or invalidated by the latest user message.")] IEnumerable<string> FactsToRemove);
