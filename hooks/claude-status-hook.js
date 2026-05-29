#!/usr/bin/env node
// AiCodingBar — Claude Code Status Hook
// Usage: node claude-status-hook.js <event_name>
// Reads stdin JSON from Claude Code, POSTs state to AiCodingBar HTTP server

const http = require("http");
const fs = require("fs");
const path = require("path");
const os = require("os");

const RUNTIME_DIR = path.join(os.homedir(), ".aicoding-bar");
const RUNTIME_FILE = path.join(RUNTIME_DIR, "runtime.json");

const EVENT_TO_STATE = {
  SessionStart: "idle",
  SessionEnd: "sleeping",
  UserPromptSubmit: "thinking",
  PreToolUse: "working",
  PostToolUse: "working",
  PostToolUseFailure: "error",
  Stop: "attention",
  StopFailure: "error",
  SubagentStart: "juggling",
  SubagentStop: "working",
  PreCompact: "sweeping",
  PostCompact: "attention",
  Notification: "notification",
  Elicitation: "notification",
  WorktreeCreate: "carrying",
};

function readRuntimePort() {
  try {
    const raw = fs.readFileSync(RUNTIME_FILE, "utf8");
    const data = JSON.parse(raw);
    return data.port || 23333;
  } catch {
    return 23333;
  }
}

function readStdinJson() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => { data += chunk; });
    process.stdin.on("end", () => {
      try { resolve(JSON.parse(data || "{}")); }
      catch { resolve({}); }
    });
    process.stdin.resume();
  });
}

function postState(body) {
  return new Promise((resolve) => {
    const port = readRuntimePort();
    const json = JSON.stringify(body);
    const req = http.request({
      hostname: "127.0.0.1",
      port,
      path: "/state",
      method: "POST",
      timeout: 500,
      headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(json) },
    }, (res) => { res.resume(); res.on("end", resolve); });
    req.on("error", () => resolve());
    req.on("timeout", () => { req.destroy(); resolve(); });
    req.write(json);
    req.end();
  });
}

function getAgentPid(resolve) {
  try {
    if (typeof resolve === "function") {
      const result = resolve();
      return result?.agentPid || null;
    }
  } catch {}
  return null;
}

async function main() {
  const event = process.argv[2];
  const state = EVENT_TO_STATE[event];
  if (!state) process.exit(0);

  const payload = await readStdinJson();
  const sessionId = payload.session_id || "default";

  const body = {
    state,
    session_id: sessionId,
    event,
    agent_id: "claude-code",
  };

  if (payload.cwd) body.cwd = payload.cwd;
  if (payload.tool_name) body.tool_name = payload.tool_name;
  if (payload.session_title) body.session_title = payload.session_title;
  if (payload.source_pid) body.source_pid = payload.source_pid;
  if (payload.agent_pid) body.agent_pid = payload.agent_pid;

  await postState(body);
  process.exit(0);
}

main().catch(() => process.exit(0));
