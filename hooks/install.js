#!/usr/bin/env node
// AiCodingBar — Hook Installer
// Injects AicodingBar status hooks into ~/.claude/settings.json
// Safe: preserves existing hooks, only adds what's missing.

const fs = require("fs");
const path = require("path");
const os = require("os");

const CLAUDE_DIR = path.join(os.homedir(), ".claude");
const SETTINGS_PATH = path.join(CLAUDE_DIR, "settings.json");
const HOOK_SCRIPT = path.join(__dirname, "claude-status-hook.js");
const RUNTIME_DIR = path.join(os.homedir(), ".aicoding-bar");

const HOOK_EVENTS = [
  "SessionStart", "SessionEnd", "UserPromptSubmit",
  "PreToolUse", "PostToolUse", "PostToolUseFailure",
  "Stop", "StopFailure",
  "SubagentStart", "SubagentStop",
  "PreCompact", "PostCompact",
  "Notification", "Elicitation", "WorktreeCreate",
];

const MONITOR_HOOK_KEY = "__aicoding_bar__";

/**
 * 读取 ~/.aicoding-bar/runtime.json 获取实际监听端口，
 * 用于 PermissionRequest HTTP hook 的 URL。
 */
function readRuntimePort() {
  try {
    const runtimePath = path.join(RUNTIME_DIR, "runtime.json");
    if (fs.existsSync(runtimePath)) {
      const raw = fs.readFileSync(runtimePath, "utf8");
      const data = JSON.parse(raw);
      return data.port || 23333;
    }
  } catch {}
  return 23333;
}

function readSettings() {
  try {
    if (fs.existsSync(SETTINGS_PATH)) {
      return JSON.parse(fs.readFileSync(SETTINGS_PATH, "utf8"));
    }
  } catch {}
  return {};
}

function writeSettings(settings) {
  if (!fs.existsSync(CLAUDE_DIR)) fs.mkdirSync(CLAUDE_DIR, { recursive: true });
  fs.writeFileSync(SETTINGS_PATH, JSON.stringify(settings, null, 2), "utf8");
}

function buildCommandHookEntry(event) {
  return {
    matcher: "",
    hooks: [
      {
        type: "command",
        command: `node "${HOOK_SCRIPT.replace(/\\/g, "\\\\")}" ${event}`,
        timeout: 5,
      }
    ],
  };
}

function findOurHook(eventHooks) {
  for (const group of eventHooks) {
    if (group && Array.isArray(group.hooks)) {
      for (const h of group.hooks) {
        if (h && h.command && h.command.includes("claude-status-hook.js")) {
          return true;
        }
        // Also check HTTP hooks that point to our /permission endpoint
        if (h && h.url && h.url.includes("aicoding-bar")) {
          return true;
        }
      }
    }
  }
  return false;
}

function install() {
  const settings = readSettings();
  settings.hooks = settings.hooks || {};

  // Mark our territory
  settings[MONITOR_HOOK_KEY] = { installed: true, installedAt: new Date().toISOString() };

  // Command hooks for state events
  for (const event of HOOK_EVENTS) {
    settings.hooks[event] = settings.hooks[event] || [];

    if (findOurHook(settings.hooks[event])) {
      console.log(`  - ${event} (already installed)`);
    } else {
      settings.hooks[event].push(buildCommandHookEntry(event));
      console.log(`  + ${event}`);
    }
  }

  // PermissionRequest HTTP hook (blocking, /permission endpoint)
  const port = readRuntimePort();
  settings.hooks["PermissionRequest"] = settings.hooks["PermissionRequest"] || [];
  if (!findOurHook(settings.hooks["PermissionRequest"])) {
    settings.hooks["PermissionRequest"].push({
      matcher: "",
      hooks: [{
        type: "http",
        url: `http://127.0.0.1:${port}/permission`,
        timeout: 600,
      }]
    });
    console.log(`  + PermissionRequest (HTTP hook → :${port}/permission)`);
  } else {
    console.log(`  - PermissionRequest (already installed)`);
  }

  writeSettings(settings);
  console.log(`\nAiCodingBar hooks installed to ${SETTINGS_PATH}`);
}

function uninstall() {
  if (!fs.existsSync(SETTINGS_PATH)) {
    console.log("No settings.json found. Nothing to uninstall.");
    return;
  }

  const settings = readSettings();
  if (!settings.hooks) {
    console.log("No hooks found. Nothing to uninstall.");
    return;
  }

  for (const event of Object.keys(settings.hooks)) {
    const before = settings.hooks[event].length;
    settings.hooks[event] = settings.hooks[event].filter((group) => {
      return !findOurHook([group]);
    });
    const removed = before - settings.hooks[event].length;
    if (removed > 0) console.log(`  - ${event}`);
    if (settings.hooks[event].length === 0) delete settings.hooks[event];
  }

  if (Object.keys(settings.hooks).length === 0) delete settings.hooks;
  delete settings[MONITOR_HOOK_KEY];

  writeSettings(settings);
  console.log(`\nAiCodingBar hooks removed from ${SETTINGS_PATH}`);
}

const cmd = process.argv[2];
if (cmd === "uninstall") {
  console.log("Removing AiCodingBar hooks...");
  uninstall();
} else {
  console.log("Installing AiCodingBar hooks...");
  install();
}
