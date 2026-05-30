#!/usr/bin/env node
// AiCodingBar — Hook Installer
// Injects AicodingBar status hooks into ~/.claude/settings.json
// State hooks run as async (non-blocking) so Claude Code is never blocked
// by AiCodingBar's HTTP round-trip during cold start or event handling.
// Safe: preserves non-AiCodingBar hooks, upgrades existing entries in-place.

const fs = require("fs");
const path = require("path");
const os = require("os");

const CLAUDE_DIR = path.join(os.homedir(), ".claude");
const SETTINGS_PATH = path.join(CLAUDE_DIR, "settings.json");
const HOOK_SCRIPT = path.join(__dirname, "claude-status-hook.js");
const RUNTIME_DIR = path.join(os.homedir(), ".aicoding-bar");

// 每个 state hook 的超时秒数 — Claude Code 在超时后继续执行不等结果
const STATE_HOOK_TIMEOUT_SECONDS = 5;

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
 * 在 Windows 上解析 node.exe 的绝对路径。
 * 部分运行环境（如 PowerShell）的 PATH 可能不包含 Node 安装目录，
 * 使用绝对路径确保 hook 能被可靠执行。
 * 参考 clawd-on-desk #317 修复。
 */
function resolveNodeBin() {
  if (process.platform !== "win32") return "node";

  // 1. where.exe node
  try {
    const { execFileSync } = require("child_process");
    const whereExe = path.join(process.env.SystemRoot || "C:\\Windows", "System32", "where.exe");
    const out = execFileSync(whereExe, ["node"], {
      encoding: "utf8",
      timeout: 2000,
      windowsHide: true,
    });
    for (const line of String(out || "").split(/\r?\n/)) {
      let trimmed = line.trim();
      if (!trimmed) continue;
      // where.exe 有时输出带引号的路径
      if (trimmed.startsWith('"') && trimmed.endsWith('"')) {
        trimmed = trimmed.slice(1, -1);
      }
      if (!path.isAbsolute(trimmed)) continue;
      // 排除 Scoop shim 路径（shims 下的 node.exe 是重定向脚本不是真实 node）
      if (trimmed.toLowerCase().replace(/\\/g, "/").includes("/scoop/shims/")) continue;
      // 排除 Electron 打包的 node（AiCodingBar 自身）
      if (trimmed.toLowerCase().includes("aicodingbar")) continue;
      try {
        fs.accessSync(trimmed, fs.constants.F_OK);
        return trimmed;
      } catch {}
    }
  } catch {}

  // 2. 常见安装路径
  const probes = [];
  if (process.env.ProgramFiles) probes.push(path.join(process.env.ProgramFiles, "nodejs", "node.exe"));
  if (process.env["ProgramFiles(x86)"]) probes.push(path.join(process.env["ProgramFiles(x86)"], "nodejs", "node.exe"));
  if (process.env.LOCALAPPDATA) {
    probes.push(path.join(process.env.LOCALAPPDATA, "Programs", "nodejs", "node.exe"));
    probes.push(path.join(process.env.LOCALAPPDATA, "Volta", "bin", "node.exe"));
  }
  for (const p of probes) {
    try {
      fs.accessSync(p, fs.constants.F_OK);
      return p;
    } catch {}
  }

  return "node"; // 回退到 PATH 中的 node
}

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

const nodeBin = resolveNodeBin();

/**
 * 构建理想的命令 hook 条目。
 * - async: true → Claude Code 不会阻塞等 hook 返回
 * - timeout: 5s → 超时后 Claude Code 继续执行
 * - PermissionRequest 保持同步阻塞（不通过此函数构建）
 */
function buildCommandHookEntry(event) {
  return {
    matcher: "",
    hooks: [
      {
        type: "command",
        command: `"${nodeBin}" "${HOOK_SCRIPT.replace(/\\/g, "\\\\")}" ${event}`,
        timeout: STATE_HOOK_TIMEOUT_SECONDS,
        async: true,
      }
    ],
  };
}

/**
 * 检测 eventHooks 数组中是否已有我们的 hook 条目，
 * 并返回其 index；同时返回是否需要升级（缺少 async/timeout 等字段）。
 */
function inspectOurHook(eventHooks, desiredEntry) {
  for (let i = 0; i < eventHooks.length; i++) {
    const group = eventHooks[i];
    if (!group || !Array.isArray(group.hooks)) continue;
    for (const h of group.hooks) {
      if (h && h.command && h.command.includes("claude-status-hook.js")) {
        // 检查是否需要升级：比较 async / timeout / command
        const needsUpgrade =
          h.async !== desiredEntry.hooks[0].async ||
          h.timeout !== desiredEntry.hooks[0].timeout ||
          h.command !== desiredEntry.hooks[0].command;
        return { found: true, index: i, needsUpgrade };
      }
    }
  }
  return { found: false, index: -1, needsUpgrade: false };
}

/**
 * 判断某个 hook 条目组是否属于 AiCodingBar（用于卸载过滤）
 */
function isOurHookGroup(group) {
  if (!group || !Array.isArray(group.hooks)) return false;
  for (const h of group.hooks) {
    if (h && h.command && h.command.includes("claude-status-hook.js")) return true;
    if (h && h.url && h.url.includes("aicoding-bar")) return true;
  }
  return false;
}

/**
 * 查找我们的 HTTP hook（PermissionRequest）
 */
function findOurHttpHook(eventHooks) {
  for (const group of eventHooks) {
    if (group && Array.isArray(group.hooks)) {
      for (const h of group.hooks) {
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

  let added = 0, skipped = 0, upgraded = 0;

  // Command hooks for state events
  for (const event of HOOK_EVENTS) {
    settings.hooks[event] = settings.hooks[event] || [];
    const desired = buildCommandHookEntry(event);
    const { found, index, needsUpgrade } = inspectOurHook(settings.hooks[event], desired);

    if (found && needsUpgrade) {
      // 原地替换已有条目，避免重复
      settings.hooks[event][index] = desired;
      console.log(`  ↑ ${event} (upgraded — added async:true, timeout:${STATE_HOOK_TIMEOUT_SECONDS}s)`);
      upgraded++;
    } else if (found) {
      console.log(`  - ${event} (already installed & up-to-date)`);
      skipped++;
    } else {
      settings.hooks[event].push(desired);
      console.log(`  + ${event}`);
      added++;
    }
  }

  // PermissionRequest HTTP hook (同步阻塞, /permission endpoint)
  // 不设置 async: true — 权限请求必须阻塞等用户决策
  const port = readRuntimePort();
  const permissionUrl = `http://127.0.0.1:${port}/permission?source=aicoding-bar`;
  settings.hooks["PermissionRequest"] = settings.hooks["PermissionRequest"] || [];
  if (!findOurHttpHook(settings.hooks["PermissionRequest"])) {
    settings.hooks["PermissionRequest"].push({
      matcher: "",
      hooks: [{
        type: "http",
        url: permissionUrl,
        timeout: 600,
      }]
    });
    console.log(`  + PermissionRequest (HTTP hook → :${port}/permission, blocking)`);
    added++;
  } else {
    console.log(`  - PermissionRequest (already installed)`);
    skipped++;
  }

  writeSettings(settings);
  console.log(`\nAiCodingBar hooks → ${SETTINGS_PATH}`);
  if (added > 0) console.log(`  Added: ${added}`);
  if (upgraded > 0) console.log(`  Upgraded: ${upgraded}`);
  if (skipped > 0) console.log(`  Skipped: ${skipped}`);
  if (nodeBin !== "node") console.log(`  Node: ${nodeBin}`);
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
    settings.hooks[event] = settings.hooks[event].filter((group) => !isOurHookGroup(group));
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
