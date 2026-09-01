const { execFile } = require('child_process');
const fs = require('fs');
const path = require('path');
const log = require('./logger');

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function execPs(script, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    execFile(
      'powershell',
      ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script],
      { maxBuffer: 16 * 1024 * 1024, timeout: timeoutMs },
      (err, stdout, stderr) => {
        if (err) reject(new Error(stderr?.trim() || err.message));
        else resolve(stdout.trim());
      }
    );
  });
}

function tryLoadNodeScreenshots() {
  try {
    return require('node-screenshots');
  } catch {
    return null;
  }
}

function pickBestWindow(windows, pid) {
  return windows
    .filter((w) => w.pid() === pid && !w.isMinimized() && w.width() >= 200 && w.height() >= 200)
    .sort((a, b) => b.width() * b.height() - a.width() * a.height())[0];
}

async function findWindowPs(pid) {
  const scriptPath = path.join(__dirname, 'winCapture.ps1');
  const out = await execPs(`& '${scriptPath.replace(/'/g, "''")}' -Mode Find -ProcessId ${pid}`);
  if (!out || out === 'NONE') return null;
  const parts = out.split('|');
  if (parts.length < 5) return null;
  return {
    hwnd: parseInt(parts[0], 10),
    x: parseInt(parts[1], 10),
    y: parseInt(parts[2], 10),
    width: parseInt(parts[3], 10),
    height: parseInt(parts[4], 10),
    title: parts[5] || '',
    source: 'powershell',
  };
}

async function findWindow(pid) {
  const ns = tryLoadNodeScreenshots();
  if (ns?.Window) {
    try {
      const win = pickBestWindow(ns.Window.all(), pid);
      if (win) {
        return {
          hwnd: win.id(),
          x: win.x(),
          y: win.y(),
          width: win.width(),
          height: win.height(),
          title: win.title() || win.appName() || '',
          window: win,
          source: 'node-screenshots',
        };
      }
    } catch (err) {
      log.warn('node-screenshots find failed', err.message);
    }
  }
  return findWindowPs(pid);
}

async function waitForGameWindow(pid, options = {}) {
  const timeoutMs = options.timeoutMs ?? 90000;
  const pollMs = options.pollMs ?? 500;
  const minWidth = options.minWidth ?? 200;
  const minHeight = options.minHeight ?? 200;
  const deadline = Date.now() + timeoutMs;

  log.info('Waiting for game window…', { pid, timeoutMs });

  while (Date.now() < deadline) {
    const info = await findWindow(pid);
    if (info && info.width >= minWidth && info.height >= minHeight) {
      log.info('Game window ready', {
        title: info.title,
        size: `${info.width}x${info.height}`,
        source: info.source,
      });
      return info;
    }
    await sleep(pollMs);
  }

  throw new Error(`Game window not found within ${Math.round(timeoutMs / 1000)}s (is the game still launching?)`);
}

async function captureWindowPs(hwnd, quality) {
  const scriptPath = path.join(__dirname, 'winCapture.ps1');
  const out = await execPs(
    `& '${scriptPath.replace(/'/g, "''")}' -Mode Capture -Hwnd ${hwnd} -Quality ${Math.max(20, Math.min(90, quality))}`
  );
  if (!out || out === 'FAIL') throw new Error('Window capture failed');
  return Buffer.from(out, 'base64');
}

async function captureWindow(windowInfo, quality) {
  if (windowInfo.window?.captureImageSync) {
    try {
      const image = windowInfo.window.captureImageSync();
      if (image?.toJpegSync) return image.toJpegSync(true);
      if (image?.toPngSync) return image.toPngSync(true);
    } catch (err) {
      log.warn('node-screenshots capture failed', err.message);
    }
  }

  if (windowInfo.hwnd) {
    try {
      return await captureWindowPs(windowInfo.hwnd, quality);
    } catch (err) {
      log.warn('PowerShell window capture failed', err.message);
    }
  }

  throw new Error('No capture method available');
}

async function refreshWindowBounds(windowInfo, pid) {
  const fresh = await findWindow(pid);
  if (!fresh) return windowInfo;
  windowInfo.window = fresh.window;
  windowInfo.x = fresh.x;
  windowInfo.y = fresh.y;
  windowInfo.width = fresh.width;
  windowInfo.height = fresh.height;
  windowInfo.hwnd = fresh.hwnd;
  windowInfo.title = fresh.title;
  return windowInfo;
}

module.exports = {
  waitForGameWindow,
  captureWindow,
  refreshWindowBounds,
  findWindow,
};
