/**
 * Sync PUBLIC_* URLs from nexuscore/.env into client/.env for Vite.
 * With --network, auto-detects LAN IP when PUBLIC_API_URL is not set.
 */
const fs = require('fs');
const path = require('path');
const os = require('os');

const root = path.join(__dirname, '..');
const envPath = path.join(root, '.env');
const clientEnvPath = path.join(root, 'client', '.env');

function parseEnv(filePath) {
  const out = {};
  if (!fs.existsSync(filePath)) return out;
  for (const line of fs.readFileSync(filePath, 'utf8').split('\n')) {
    const t = line.trim();
    if (!t || t.startsWith('#')) continue;
    const i = t.indexOf('=');
    if (i <= 0) continue;
    out[t.slice(0, i).trim()] = t.slice(i + 1).trim();
  }
  return out;
}

function getLanIp() {
  const nets = os.networkInterfaces();
  for (const ifaces of Object.values(nets)) {
    for (const net of ifaces || []) {
      if (net.family !== 'IPv4' || net.internal) continue;
      const addr = net.address;
      if (addr.startsWith('169.254.')) continue;
      if (addr.startsWith('100.')) continue;
      return addr;
    }
  }
  return null;
}

function isTailscaleUrl(url) {
  return /^https?:\/\/100\.\d{1,3}\.\d{1,3}\.\d{1,3}/i.test(url || '');
}

function upsertClientEnv(updates) {
  let lines = [];
  if (fs.existsSync(clientEnvPath)) {
    lines = fs.readFileSync(clientEnvPath, 'utf8').split('\n');
  }
  const keys = new Set(Object.keys(updates));
  const kept = lines.filter((line) => {
    const t = line.trim();
    if (!t || t.startsWith('#')) return true;
    const k = t.split('=')[0]?.trim();
    return !keys.has(k);
  });
  while (kept.length && kept[kept.length - 1] === '') kept.pop();
  for (const [k, v] of Object.entries(updates)) {
    kept.push(`${k}=${v}`);
  }
  kept.push('');
  fs.writeFileSync(clientEnvPath, kept.join('\n'));
}

const networkMode = process.argv.includes('--network') || process.env.NETWORK_MODE === '1';
const httpsMode = process.argv.includes('--https') || process.env.HTTPS_MODE === '1';
const env = parseEnv(envPath);
const port = env.PORT || '5000';
const webPort = env.WEB_PORT || '5173';

let publicApi = env.PUBLIC_API_URL || '';
let publicWeb = env.PUBLIC_WEB_URL || '';

if (networkMode) {
  const ip = getLanIp();
  if (!publicApi && ip && !httpsMode) publicApi = `http://${ip}:${port}/api`;
  if (!publicWeb && ip && !httpsMode) publicWeb = `http://${ip}:${webPort}`;

  if (!httpsMode && ip && (isTailscaleUrl(publicApi) || isTailscaleUrl(publicWeb))) {
    console.log('(network) Stale Tailscale URL detected — switching to LAN IP', ip);
    publicApi = `http://${ip}:${port}/api`;
    publicWeb = `http://${ip}:${webPort}`;
  }

  const shouldWriteEnv = (publicApi || publicWeb) && !httpsMode
    && (!env.PUBLIC_API_URL || isTailscaleUrl(env.PUBLIC_API_URL) || isTailscaleUrl(env.PUBLIC_WEB_URL));

  if (shouldWriteEnv) {
    const envLines = fs.existsSync(envPath) ? fs.readFileSync(envPath, 'utf8').split('\n') : [];
    const filtered = envLines.filter((l) => !l.trim().startsWith('PUBLIC_API_URL=') && !l.trim().startsWith('PUBLIC_WEB_URL=') && !l.trim().startsWith('NETWORK_MODE='));
    while (filtered.length && filtered[filtered.length - 1] === '') filtered.pop();
    filtered.push(`NETWORK_MODE=1`, `API_BIND=0.0.0.0`);
    if (publicApi) filtered.push(`PUBLIC_API_URL=${publicApi}`);
    if (publicWeb) filtered.push(`PUBLIC_WEB_URL=${publicWeb}`);
    filtered.push('');
    fs.writeFileSync(envPath, filtered.join('\n'));
    console.log('(network) Updated nexuscore/.env with PUBLIC_* URLs');
  }
}

const clientUpdates = { VITE_API_URL: '/api' };
if (publicApi) clientUpdates.VITE_PUBLIC_API_URL = publicApi;
if (publicWeb) clientUpdates.VITE_PUBLIC_WEB_URL = publicWeb;
if (networkMode) clientUpdates.VITE_DEV_HOST = '0.0.0.0';
if (httpsMode) clientUpdates.VITE_HTTPS_MODE = '1';

if (Object.keys(clientUpdates).length) {
  upsertClientEnv(clientUpdates);
  console.log('(network) Updated client/.env for remote access');
}

if (networkMode) {
  console.log('');
  if (httpsMode) {
    console.log('  HTTPS mode (Tailscale Serve / reverse proxy):');
  } else {
    console.log('  Network mode — other devices can connect using:');
  }
  if (publicWeb) console.log(`  Website (player):  ${publicWeb}`);
  if (publicApi) console.log(`  API (agent):       ${publicApi}`);
  console.log('');
  if (httpsMode) {
    console.log('  Use start-site-https.bat to configure Tailscale Serve automatically.');
  } else {
    console.log('  Set PUBLIC_API_URL / PUBLIC_WEB_URL in nexuscore/.env for Tailscale or tunnel URLs.');
    console.log('  For HTTPS on Tailscale, use start-site-https.bat instead.');
  }
  console.log('  Allow Windows Firewall inbound on ports', port, 'and', webPort, '(HTTP backend; HTTPS is via Tailscale)');
  console.log('');
}

module.exports = { getLanIp, publicApi, publicWeb, networkMode };
