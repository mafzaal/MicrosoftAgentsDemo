
const messagesEl  = document.getElementById('messages');
const form        = document.getElementById('form');
const input       = document.getElementById('input');
const sendBtn     = document.getElementById('send-btn');
const statusText  = document.getElementById('status-text');
const newChatBtn  = document.getElementById('new-chat-btn');

// Persists the server-side AgentSession across turns
let threadId = null;

function scrollToBottom() {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

function addMessage(role) {
  const wrap = document.createElement('div');
  wrap.className = `message ${role}`;

  const avatar = document.createElement('div');
  avatar.className = 'avatar';
  avatar.textContent = role === 'user' ? 'U' : 'A';

  const bubble = document.createElement('div');
  bubble.className = 'bubble';

  wrap.appendChild(avatar);
  wrap.appendChild(bubble);
  messagesEl.appendChild(wrap);
  scrollToBottom();
  return bubble;
}

function showTyping() {
  const wrap = document.createElement('div');
  wrap.className = 'message assistant';
  wrap.id = 'typing-indicator';

  const avatar = document.createElement('div');
  avatar.className = 'avatar';
  avatar.textContent = 'A';

  const dots = document.createElement('div');
  dots.className = 'bubble';
  dots.innerHTML = '<div class="typing-dots"><span></span><span></span><span></span></div>';

  wrap.appendChild(avatar);
  wrap.appendChild(dots);
  messagesEl.appendChild(wrap);
  scrollToBottom();
  return wrap;
}

function setWorking(working) {
  sendBtn.disabled = working;
  statusText.textContent = working ? 'Thinking…' : 'Ready';
}

async function sendMessage(text) {
  if (mode === 'clienttools') { await sendClientToolsMessage(text); return; }
  addMessage('user').textContent = text;
  input.value = '';
  input.style.height = 'auto';
  setWorking(true);

  const typingEl = showTyping();
  let bubble;
  let started = false;

  try {
    const res = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: text, threadId }),
    });

    if (!res.ok) throw new Error(`Server error ${res.status}`);

    const reader  = res.body.getReader();
    const decoder = new TextDecoder();
    let   buffer  = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop();

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const raw = line.slice(6).trim();
        if (raw === '[DONE]') break;

        let parsed;
        try { parsed = JSON.parse(raw); } catch { continue; }

        if (parsed.type === 'thread') {
          threadId = parsed.threadId;
          continue;
        }

        if (parsed.type === 'chunk' && parsed.text) {
          if (!started) {
            typingEl.remove();
            bubble = addMessage('assistant');
            started = true;
          }
          bubble.textContent += parsed.text;
          scrollToBottom();
        }
      }
    }

    if (!started) {
      typingEl.remove();
      addMessage('assistant').textContent = '(no response)';
    }
  } catch (err) {
    typingEl.remove();
    const b = addMessage('assistant');
    b.textContent = 'Error: ' + err.message;
    b.classList.add('error');
  } finally {
    setWorking(false);
  }
}

newChatBtn.addEventListener('click', () => {
  threadId = null;
  messagesEl.innerHTML = '';
  const wrap   = document.createElement('div');
  wrap.className = 'message assistant';
  const avatar = document.createElement('div');
  avatar.className = 'avatar';
  avatar.textContent = 'A';
  const bubble = document.createElement('div');
  bubble.className = 'bubble';
  bubble.textContent = 'Hello! How can I help you today?';
  wrap.appendChild(avatar);
  wrap.appendChild(bubble);
  messagesEl.appendChild(wrap);
  input.focus();
});

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const text = input.value.trim();
  if (text) await sendMessage(text);
});

input.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    form.dispatchEvent(new Event('submit'));
  }
});

input.addEventListener('input', () => {
  input.style.height = 'auto';
  input.style.height = Math.min(input.scrollHeight, 130) + 'px';
});

// ── Mode: standard expense-assistant vs. client-tools demo ───────────────────
let mode       = 'standard';   // 'standard' | 'clienttools'
let ctThreadId = null;

const modeToggleBtn = document.getElementById('mode-toggle-btn');

modeToggleBtn.addEventListener('click', () => {
  mode = mode === 'standard' ? 'clienttools' : 'standard';
  const isClientTools = mode === 'clienttools';
  modeToggleBtn.classList.toggle('active', isClientTools);
  document.querySelector('.header-title').textContent =
    isClientTools ? 'Client Tools Demo' : 'AI Agent';
  input.placeholder = isClientTools
    ? 'Try: "Where am I?", "What browser am I using?", "Delete my oldest expense (ask me first)"'
    : 'Type a message… (Enter to send, Shift+Enter for newline)';
  ctThreadId = null;
  threadId   = null;
  messagesEl.innerHTML = '';
  const bubble = addMessage('assistant');
  bubble.textContent = isClientTools
    ? 'Client Tools mode active! I can call tools that run directly in your browser — like reading your location, screen info, or showing a confirmation dialog. What would you like to try?'
    : 'Hello! How can I help you today?';
});

// ── Browser-side tool implementations ────────────────────────────────────────
const clientToolHandlers = {
  get_user_location: () => new Promise(resolve => {
    if (!navigator.geolocation)
      return resolve(JSON.stringify({ error: 'Geolocation not supported by this browser' }));
    navigator.geolocation.getCurrentPosition(
      p => resolve(JSON.stringify({
        latitude:        parseFloat(p.coords.latitude.toFixed(6)),
        longitude:       parseFloat(p.coords.longitude.toFixed(6)),
        accuracy_metres: Math.round(p.coords.accuracy),
      })),
      e => resolve(JSON.stringify({ error: e.message })),
      { timeout: 10000, enableHighAccuracy: false },
    );
  }),

  get_screen_info: () => Promise.resolve(JSON.stringify({
    screen:  { width: screen.width, height: screen.height, colorDepth: screen.colorDepth },
    window:  { width: innerWidth,   height: innerHeight,   devicePixelRatio },
    browser: { userAgent: navigator.userAgent, language: navigator.language },
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  })),

  confirm_with_user: args => {
    const question  = args?.question ?? 'Do you confirm?';
    const confirmed = confirm(question);
    return Promise.resolve(JSON.stringify({ confirmed }));
  },
};

async function executeClientTool(name, args) {
  const handler = clientToolHandlers[name];
  if (!handler) return JSON.stringify({ error: `Unknown client tool: ${name}` });
  try {
    const parsed = typeof args === 'string' ? JSON.parse(args) : (args ?? {});
    return await handler(parsed);
  } catch (e) {
    return JSON.stringify({ error: e.message });
  }
}

// ── Client-tools streaming loop ───────────────────────────────────────────────
async function sendClientToolsMessage(text) {
  addMessage('user').textContent = text;
  input.value = '';
  input.style.height = 'auto';
  setWorking(true);
  await runClientToolsStream({ message: text, threadId: ctThreadId });
  setWorking(false);
}

async function runClientToolsStream(body) {
  const typingEl = showTyping();
  let bubble  = null;
  let started = false;

  try {
    let currentBody = body;

    while (true) {
      const pendingToolCalls = [];

      const res = await fetch('/api/clienttools/stream', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify(currentBody),
      });
      if (!res.ok) throw new Error(`Server error ${res.status}`);

      const reader  = res.body.getReader();
      const decoder = new TextDecoder();
      let   buffer  = '';
      let   done    = false;

      while (!done) {
        const { done: streamDone, value } = await reader.read();
        if (streamDone) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop();

        for (const line of lines) {
          if (!line.startsWith('data: ')) continue;
          const raw = line.slice(6).trim();
          if (raw === '[DONE]') { done = true; break; }

          let parsed;
          try { parsed = JSON.parse(raw); } catch { continue; }

          if (parsed.type === 'thread') {
            ctThreadId = parsed.threadId;
          } else if (parsed.type === 'chunk' && parsed.text) {
            if (!started) { typingEl.remove(); bubble = addMessage('assistant'); started = true; }
            bubble.textContent += parsed.text;
            scrollToBottom();
          } else if (parsed.type === 'status') {
            const dotsEl = typingEl.querySelector('.bubble');
            if (dotsEl) dotsEl.textContent = parsed.text;
          } else if (parsed.type === 'tool_call') {
            pendingToolCalls.push(parsed);
          } else if (parsed.type === 'error') {
            throw new Error(parsed.text);
          }
        }
      }

      if (!started && pendingToolCalls.length > 0) {
        typingEl.remove();
        bubble  = addMessage('assistant');
        started = true;
      }

      if (pendingToolCalls.length === 0) break;

      // Execute each client tool in the browser, then resume
      const toolResults = [];
      for (const tc of pendingToolCalls) {
        appendToolBadge(bubble, `🔧 Calling: ${tc.name}…`);
        scrollToBottom();
        const result = await executeClientTool(tc.name, tc.arguments);
        appendToolBadge(bubble, `✅ ${tc.name} returned`, 'tool-result-badge');
        toolResults.push({ callId: tc.callId, result });
      }

      currentBody = { threadId: ctThreadId, toolResults };
    }

    if (!started) { typingEl.remove(); addMessage('assistant').textContent = '(no response)'; }
  } catch (err) {
    typingEl.remove();
    const b = addMessage('assistant');
    b.textContent = 'Error: ' + err.message;
    b.classList.add('error');
  }
}

function appendToolBadge(bubble, label, extraClass = 'tool-call-badge') {
  const el = document.createElement('span');
  el.className = `tool-badge ${extraClass}`;
  el.textContent = label;
  bubble.appendChild(el);
}
