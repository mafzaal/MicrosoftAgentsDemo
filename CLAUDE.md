# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build

# Run
dotnet run

# Restore packages
dotnet restore
```

## Environment

The app reads `AZURE_OPENAI_API_KEY` from the environment. A `.env` file exists in the repo root but is **not** auto-loaded — export the variable manually or use a tool like `dotenv`. The endpoint is currently hardcoded in `Program.cs`; the `.env` file contains an alternative endpoint and deployment name that can be used if switching configurations.

Authentication falls back to `DefaultAzureCredential` (Azure CLI / Managed Identity) when `AZURE_OPENAI_API_KEY` is not set.

## Architecture

This is a single-file .NET 10 console app (`Program.cs`) that demonstrates the **Microsoft.Agents.AI** agent abstraction over Azure OpenAI.

Key layers:
- `AzureOpenAIClient` (Azure.AI.OpenAI) — authenticates and wraps the Azure OpenAI REST API
- `.GetChatClient(deploymentName).AsIChatClient()` — adapts the raw chat client to the `Microsoft.Extensions.AI` `IChatClient` interface
- `chatClient.AsAIAgent(...)` (Microsoft.Agents.AI.Foundry) — wraps the chat client in an `AIAgent` that supports both one-shot (`RunAsync`) and streaming (`RunStreamingAsync`) invocations

The `AIAgent` is the primary abstraction; instructions passed at construction time act as the system prompt.
