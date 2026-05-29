#!/usr/bin/env node
// ClaudeMonitor — Hook Installer
// Injects ClaudeMonitor status hooks into ~/.claude/settings.json
// Safe: preserves existing hooks, only adds what's missing.

const fs = require("fs");
const path = require("path");
const os = require("os");

const CLAUDE_DIR = path.join(os.homedir(), ".claude");
const SETTINGS_PATH = path.join(CLAUDE_DIR, "settings.json");
const HOOK_SCRIPT = path.join(__dirname, "claude-status-hook.js");

const HOOK_EVENTS = [
  "SessionStart", "SessionEnd", "UserPromptSubmit",
  "PreToolUse", "PostToolUse", "PostToolUseFailure",
  "Stop", "StopFailure",
  "SubagentStart", "SubagentStop",
  "PreCompact", "PostCompact",
  "Notification", "Elicitation", "WorktreeCreate",
];

const MONITOR_HOOK_KEY = "__claude_monitor__";

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

function buildHookEntry(event) {
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

  for (const event of HOOK_EVENTS) {
    settings.hooks[event] = settings.hooks[event] || [];

    if (findOurHook(settings.hooks[event])) {
      console.log(`  - ${event} (already installed)`);
    } else {
      settings.hooks[event].push(buildHookEntry(event));
      console.log(`  + ${event}`);
    }
  }

  writeSettings(settings);
  console.log(`\nClaudeMonitor hooks installed to ${SETTINGS_PATH}`);
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
      // Remove matcher groups that contain our hook
      return !findOurHook([group]);
    });
    const removed = before - settings.hooks[event].length;
    if (removed > 0) console.log(`  - ${event}`);
    if (settings.hooks[event].length === 0) delete settings.hooks[event];
  }

  if (Object.keys(settings.hooks).length === 0) delete settings.hooks;
  delete settings[MONITOR_HOOK_KEY];

  writeSettings(settings);
  console.log(`\nClaudeMonitor hooks removed from ${SETTINGS_PATH}`);
}

const cmd = process.argv[2];
if (cmd === "uninstall") {
  console.log("Removing ClaudeMonitor hooks...");
  uninstall();
} else {
  console.log("Installing ClaudeMonitor hooks...");
  install();
}
