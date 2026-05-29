// AiCodingBar — opencode Plugin
// Runs inside the opencode process (Bun runtime) and forwards session/tool
// events to the AiCodingBar HTTP server (127.0.0.1:23333-23337).
//
// Design invariants:
//   - Zero dependencies (Bun's built-in fetch + fs/os/path + Bun.serve + node:crypto)
//   - fire-and-forget: event hook never awaits the fetch, so slow/broken receiver
//     cannot stall opencode
//   - same-state dedup — consecutive identical states skip POST
//   - self-healing port discovery: cache hit skips I/O; on miss we read
//     runtime.json, then fall back to a full SERVER_PORTS scan
//
// Permission handling:
//   opencode TUI does NOT bind an external HTTP listener. So we start a tiny
//   Bun.serve() bridge here: AiCodingBar POSTs decisions to the bridge, and
//   the bridge calls ctx.client._client.post() — the same in-process Hono
//   router that `opencode serve` would expose externally.

import { readFileSync, writeFileSync, mkdirSync, promises as fsp } from "fs";
import { homedir, platform } from "os";
import { join } from "path";
import { randomBytes, timingSafeEqual } from "crypto";

const RUNTIME_DIR = join(homedir(), ".aicoding-bar");
const RUNTIME_CONFIG_PATH = join(RUNTIME_DIR, "runtime.json");
const DEBUG_LOG_PATH = join(RUNTIME_DIR, "opencode-plugin.log");
const SERVER_PORTS = [23333, 23334, 23335, 23336, 23337];
const STATE_PATH = "/state";
const POST_TIMEOUT_MS = 1000;
const AGENT_ID = "opencode";

// opencode emits session.status=busy between every tool call as the LLM
// deliberates the next step; without this gate the display would flash
// thinking ↔ working on every invocation.
const ACTIVE_STATES_BLOCKING_THINKING = new Set(["working", "sweeping"]);

// Per plugin-instance state (scoped to one opencode process).
let _cachedPort = null;
let _lastState = null;
let _lastSessionId = null;
let _reqCounter = 0;
let _rootSessionId = null;
let _cwd = "";

// Reverse bridge state for permission replies
let _bridgeUrl = "";
let _bridgeTokenHex = "";
let _bridgeTokenBuf = null;
let _bridgeServer = null;
let _ctxClient = null;

// Debug logging — batched async flush so it never blocks the TUI main thread
const _debugBuffer = [];
let _debugFlushing = false;

function debugLog(msg) {
  _debugBuffer.push(`[${new Date().toISOString()}] ${msg}\n`);
  scheduleDebugFlush();
}

function scheduleDebugFlush() {
  if (_debugFlushing || _debugBuffer.length === 0) return;
  _debugFlushing = true;
  setImmediate(async () => {
    const chunk = _debugBuffer.join("");
    _debugBuffer.length = 0;
    try { await fsp.appendFile(DEBUG_LOG_PATH, chunk, "utf8"); } catch {}
    _debugFlushing = false;
    if (_debugBuffer.length > 0) scheduleDebugFlush();
  });
}

function resetDebugLog() {
  try {
    mkdirSync(RUNTIME_DIR, { recursive: true });
    writeFileSync(DEBUG_LOG_PATH, "", "utf8");
  } catch {}
}

// ── Port discovery ──

function readRuntimePort() {
  try {
    const raw = JSON.parse(readFileSync(RUNTIME_CONFIG_PATH, "utf8"));
    const port = Number(raw && raw.port);
    if (Number.isInteger(port) && SERVER_PORTS.includes(port)) return port;
  } catch {}
  return null;
}

function getPortCandidates() {
  const ordered = [];
  const seen = new Set();
  const add = (p) => {
    if (p && !seen.has(p) && SERVER_PORTS.includes(p)) { seen.add(p); ordered.push(p); }
  };
  add(_cachedPort);
  if (_cachedPort == null) add(readRuntimePort());
  SERVER_PORTS.forEach(add);
  return ordered;
}

// ── HTTP communication ──

function postToBar(urlPath, body, logTag) {
  if (_cwd) body.cwd = _cwd;
  body.agent_pid = process.pid;
  const payload = JSON.stringify(body);
  const candidates = getPortCandidates();
  const reqId = ++_reqCounter;

  (async () => {
    for (const port of candidates) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), POST_TIMEOUT_MS);
      try {
        const res = await fetch(`http://127.0.0.1:${port}${urlPath}`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: payload,
          signal: controller.signal,
        });
        clearTimeout(timer);
        const header = res.headers.get("x-aicoding-bar");
        if (header === "aicoding-bar") {
          _cachedPort = port;
          try { await res.text(); } catch {}
          debugLog(`POST[${reqId}] ${logTag} OK port=${port}`);
          return;
        }
      } catch { clearTimeout(timer); }
    }
    _cachedPort = null;
    debugLog(`POST[${reqId}] ${logTag} EXHAUSTED`);
  })().catch(() => {});
}

function postStateToBar(body) {
  postToBar(STATE_PATH, body, `STATE state=${body.state}`);
}

function postPermissionToBar(body) {
  postToBar("/permission", body, `PERM tool=${body.tool_name}`);
}

// ── State reporting ──

function sendState(state, eventName, sessionId) {
  if (!state || !eventName) return;

  // thinking 防抖门：working/sweeping 期间不回落 thinking
  if (state === "thinking" && ACTIVE_STATES_BLOCKING_THINKING.has(_lastState)) {
    return;
  }

  // 同状态去重
  if (state === _lastState && sessionId === _lastSessionId) return;

  _lastState = state;
  _lastSessionId = sessionId;

  postStateToBar({
    state,
    session_id: sessionId || "default",
    event: eventName,
    agent_id: AGENT_ID,
  });
}

// ── Event translation ──

function translateEvent(event) {
  if (!event || typeof event.type !== "string") return null;
  const props = event.properties || {};

  switch (event.type) {
    case "session.created":
      return { state: "idle", event: "SessionStart" };

    case "session.status": {
      const type = props.status && props.status.type;
      if (type === "busy") return { state: "thinking", event: "UserPromptSubmit" };
      return null;
    }

    case "message.part.updated": {
      const part = props.part;
      if (!part || typeof part !== "object") return null;

      if (part.type === "tool") {
        const status = part.state && part.state.status;
        if (status === "running") return { state: "working", event: "PreToolUse" };
        if (status === "completed") return { state: "working", event: "PostToolUse" };
        if (status === "error") return { state: "error", event: "PostToolUseFailure" };
        return null;
      }

      if (part.type === "compaction") {
        return { state: "sweeping", event: "PreCompact" };
      }

      return null;
    }

    case "session.compacted":
      return { state: "sweeping", event: "PreCompact" };

    case "session.idle":
      // 只有 root session 的 idle 才发 attention（完成）
      if (_rootSessionId && props.sessionID && props.sessionID !== _rootSessionId) {
        return { state: "sleeping", event: "SessionEnd" };
      }
      return { state: "attention", event: "Stop" };

    case "session.error":
      return { state: "error", event: "StopFailure" };

    case "session.deleted":
    case "server.instance.disposed":
      return { state: "sleeping", event: "SessionEnd" };

    default:
      return null;
  }
}

// ── Permission handling (Phase 2: reverse bridge) ──

function handlePermissionAsked(event) {
  const p = (event && event.properties) || {};
  const requestId = p.id;
  if (!requestId) return;

  postPermissionToBar({
    agent_id: AGENT_ID,
    tool_name: p.permission || "unknown",
    tool_input: p.metadata || {},
    patterns: Array.isArray(p.patterns) ? p.patterns : [],
    always: Array.isArray(p.always) ? p.always : [],
    session_id: _lastSessionId || "default",
    request_id: requestId,
    bridge_url: _bridgeUrl,
    bridge_token: _bridgeTokenHex,
  });
}

// ── Reverse bridge (Bun.serve) ──

function verifyBridgeToken(headerValue) {
  if (!headerValue || !_bridgeTokenBuf) return false;
  const m = /^Bearer\s+([a-f0-9]+)$/i.exec(headerValue);
  if (!m) return false;
  let candidate;
  try { candidate = Buffer.from(m[1], "hex"); } catch { return false; }
  if (candidate.length !== _bridgeTokenBuf.length) return false;
  try { return timingSafeEqual(candidate, _bridgeTokenBuf); } catch { return false; }
}

async function handleBridgeRequest(req) {
  const url = new URL(req.url);
  if (req.method !== "POST" || url.pathname !== "/reply") {
    return new Response("not found", { status: 404 });
  }
  if (!verifyBridgeToken(req.headers.get("authorization"))) {
    return new Response("unauthorized", { status: 401 });
  }
  let body;
  try { body = await req.json(); } catch {
    return new Response("bad json", { status: 400 });
  }
  const requestId = body && typeof body.request_id === "string" ? body.request_id : "";
  const reply = body && typeof body.reply === "string" ? body.reply : "";
  if (!requestId || !["once", "always", "reject"].includes(reply)) {
    return new Response("bad payload", { status: 400 });
  }
  if (!_ctxClient || !_ctxClient._client) {
    return new Response("plugin not ready", { status: 503 });
  }

  debugLog(`BRIDGE → opencode reply requestId=${requestId} reply=${reply}`);
  try {
    const result = await _ctxClient._client.post({
      url: `/permission/${encodeURIComponent(requestId)}/reply`,
      body: { reply },
      headers: { "Content-Type": "application/json" },
    });
    const hasError = result && result.error != null;
    if (hasError) {
      return new Response(JSON.stringify({ ok: false, error: String(result.error) }), {
        status: 502,
        headers: { "Content-Type": "application/json" },
      });
    }
    return new Response(JSON.stringify({ ok: true }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  } catch (err) {
    debugLog(`BRIDGE reply THROW requestId=${requestId} msg=${err && err.message}`);
    return new Response(JSON.stringify({ ok: false, error: String(err && err.message) }), {
      status: 502,
      headers: { "Content-Type": "application/json" },
    });
  }
}

function startBridge() {
  if (typeof Bun === "undefined" || !Bun.serve) {
    debugLog("BRIDGE not available: Bun.serve not found");
    return;
  }
  try {
    _bridgeTokenBuf = randomBytes(32);
    _bridgeTokenHex = _bridgeTokenBuf.toString("hex");
    _bridgeServer = Bun.serve({
      port: 0,
      hostname: "127.0.0.1",
      fetch: handleBridgeRequest,
    });
    _bridgeUrl = `http://127.0.0.1:${_bridgeServer.port}`;
    debugLog(`BRIDGE listening on ${_bridgeUrl}`);
  } catch (err) {
    debugLog(`BRIDGE start failed: ${err && err.message}`);
    _bridgeServer = null;
    _bridgeUrl = "";
    _bridgeTokenHex = "";
    _bridgeTokenBuf = null;
  }
}

// ── Plugin entrypoint ──

export default async (ctx) => {
  resetDebugLog();
  _ctxClient = ctx && ctx.client ? ctx.client : null;
  _cwd = ctx && typeof ctx.directory === "string" ? ctx.directory : "";
  debugLog(`INIT directory=${_cwd} pid=${process.pid} hasClient=${!!_ctxClient}`);
  startBridge();

  return {
    event: async ({ event }) => {
      try {
        if (!event || typeof event.type !== "string") return;

        // Capture root session ID (first session seen is the root)
        const sid = event.properties && event.properties.sessionID;
        if (sid && !_rootSessionId) {
          _rootSessionId = sid;
        }

        // Permission rides a parallel channel — skip state translation
        if (event.type === "permission.asked") {
          handlePermissionAsked(event);
          return;
        }

        const mapped = translateEvent(event);
        if (!mapped) return;

        const sessionId = (event.properties && event.properties.sessionID) || "default";
        sendState(mapped.state, mapped.event, sessionId);
      } catch (err) {
        debugLog(`ERROR in event hook: ${err && err.message}`);
      }
    },
  };
};
