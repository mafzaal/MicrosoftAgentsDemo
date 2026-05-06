using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Azure OpenAI configuration ────────────────────────────────────────────────
// Supported configuration sources (checked in order by IConfiguration):
//   1. Environment variables
//   2. .NET user secrets  (dotnet user-secrets set "AZURE_OPENAI_ENDPOINT" "...")
//   3. appsettings.json   (AzureOpenAI:Endpoint)
//
// Both key naming styles are supported — use whichever you prefer:
//   Flat style (env vars / user secrets): AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT_NAME, AZURE_OPENAI_API_KEY
//   Nested style (appsettings.json):      AzureOpenAI:Endpoint,  AzureOpenAI:DeploymentName,  AzureOpenAI:ApiKey
//
// API-key auth is used when a key is present; otherwise the app falls back to
// DefaultAzureCredential (Azure CLI / Managed Identity). When using
// DefaultAzureCredential the identity must have the
// "Cognitive Services OpenAI User" RBAC role on the Azure OpenAI resource.
var config = builder.Configuration;

var endpoint = config["AzureOpenAI:Endpoint"]
               ?? config["AZURE_OPENAI_ENDPOINT"]
               ?? throw new InvalidOperationException(
                   "Azure OpenAI endpoint is not configured. " +
                   "Set AzureOpenAI:Endpoint in appsettings.json, user-secrets, or the AZURE_OPENAI_ENDPOINT environment variable.");

var deploymentName = config["AzureOpenAI:DeploymentName"]
                     ?? config["AZURE_OPENAI_DEPLOYMENT_NAME"]
                     ?? "gpt-4o-mini";

var apiKey = config["AzureOpenAI:ApiKey"]
             ?? config["AZURE_OPENAI_API_KEY"];

AzureOpenAIClient openAIClient = apiKey is not null
    ? new AzureOpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(apiKey))
    : new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());

// ── Tools ─────────────────────────────────────────────────────────────────────
AIFunction getCurrentDateTime = AIFunctionFactory.Create(
    () => DateTimeOffset.Now.ToString("f"),
    name: "get_current_datetime",
    description: "Returns the current local date and time.");

var expenseDb = new ExpenseDb("expenses.db");
var expenseTools = new ExpenseTools(expenseDb);

// ── Skill: budget advisor ─────────────────────────────────────────────────────
// AgentSkillsProvider injects the skill's instructions, resources, and scripts
// into the chat context only when the conversation warrants it — keeping the
// system prompt lean until the user asks a budget-related question.
var skillsProvider = new AgentSkillsProvider(new ExpenseBudgetSkill(expenseDb));

// ── Chat client with middleware pipeline ──────────────────────────────────────
// UseFunctionInvocation  — automatically executes any server-side tool calls
//                          the model makes and feeds the results back into the loop.
// UseAIContextProviders  — calls the skills provider before each request so
//                          relevant skill context is injected on demand.
IChatClient chatClient = openAIClient
    .GetChatClient(deploymentName)
    .AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation()
    .UseAIContextProviders(skillsProvider)
    .Build();

builder.Services.AddSingleton<IChatClient>(chatClient);

AITool[] tools =
[
    getCurrentDateTime,
    AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.AddExpense))!,        expenseTools),
    AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.GetExpenses))!,       expenseTools),
    AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.UpdateExpense))!,     expenseTools),
    AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.DeleteExpense))!,     expenseTools),
    AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.GetExpenseSummary))!, expenseTools),
];

// ── Agent (Microsoft.Agents.AI) ───────────────────────────────────────────────
// AIAgent wraps the chat client with session management, tool invocation,
// and skill injection. It exposes RunAsync (one-shot) and RunStreamingAsync.
var agentBuilder = builder.AddAIAgent(
    "expense-assistant",
    instructions: """
        You are a helpful personal finance assistant with access to an expense tracker.
        When the user asks to add, view, update, or delete expenses, use the appropriate tool.
        Always confirm actions and present amounts in a friendly, readable format.
        For summaries, present the data in a clear, organised way.
        """);

foreach (var tool in tools)
    agentBuilder.WithAITool(tool);

agentBuilder.WithInMemorySessionStore();

// ── OpenAI-compatible protocol support ────────────────────────────────────────
builder.AddOpenAIChatCompletions();  // POST /expense-assistant/v1/chat/completions
builder.AddOpenAIResponses();        // POST /expense-assistant/v1/responses (stateful)
builder.AddOpenAIConversations();    // POST /v1/conversations

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenAIChatCompletions(agentBuilder);
app.MapOpenAIResponses(agentBuilder);
app.MapOpenAIConversations();

// ── Custom chat endpoints (used by the browser UI) ────────────────────────────
// Sessions map a client threadId to an AIAgent session on the server.
// The agent session holds the full conversation history automatically.
var sessions = new ConcurrentDictionary<string, AgentSession>();

// POST /api/chat — non-streaming, returns the full reply in one response.
app.MapPost("/api/chat", async (
    ChatRequest request,
    [FromKeyedServices("expense-assistant")] AIAgent agentSvc) =>
{
    var (session, threadId) = await GetOrCreateSession(request.ThreadId, agentSvc);
    var response = await agentSvc.RunAsync(request.Message, session);
    return Results.Ok(new { threadId, reply = response.Text });
});

// POST /api/chat/stream — streaming reply via Server-Sent Events.
// SSE event types: { type:"thread" }, { type:"chunk", text }, [DONE]
app.MapPost("/api/chat/stream", async (
    HttpContext context,
    ChatRequest request,
    [FromKeyedServices("expense-assistant")] AIAgent agentSvc) =>
{
    SetSseHeaders(context);
    var ct = context.RequestAborted;

    var (session, threadId) = await GetOrCreateSession(request.ThreadId, agentSvc);
    await SseWriteAsync(context, new { type = "thread", threadId }, ct);

    await foreach (var update in agentSvc.RunStreamingAsync(request.Message, session).WithCancellation(ct))
    {
        if (!string.IsNullOrEmpty(update.Text))
            await SseWriteAsync(context, new { type = "chunk", text = update.Text }, ct);
    }

    await context.Response.WriteAsync("data: [DONE]\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
});

// ── Client-tools demo endpoint ────────────────────────────────────────────────
// Demonstrates LLM-driven tool calls that execute inside the user's browser.
//
// How it works:
//   1. The browser sends a user message to POST /api/clienttools/stream.
//   2. The server runs a manual agentic loop using a raw IChatClient
//      (no UseFunctionInvocation middleware — we drive the loop ourselves).
//   3. Server tools (date/time, expense queries) are executed immediately.
//   4. Client tools (GPS, screen info, confirm dialog) are emitted as
//      SSE `tool_call` events and the stream ends for that turn.
//   5. The browser executes the tool, then POSTs the results back
//      (same endpoint, no `message`, includes `toolResults`).
//   6. The loop resumes with the tool results appended to history.
//
// Why a raw client?
//   The DI `chatClient` uses UseFunctionInvocation(), which auto-executes ALL
//   tools on the server. Client tools must run in the browser, so we extract the
//   underlying OpenAI.Chat.ChatClient from the middleware chain to reuse its auth
//   while bypassing the auto-invocation layer.
var underlyingOpenAIClient = chatClient.GetService<global::OpenAI.Chat.ChatClient>();
IChatClient rawChatClient = underlyingOpenAIClient is not null
    ? underlyingOpenAIClient.AsIChatClient()
    : openAIClient.GetChatClient(deploymentName).AsIChatClient();

// Tool names whose implementations live in the browser (app.js), not on the server.
var clientToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "get_user_location", "get_screen_info", "confirm_with_user" };

// Client tool declarations — schema only. The LLM sees these as normal tools
// and will call them by name; the server forwards those calls to the browser.
AIFunction ctGetLocation = AIFunctionFactory.Create(
    () => "executed by client",
    name: "get_user_location",
    description: "Retrieves the user's current geographic location via the browser Geolocation API. " +
                 "Returns JSON with latitude, longitude, and accuracy in metres.");

AIFunction ctGetScreenInfo = AIFunctionFactory.Create(
    () => "executed by client",
    name: "get_screen_info",
    description: "Returns the user's screen and browser environment: screen dimensions, " +
                 "window size, user-agent string, language, timezone, and device pixel ratio.");

AIFunction ctConfirm = AIFunctionFactory.Create(
    ([Description("The yes/no question to present to the user in a browser dialog")] string question)
        => "executed by client",
    name: "confirm_with_user",
    description: "Displays a native browser confirmation dialog. " +
                 "Returns {\"confirmed\":true} if OK was clicked, {\"confirmed\":false} if cancelled.");

// Server-side tools available within the client-tools endpoint.
var ctServerFuncs = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase)
{
    ["get_current_datetime"] = (AIFunction)getCurrentDateTime,
    [nameof(ExpenseTools.GetExpenses)] = AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.GetExpenses))!, expenseTools),
    [nameof(ExpenseTools.GetExpenseSummary)] = AIFunctionFactory.Create(typeof(ExpenseTools).GetMethod(nameof(ExpenseTools.GetExpenseSummary))!, expenseTools),
};

ChatOptions ctChatOptions = new()
{
    Tools = [.. ctServerFuncs.Values, ctGetLocation, ctGetScreenInfo, ctConfirm],
    ToolMode = ChatToolMode.Auto,
};

const string ctSystemPrompt = """
    You are a helpful assistant demonstrating client-side tool execution.

    BROWSER TOOLS (executed inside the user's browser — never on the server):
    • get_user_location  — reads GPS coordinates via the Geolocation API
    • get_screen_info    — returns screen/window dimensions, user-agent, language, timezone
    • confirm_with_user  — shows a native browser confirmation dialog

    SERVER TOOLS (run on the server as usual):
    • get_current_datetime, GetExpenses, GetExpenseSummary

    When you call a browser tool the request is forwarded to the user's browser for execution.
    Always interpret the results in a friendly, human-readable way.
    When using confirm_with_user, honour the user's choice and explain what happens next.
    """;

// Per-session conversation history for the client-tools endpoint.
// (The standard chat endpoints use the AIAgent's built-in session store instead.)
var ctSessions = new ConcurrentDictionary<string, List<ChatMessage>>();

// POST /api/clienttools/stream — client-tools agentic loop over SSE.
// SSE event types: thread | status | chunk | tool_call | error | [DONE]
// Resume a turn by posting { threadId, toolResults:[{callId, result}] } with no `message`.
app.MapPost("/api/clienttools/stream", async (HttpContext ctx, ClientToolsRequest ctReq) =>
{
    SetSseHeaders(ctx);
    var ct = ctx.RequestAborted;

    // Local helper: serialize and flush one SSE data line.
    async Task SseAsync(object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    // Resolve an existing session or start a new one.
    string threadId;
    List<ChatMessage> history;
    if (!string.IsNullOrWhiteSpace(ctReq.ThreadId) && ctSessions.TryGetValue(ctReq.ThreadId, out var h))
        (threadId, history) = (ctReq.ThreadId, h);
    else
    {
        threadId = Guid.NewGuid().ToString("N");
        history = [];
        ctSessions[threadId] = history;
    }

    await SseAsync(new { type = "thread", threadId });

    // Append the new user message, or resume with client tool results.
    if (ctReq.Message is not null)
        history.Add(new ChatMessage(ChatRole.User, ctReq.Message));

    if (ctReq.ToolResults?.Length > 0)
        foreach (var tr in ctReq.ToolResults)
            history.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(tr.CallId, tr.Result)]));

    // Manual agentic loop — iterate until the model produces plain text.
    // The cap of 10 prevents runaway loops during development.
    for (var iter = 0; iter < 10; iter++)
    {
        // Build the full message list: system prompt + conversation history.
        var msgs = new List<ChatMessage> { new(ChatRole.System, ctSystemPrompt) };
        msgs.AddRange(history);

        ChatResponse response;
        try
        {
            response = await rawChatClient.GetResponseAsync(msgs, ctChatOptions, ct);
        }
        catch (Exception ex)
        {
            await SseAsync(new { type = "error", text = ex.Message });
            break;
        }

        history.AddMessages(response);

        var lastMsg = response.Messages.Last();
        var toolCalls = lastMsg.Contents.OfType<FunctionCallContent>().ToList();

        // No tool calls — model produced final text. Emit it and finish.
        if (toolCalls.Count == 0)
        {
            var text = string.Concat(lastMsg.Contents.OfType<TextContent>().Select(t => t.Text));
            if (!string.IsNullOrEmpty(text))
                await SseAsync(new { type = "chunk", text });
            break;
        }

        // Partition: server tools run now; client tools are delegated to the browser.
        var serverCalls = toolCalls.Where(tc => ctServerFuncs.ContainsKey(tc.Name ?? "")).ToList();
        var clientCalls = toolCalls.Where(tc => clientToolNames.Contains(tc.Name ?? "")).ToList();

        // Execute server tools inline and append results to history.
        foreach (var tc in serverCalls)
        {
            await SseAsync(new { type = "status", text = $"Running {tc.Name}…" });
            var func = ctServerFuncs[tc.Name!];
            object? result;
            try
            {
                result = await func.InvokeAsync(
                    tc.Arguments is not null ? new AIFunctionArguments(tc.Arguments) : null, ct);
            }
            catch (Exception ex) { result = $"Error: {ex.Message}"; }

            history.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(tc.CallId!, result?.ToString() ?? "null")]));
        }

        // Send client tool calls to the browser, then pause — the browser will
        // execute each tool and POST the results back to resume the loop.
        if (clientCalls.Count > 0)
        {
            foreach (var tc in clientCalls)
                await SseAsync(new
                {
                    type = "tool_call",
                    callId = tc.CallId,
                    name = tc.Name,
                    arguments = tc.Arguments,
                });
            break;
        }

        if (serverCalls.Count == 0) break;
    }

    await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
});

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────

async Task<(AgentSession session, string threadId)> GetOrCreateSession(string? threadId, AIAgent agent)
{
    if (!string.IsNullOrWhiteSpace(threadId) && sessions.TryGetValue(threadId, out var existing))
        return (existing, threadId);

    var id = Guid.NewGuid().ToString("N");
    var session = await agent.CreateSessionAsync();
    sessions[id] = session;
    return (session, id);
}

void SetSseHeaders(HttpContext context)
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
}

async Task SseWriteAsync(HttpContext context, object payload, CancellationToken ct)
{
    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
}

// ── Record types ──────────────────────────────────────────────────────────────
record ChatRequest(string Message, string? ThreadId = null);
record ClientToolsRequest(string? Message, string? ThreadId = null, ClientToolResult[]? ToolResults = null);
record ClientToolResult(string CallId, string Result);

