namespace AgentMemoryService.Data.Entities;

public class UserMemory
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = null!;

    public IList<string> Facts { get; set; } = [];
}
