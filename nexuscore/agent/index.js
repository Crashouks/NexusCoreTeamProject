#!/usr/bin/env node
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, exec } = require('child_process');
const { startStreaming } = require('./stream');
const log = require('./logger');

const configPath = path.join(__dirname, 'config.json');
if (!fs.existsSync(configPath)) {
  log.error('Missing config.json — copy from Admin → Cloud → Download config.json');
  process.exit(1);
}

const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const configuredApiUrl = (config.apiUrl || 'http://localhost:5000/api').replace(/\/$/, '');
let apiUrl = configuredApiUrl;
const serverId = config.serverId;
const password = config.password || '';
const pollMs = config.pollIntervalMs || 3000;
const apiProbeTimeoutMs = config.apiProbeTimeoutMs || 3000;

if (!serverId) {
  log.error('config.json must include serverId (Admin → Cloud → your machine)');
  process.exit(1);
}

const runningBySession = new Map();
const streamConns = new Map();
const endingSessions = new Set();

function localApiFallbackUrls(configuredUrl) {
  const port = configuredUrl.match(/:(\d+)/)?.[1] || '5000';
  const urls = [`http://localhost:${port}/api`, `http://127.0.0.1:${port}/api`];
  try {
    for (const ifaces of Object.values(os.networkInterfaces())) {
      for (const net of ifaces || []) {
        if (net.family === 'IPv4' && !net.internal) {
          urls.push(`http://${net.address}:${port}/api`);
        }
      }
    }
  } catch (_) {}
  return [...new Set(urls)].filter((url) => url !== configuredUrl);
}

async function probeApiUrl(url) {
  try {
    const res = await fetch(`${url}/cloud/agent/heartbeat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ server_id: serverId, password: password || null }),
      signal: AbortSignal.timeout(apiProbeTimeoutMs),
    });
    return res.ok || res.status === 401;
  } catch {
    return false;
  }
}

async function resolveApiUrl() {
  const candidates = [configuredApiUrl, ...localApiFallbackUrls(configuredApiUrl)];
  const results = await Promise.all(
    candidates.map(async (url) => ({ url, ok: await probeApiUrl(url) }))
  );
  const match = results.find((r) => r.ok);
  if (match) {
    if (match.url !== configuredApiUrl) {
      log.warn('Configured apiUrl unreachable — using fallback', {
        configuredApiUrl,
        apiUrl: match.url,
        hint: 'Re-download config.json from Admin → Cloud after restarting start-site-network.bat',
      });
    }
    return match.url;
  }

  log.error('No reachable API URL found', {
    configuredApiUrl,
    tried: candidates,
    hint: 'Run start-site-network.bat on the host PC first',
  });
  return configuredApiUrl;
}

async function apiPost(route, body) {
  const url = `${apiUrl}${route}`;
  let res;
  try {
    res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ server_id: serverId, password: password || null, ...body }),
      signal: AbortSignal.timeout(apiProbeTimeoutMs),
    });
  } catch (err) {
    const hint = err.name === 'TimeoutError' || err.name === 'AbortError'
      ? `API did not respond within ${apiProbeTimeoutMs}ms`
      : 'Is start-site-network.bat running? Is apiUrl correct?';
    log.error(`Network error POST ${route}`, { url, error: err.message, hint });
    throw err;
  }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    log.error(`API ${res.status} POST ${route}`, { error: data.error, code: data.code, serverId });
    throw new Error(data.error || res.statusText || `HTTP ${res.status}`);
  }
  return data;
}

async function heartbeat() {
  await apiPost('/cloud/agent/heartbeat', {});
}

async function setJobStatus(jobId, status, error) {
  await apiPost(`/cloud/agent/jobs/${jobId}/status`, { status, error: error || null });
}

async function notifySessionEnded(sessionId) {
  if (endingSessions.has(sessionId)) return;
  endingSessions.add(sessionId);
  try {
    await apiPost('/cloud/agent/session-ended', { session_id: sessionId });
    log.info(`Session ${sessionId} ended — game closed on host`);
  } catch (err) {
    const msg = err.message || String(err);
    if (msg.includes('already ended') || msg.includes('not found')) {
      log.info(`Session ${sessionId} already ended on server`);
    } else {
      log.error(`Could not end session ${sessionId}`, msg);
    }
  } finally {
    endingSessions.delete(sessionId);
  }
}

function stopStream(sessionId) {
  const stream = streamConns.get(sessionId);
  if (stream) {
    stream.stop().catch(() => {});
    streamConns.delete(sessionId);
  }
}

function launchGame(job) {
  const exe = job.executable_path;
  if (!exe || !fs.existsSync(exe)) throw new Error(`Executable not found: ${exe}`);
  const cwd = path.dirname(exe);
  const sessionId = job.session_id;
  const child = spawn(exe, [], { cwd, detached: false, stdio: 'ignore' });
  runningBySession.set(sessionId, child);
  child.on('exit', (code) => {
    runningBySession.delete(sessionId);
    log.info(`Game exited`, { sessionId, code });
    stopStream(sessionId);
    notifySessionEnded(sessionId).catch(() => {});
  });
  return child;
}

function stopGame(sessionId) {
  stopStream(sessionId);
  const proc = runningBySession.get(sessionId);
  if (!proc) return false;
  try {
    if (process.platform === 'win32') exec(`taskkill /PID ${proc.pid} /T /F`);
    else proc.kill('SIGTERM');
  } catch (_) {}
  runningBySession.delete(sessionId);
  return true;
}

async function handleJob(job) {
  const type = job.job_type;
  log.info(`Job ${job.job_id}: ${type}`, { session: job.session_id, game: job.game_id });

  if (type === 'launch') {
    await setJobStatus(job.job_id, 'running');
    try {
      const child = launchGame(job);
      log.info('Game launched', job.executable_path);
      const stream = await startStreaming({
        apiUrl, serverId, password, sessionId: job.session_id,
        gamePid: child.pid,
        gameWindowOnly: config.streamGameWindowOnly !== false,
        waitForGameMs: config.streamWaitForGameMs ?? 90000,
        streamOptions: {
          minFps: config.streamMinFps,
          maxFps: config.streamMaxFps,
          minQuality: config.streamMinQuality,
          maxQuality: config.streamMaxQuality,
          startQuality: config.streamStartQuality,
          startIntervalMs: config.streamStartIntervalMs,
        },
      });
      if (stream) streamConns.set(job.session_id, stream);
      await setJobStatus(job.job_id, 'done');
    } catch (err) {
      log.error('Launch failed', err.message);
      await setJobStatus(job.job_id, 'failed', err.message);
    }
    return;
  }

  if (type === 'stop') {
    stopGame(job.session_id);
    log.info(`Stop requested for session ${job.session_id}`);
    await setJobStatus(job.job_id, 'done');
  }
}

async function poll() {
  try {
    await heartbeat();
    const { jobs } = await apiPost('/cloud/agent/jobs/poll', {});
    for (const job of jobs || []) await handleJob(job);
  } catch (err) {
    log.error('[poll]', err.message);
  }
}

async function startupCheck() {
  log.info('NexusCore Cloud Agent starting', { apiUrl, configuredApiUrl, serverId, passwordSet: !!password, logFile: log.logFile });
  try {
    await heartbeat();
    log.info('Startup check OK — connected to API and authenticated', { serverId });
  } catch (err) {
    log.error('Startup check FAILED', {
      error: err.message,
      configuredApiUrl,
      activeApiUrl: apiUrl,
      hints: [
        'Run start-site-network.bat first (API on http://localhost:5000)',
        'Agent on the same PC as the API? Set apiUrl to http://localhost:5000/api',
        'Remote agent? Ensure PUBLIC_API_URL in nexuscore/.env is reachable from the gaming PC',
        'Check config.json serverId matches Admin → Cloud → Agent server ID',
        'Check agent password matches Admin → Cloud → Agent password',
      ],
    });
  }
}

log.info('Waiting for cloud play jobs…');
resolveApiUrl().then((url) => {
  apiUrl = url;
  return startupCheck();
}).then(() => {
  poll();
  setInterval(poll, pollMs);
});
