namespace AgentMemoryService.Settings;

public class AzureOpenAISettings
{
    public required string Endpoint { get; init; }

    public required string DefaultDeployment { get; init; }

    public required string MemoryDeployment { get; init; }

    public required string ApiKey { get; init; }
}
