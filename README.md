# Agent Memory Service

Sample Web API that demonstrates how to build an agent with user memory using Microsoft Agent Framework, Azure OpenAI, API key authentication, and SQL Server.

The service exposes chat endpoints that use a default conversational agent. After every interaction, a second agent analyzes the latest user message and updates the user's persistent memories in SQL Server.

## How it works

The project defines two AI agents:

- **Default agent**: answers user messages and receives the user's known memories as additional context.
- **Memory agent**: extracts stable facts from the latest user message and decides which memories should be added or removed.

The request flow is:

1. The client sends a message to one of the chat endpoints with an API key in the `x-api-key` header.
2. Authentication maps the API key to a user name.
3. The `UserMemoryContextProvider` loads the user's memories from SQL Server.
4. The default agent receives those memories as context and generates the response.
5. The memory agent reviews the latest user message and updates the `Memories` table when it finds new or invalidated stable facts.
6. The conversation session is stored in memory by conversation ID.

Conversation history is kept in memory, while user memories are persisted in SQL Server.

## Endpoints

All endpoints require API key authentication through the `x-api-key` header.

### `POST /api/chat`

Returns a complete chat response.

```json
{
  "conversationId": null,
  "message": "Remember that I like espresso."
}
```

If `conversationId` is omitted or `null`, the service creates a new conversation ID and returns it in the response. Send the same ID in following requests to continue the same in-memory session.

### `POST /api/chat/streaming`

Returns the response as Server-Sent Events:

- `start`: contains the conversation ID.
- `delta`: contains streamed response text chunks.
- `metadata`: contains token usage metadata when available.

## Configuration

Configure the application in `AgentMemoryService/appsettings.json`.

### Connection string

Set the `SqlConnection` connection string used by Entity Framework Core:

```json
{
  "ConnectionStrings": {
    "SqlConnection": "Server=localhost;Database=AgentMemoryService;Trusted_Connection=True;TrustServerCertificate=True"
  }
}
```

### Azure OpenAI

Configure Azure OpenAI with two deployments: one for normal chat and one for memory extraction.

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource-name.openai.azure.com/openai/v1/",
    "DefaultDeployment": "your-chat-deployment-name",
    "MemoryDeployment": "your-memory-deployment-name",
    "ApiKey": "your-api-key"
  }
}
```

- **Endpoint**: the Azure OpenAI resource endpoint URL.
- **DefaultDeployment**: the deployment used by the main conversational agent.
- **MemoryDeployment**: the deployment used to extract and update memories.
- **ApiKey**: the Azure OpenAI API key.

### API key authentication

Configure API keys under `Authentication:ApiKey`. Each key is associated with a user name, and that user name is used to load and store memories.

```json
{
  "Authentication": {
    "ApiKey": {
      "SchemeName": "ApiKey",
      "HeaderName": "x-api-key",
      "ApiKeys": [
        {
          "Value": "your-client-api-key",
          "UserName": "alice"
        }
      ]
    }
  }
}
```

Use different API keys and user names to keep memories separated by user.

## Database setup

The service uses SQL Server and stores user memories in the `Memories` table. Run the following script before starting the application:

```sql
CREATE TABLE [dbo].[Memories]
(
    [Id] uniqueidentifier NOT NULL
        CONSTRAINT [DF_Memories_Id] DEFAULT (newsequentialid()),
    [UserName] nvarchar(64) NOT NULL,
    [Facts] json NOT NULL,
    CONSTRAINT [PK_Memories] PRIMARY KEY ([Id])
);
GO

CREATE UNIQUE INDEX [IX_Memories_UserName]
    ON [dbo].[Memories] ([UserName]);
GO
```

The `Facts` column contains the user's memories as a JSON array managed by Entity Framework Core.

## Run the service

From the repository root, run:

```bash
dotnet run --project AgentMemoryService/AgentMemoryService.csproj
```

The development profile exposes the application at:

- `https://localhost:7017`

Swagger UI is available at `/swagger` when using the HTTPS launch profile.
