import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '2m', target: 200 },  // Ramp-up to 200 VUs
    { duration: '5m', target: 500 },  // Sustained load at 500 VUs
    { duration: '3m', target: 1000 }, // Peak stress at 1000 VUs
    { duration: '2m', target: 0 },    // Ramp-down to 0 VUs
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],   // Error rate should be under 1%
    http_req_duration: ['p(95)<200'], // 95% of requests must complete in under 200ms
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const API_KEY = __ENV.API_KEY || 'agent_sec_live_9f83a2b7c4e1';

export default function () {
  const params = {
    headers: {
      'Content-Type': 'application/json',
      'X-NexusAgent-API-Key': API_KEY,
    },
  };

  // 1. Fetch Session Config
  const configRes = http.get(`${BASE_URL}/api/v1/agent/session/sess_k6_test/config`, params);
  check(configRes, {
    'config status is 200': (r) => r.status === 200,
  });

  // 2. Report Agent Metrics Status
  const statusPayload = JSON.stringify({
    agentId: `agent_k6_${__VU}`,
    sessionId: 'sess_k6_test',
    status: 'Ready',
    metrics: {
      cpuUsagePercent: Math.random() * 50,
      gpuUsagePercent: Math.random() * 70,
      ramUsageMb: 4096,
      availableRamMb: 12288,
      currentFps: 60.0,
      rttMs: 15.4,
      packetLossPercent: 0.01,
    },
  });

  const statusRes = http.post(`${BASE_URL}/api/v1/agent/status`, statusPayload, params);
  check(statusRes, {
    'status report status is 200': (r) => r.status === 200,
