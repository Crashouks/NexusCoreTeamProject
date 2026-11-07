const signalR = require('@microsoft/signalr');
const log = require('./logger');
const { waitForGameWindow, captureWindow, refreshWindowBounds } = require('./windowCapture');
const { handleInput, setInputMapper, stopInputBridge } = require('./input');

function hubBase(apiUrl) {
  return apiUrl.replace(/\/api\/?$/, '');
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/** Adjusts JPEG quality and frame interval from capture/send timing. */
class AdaptiveStreamControl {
  constructor(options = {}) {
    const maxFps = options.maxFps ?? 20;
    const minFps = options.minFps ?? 2;
    this.minInterval = Math.round(1000 / maxFps);
    this.maxInterval = Math.round(1000 / minFps);
    this.minQuality = options.minQuality ?? 22;
    this.maxQuality = options.maxQuality ?? 72;
    this.quality = options.startQuality ?? 45;
    this.intervalMs = options.startIntervalMs ?? 180;
    this.consecutiveGood = 0;
    this.consecutiveBad = 0;
    this.framesSent = 0;
    this.lastLogAt = Date.now();
  }

  get targetFps() {
    return Math.max(1, Math.round(1000 / this.intervalMs));
  }

  noteSuccess(captureMs, sendMs, frameBytes) {
    this.framesSent += 1;
    const totalMs = captureMs + sendMs;
    const heavyFrame = frameBytes > 750_000;

    if (totalMs > this.intervalMs * 1.12 || heavyFrame) {
      this.consecutiveBad += 1;
      this.consecutiveGood = 0;
      if (this.consecutiveBad >= 2) this.stepDown(heavyFrame ? 'large frame' : 'slow pipeline');
    } else if (totalMs < this.intervalMs * 0.62) {
      this.consecutiveGood += 1;
      this.consecutiveBad = 0;
      if (this.consecutiveGood >= 4) this.stepUp();
    } else {
      this.consecutiveGood = Math.max(0, this.consecutiveGood - 1);
      this.consecutiveBad = Math.max(0, this.consecutiveBad - 1);
    }

    this.maybeLogStats();
  }

  noteFailure(reason) {
    this.consecutiveBad += 2;
    this.consecutiveGood = 0;
    this.stepDown(reason || 'send error');
  }

  stepDown(reason) {
    this.consecutiveBad = 0;
    const prevFps = this.targetFps;
    const prevQ = this.quality;
    this.intervalMs = Math.min(this.maxInterval, Math.round(this.intervalMs * 1.22));
    this.quality = Math.max(this.minQuality, this.quality - 10);
    if (this.targetFps !== prevFps || this.quality !== prevQ) {
      log.info(`Stream adapt ↓ (${reason})`, { fps: this.targetFps, quality: this.quality });
    }
  }

  stepUp() {
    this.consecutiveGood = 0;
    const prevFps = this.targetFps;
    const prevQ = this.quality;
    this.intervalMs = Math.max(this.minInterval, Math.round(this.intervalMs * 0.9));
    if (this.intervalMs <= 70 && this.quality < this.maxQuality) {
      this.quality = Math.min(this.maxQuality, this.quality + 6);
    }
    if (this.targetFps !== prevFps || this.quality !== prevQ) {
      log.info('Stream adapt ↑', { fps: this.targetFps, quality: this.quality });
    }
  }

  maybeLogStats() {
    const now = Date.now();
    if (now - this.lastLogAt < 15000) return;
    this.lastLogAt = now;
    log.info('Stream stats', { fps: this.targetFps, quality: this.quality, framesSent: this.framesSent });
    this.framesSent = 0;
  }
}

/**
 * Keeps retrying after a dropped hub connection (API restart, Tailscale blip, laptop sleep) instead of
 * giving up after a few seconds: quick first retries, then every 10s, until `maxElapsedMs` has passed.
 */
function createReconnectPolicy(maxElapsedMs = 5 * 60 * 1000) {
  const delays = [0, 1000, 2000, 5000];
  return {
    nextRetryDelayInMilliseconds(ctx) {
      if (ctx.elapsedMilliseconds >= maxElapsedMs) return null;
      return delays[ctx.previousRetryCount] ?? 10000;
    },
  };
}

async function startStreaming({
  apiUrl, serverId, password, sessionId, streamOptions,
  gamePid, gameWindowOnly = true, waitForGameMs = 90000,
  telemetryIntervalMs = 2000,
}) {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${hubBase(apiUrl)}/hubs/cloud-stream`)
    .withAutomaticReconnect(createReconnectPolicy())
    .build();

  const adaptive = new AdaptiveStreamControl(streamOptions);
  let stopped = false;
  let loopPromise = null;
  let telemetryTimer = null;
  let telemetryReportPending = false;
  let telemetryWindow = createTelemetryWindow();
  let gameWindow = null;
  let streamWidth = 1920;
  let streamHeight = 1080;
  let boundsRefreshCounter = 0;

  setInputMapper((input) => {
    if (!gameWindow || input.x == null || input.y == null) return input;
    return {
      ...input,
      x: Math.round(gameWindow.x + (input.x / streamWidth) * gameWindow.width),
      y: Math.round(gameWindow.y + (input.y / streamHeight) * gameWindow.height),
    };
  });

  conn.on('ReceiveInput', (input) => handleInput(input));

  const stop = async () => {
    if (stopped) return;
    stopped = true;
    if (telemetryTimer) clearInterval(telemetryTimer);
    stopInputBridge();
    setInputMapper(null);
    try { await conn.stop(); } catch (_) {}
  };

  // Group membership and the server-side "this connection is the agent for session N" binding are per
  // connection id, so they are lost on every reconnect and must be re-established — otherwise the player's
  // input (sent to the agent group) silently stops arriving after the first network blip.
  const joinAsAgent = () => conn.invoke('AgentJoin', sessionId, serverId, password || null);

  conn.onreconnecting((err) => {
    log.warn('SignalR connection lost — reconnecting', err ? err.message : '');
  });
  conn.onreconnected(async (connectionId) => {
    log.info('SignalR reconnected — re-joining session', { sessionId, connectionId });
    try {
      await joinAsAgent();
    } catch (err) {
      log.error('Re-join after reconnect failed', { error: err.message, sessionId });
    }
  });

  await conn.start();
  log.info('SignalR connected to cloud-stream hub', hubBase(apiUrl));
  try {
    await joinAsAgent();
  } catch (err) {
    log.error('AgentJoin failed', {
      error: err.message,
      sessionId,
      serverId,
      hints: [
        'Session may not be active on this server',
        'serverId in config.json must match the server the player selected',
        'Restart agent after saving config.json',
      ],
    });
    throw err;
  }

  if (gameWindowOnly && gamePid) {
    log.info(`Waiting for game window (pid ${gamePid}) before streaming…`);
    gameWindow = await waitForGameWindow(gamePid, { timeoutMs: waitForGameMs });
    await sleep(800);
    gameWindow = await refreshWindowBounds(gameWindow, gamePid);
    streamWidth = gameWindow.width;
    streamHeight = gameWindow.height;
    log.info(`Streaming game window only`, { size: `${streamWidth}x${streamHeight}`, title: gameWindow.title });
  } else {
    let screenshot;
    try { screenshot = require('screenshot-desktop'); } catch {
      console.warn('  Install screenshot-desktop: npm install screenshot-desktop');
      return null;
    }
    gameWindow = { screenshotDesktop: screenshot };
    log.warn('Full desktop capture fallback — set gamePid for game-window-only stream');
  }

  log.info(`Streaming session ${sessionId}`, 'adaptive quality/FPS enabled');

  telemetryTimer = setInterval(() => {
    if (stopped || telemetryReportPending || conn.state !== signalR.HubConnectionState.Connected) return;
    const now = Date.now();
    const elapsedMs = Math.max(1, now - telemetryWindow.startedAt);
    const report = {
      captureMsAvg: telemetryWindow.captureSamples
        ? telemetryWindow.captureMsTotal / telemetryWindow.captureSamples : 0,
      captureMsMax: telemetryWindow.captureMsMax,
      hubRoundTripMsAvg: telemetryWindow.sendSamples
        ? telemetryWindow.sendMsTotal / telemetryWindow.sendSamples : 0,
      hubRoundTripMsMax: telemetryWindow.sendMsMax,
      frameBytesAvg: telemetryWindow.framesSent
        ? telemetryWindow.frameBytesTotal / telemetryWindow.framesSent : 0,
      frameBytesMax: telemetryWindow.frameBytesMax,
      targetFps: telemetryWindow.framesSent / (elapsedMs / 1000),
      quality: adaptive.quality,
      framesSent: telemetryWindow.framesSent,
      framesSkipped: telemetryWindow.framesSkipped,
      failures: telemetryWindow.failures,
    };
    telemetryWindow = createTelemetryWindow(now);
    telemetryReportPending = true;
    conn.invoke('ReportAgentTelemetry', sessionId, serverId, password || null, report)
      .catch((err) => log.warn('Stream telemetry report failed', err.message || String(err)))
      .finally(() => { telemetryReportPending = false; });
  }, 10_000);

  async function captureFrame() {
    if (gameWindow.screenshotDesktop) {
      return gameWindow.screenshotDesktop({ format: 'jpg', quality: adaptive.quality });
    }
    if (boundsRefreshCounter++ % 45 === 0 && gamePid) {
      gameWindow = await refreshWindowBounds(gameWindow, gamePid);
      streamWidth = gameWindow.width;
      streamHeight = gameWindow.height;
    }
    return captureWindow(gameWindow, adaptive.quality);
  }

  async function captureLoop() {
    while (!stopped) {
      if (conn.state !== signalR.HubConnectionState.Connected) {
        await sleep(250);
        continue;
      }

      if (gamePid && gameWindowOnly && !gameWindow?.screenshotDesktop) {
        try {
          process.kill(gamePid, 0);
        } catch (_) {
          log.info('Game process ended — stopping stream');
          break;
        }
      }

      const tickStart = Date.now();
      try {
        const capStart = Date.now();
        const buf = await captureFrame();
        const captureMs = Date.now() - capStart;
        telemetryWindow.captureSamples += 1;
        telemetryWindow.captureMsTotal += captureMs;
        telemetryWindow.captureMsMax = Math.max(telemetryWindow.captureMsMax, captureMs);

        if (buf.length > 950_000) {
          telemetryWindow.framesSkipped += 1;
          adaptive.noteSuccess(captureMs, 0, buf.length);
          const elapsed = Date.now() - tickStart;
          await sleep(Math.max(0, adaptive.intervalMs - elapsed));
          continue;
        }

        const sendStart = Date.now();
        const frameBase64 = buf.toString('base64');
        await conn.invoke('SendFrame', sessionId, serverId, password || null, frameBase64);
        const sendMs = Date.now() - sendStart;
        telemetryWindow.framesSent += 1;
        telemetryWindow.frameBytesTotal += buf.length;
        telemetryWindow.frameBytesMax = Math.max(telemetryWindow.frameBytesMax, buf.length);
        telemetryWindow.sendSamples += 1;
        telemetryWindow.sendMsTotal += sendMs;
        telemetryWindow.sendMsMax = Math.max(telemetryWindow.sendMsMax, sendMs);

        adaptive.noteSuccess(captureMs, sendMs, buf.length);
      } catch (err) {
        if (!stopped) {
          telemetryWindow.failures += 1;
          const msg = err.message || String(err);
          if (!msg.includes('connection') && !msg.includes('canceled')) {
            adaptive.noteFailure(msg.slice(0, 60));
          }
        }
      }

      const elapsed = Date.now() - tickStart;
      await sleep(Math.max(0, adaptive.intervalMs - elapsed));
    }
  }

  loopPromise = captureLoop();

  // Every few seconds: measure the real round-trip to the hub (Ping is a no-op on the server, so unlike a
  // frame send it excludes password hashing and payload size) and push it with fps/quality to the player.
  let telemetryBusy = false;
  telemetryTimer = setInterval(async () => {
    if (stopped || telemetryBusy || conn.state !== signalR.HubConnectionState.Connected) return;
    telemetryBusy = true;
    try {
      const t0 = Date.now();
      await conn.invoke('Ping');
      const latencyMs = Date.now() - t0;
      await conn.invoke('ReportTelemetry', sessionId, latencyMs, adaptive.targetFps, adaptive.quality);
    } catch (err) {
      if (!stopped) log.debug?.('Telemetry report skipped', err.message);
    } finally {
      telemetryBusy = false;
    }
  }, telemetryIntervalMs);

  conn.onclose((err) => {
    stopped = true;
    if (telemetryTimer) clearInterval(telemetryTimer);
    if (err) log.warn('SignalR stream disconnected', err.message);
    else log.info('SignalR stream closed');
  });

  return {
    stop: async () => {
      await stop();
      try { await loopPromise; } catch (_) {}
    },
  };
}

function createTelemetryWindow(startedAt = Date.now()) {
  return {
    startedAt,
    captureSamples: 0,
    captureMsTotal: 0,
    captureMsMax: 0,
    sendSamples: 0,
    sendMsTotal: 0,
    sendMsMax: 0,
    framesSent: 0,
    frameBytesTotal: 0,
    frameBytesMax: 0,
    framesSkipped: 0,
    failures: 0,
  };
}

module.exports = { startStreaming, AdaptiveStreamControl };
