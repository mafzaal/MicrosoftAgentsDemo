# Microsoft Agents Demo — Client Tools with Server LLM

A **.NET 10** web application that demonstrates two complementary patterns using
[Microsoft.Agents.AI](https://aka.ms/agents-ai) and Azure OpenAI:

| Mode | What it shows |
|------|---------------|
| **Expense Assistant** | Server-side AI agent with persistent sessions, CRUD tools over SQLite, and a progressive-disclosure budget-advisor skill |
| **Client Tools Demo** | LLM running on the server that calls tools executing inside the user's **browser** (GPS, screen info, native confirm dialog) |

---

## Architecture

### Expense Assistant (standard chat)

```
Browser  ──POST /api/chat/stream──▶  AIAgent (Microsoft.Agents.AI)
                                          │
                                          ├─ UseFunctionInvocation  ← auto-executes tools
                                          ├─ UseAIContextProviders  ← injects skills on demand
                                          │
                                          ├─ ExpenseTools    (SQLite CRUD)
                                          └─ ExpenseBudgetSkill  (budget analysis skill)
```

### Client Tools Demo (manual agentic loop)

The `/api/clienttools/stream` endpoint drives the tool loop **manually** so that
browser-only tools are never auto-executed on the server:

```
Browser  ──POST /api/clienttools/stream──▶  rawChatClient (no middleware)
                                                  │
                                    ┌─────────────┴─────────────┐
                                    ▼                           ▼
                             Server tools               Client tools
                         (run on server now)        (emitted as SSE tool_call)
                                    │                           │
                             result appended             Browser executes tool
                             to history                  (GPS / screen / confirm)
                                    │                           │
                             loop continues          POST toolResults back ──▶ loop resumes
```

**Why a raw `IChatClient`?**
The DI-registered `chatClient` uses `UseFunctionInvocation()`, which auto-executes *all*
tools on the server. Client tools must run in the browser, so the endpoint extracts the
underlying `OpenAI.Chat.ChatClient` from the middleware chain — reusing its auth while
bypassing auto-invocation.

---

## SSE protocol — client-tools endpoint

| Event `type`  | Direction      | Payload fields                          |
|---------------|----------------|-----------------------------------------|
| `thread`      | server→client  | `threadId`                              |
| `status`      | server→client  | `text` (e.g. "Running GetExpenses…")    |
| `chunk`       | server→client  | `text` (token to append)                |
| `tool_call`   | server→client  | `callId`, `name`, `arguments`           |
| `error`       | server→client  | `text`                                  |
| `[DONE]`      | server→client  | *(literal string, no JSON)*             |

To resume after a `tool_call`, POST `{ threadId, toolResults: [{callId, result}] }` with
no `message` field.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [Azure OpenAI resource](https://portal.azure.com) with a deployed model
  (e.g. `gpt-4o-mini`)
- Either an API key **or** an account logged in with `az login` for passwordless auth

---

## Setup

### 1 — Clone and restore

```sh
git clone https://github.com/YOUR_USERNAME/MicrosoftAgentsDemo
cd MicrosoftAgentsDemo
dotnet restore
```

### 2 — Configure credentials

Copy `.env.example` to `.env` and fill in your values:

```sh
cp .env.example .env
# edit .env, then export the variables:
export $(grep -v '^#' .env | xargs)        # bash / zsh
```

Or set values directly in `appsettings.json` (endpoint and deployment only — never
commit an API key).

**Passwordless auth (recommended):** omit `AZURE_OPENAI_API_KEY` and ensure your
identity has the **Cognitive Services OpenAI User** RBAC role on the Azure OpenAI
resource.

### 3 — Run

```sh
dotnet run
```

Open **http://localhost:5000** in your browser.

---

## Project structure

| File | Purpose |
|------|---------|
| `Program.cs` | App entry point: service setup, endpoints, agentic loops |
| `ExpenseDb.cs` | SQLite schema and connection factory |
| `ExpenseTools.cs` | Agent tools: add, list, update, delete, summarise expenses |
| `ExpenseBudgetSkill.cs` | Progressive-disclosure skill: budget analysis |
| `appsettings.json` | Configuration template (endpoint, deployment name) |
| `wwwroot/index.html` | Single-page chat UI |
| `wwwroot/app.js` | SSE consumer, mode toggle, browser tool implementations |
| `wwwroot/styles.css` | UI styles |

---

## Key concepts

### Agent skills (progressive disclosure)

`ExpenseBudgetSkill` extends `AgentClassSkill<T>` and is registered via
`AgentSkillsProvider`. The skill's instructions, resources, and scripts are injected
into the conversation context **only when relevant** — keeping the system prompt lean
until the user asks a budget question.

### Client-side tools

Tools like `get_user_location`, `get_screen_info`, and `confirm_with_user` are
**declared** on the server (so the LLM knows their schema) but **implemented** in
`app.js`. When the model calls one, the server pauses the loop and emits a `tool_call`
SSE event. The browser executes the tool and POSTs the result back, resuming the
conversation transparently.

### OpenAI-compatible endpoints

In addition to the custom `/api/chat` endpoints, the app also exposes
OpenAI-compatible APIs that any standard client can call:

| Path | Protocol |
|------|----------|
| `POST /expense-assistant/v1/chat/completions` | Chat Completions API |
| `POST /expense-assistant/v1/responses` | Responses API (stateful) |
| `POST /v1/conversations` | Conversations API |

---

## License

MIT
