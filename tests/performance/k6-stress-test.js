import http from 'k6/http';
import { check, group, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '1m', target: 10 },
    { duration: '2m', target: 10 },
    { duration: '1m', target: 25 },
    { duration: '2m', target: 25 },
    { duration: '1m', target: 50 },
    { duration: '1m', target: 100 },
    { duration: '1m', target: 0 },
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5000').replace(/\/$/, '');

export default function () {
  group('liveness', () => {
    const response = http.get(`${BASE_URL}/api/health`);
    check(response, { 'health returns 200': (r) => r.status === 200 });
  });

  group('approved game catalog', () => {
    const response = http.get(`${BASE_URL}/api/games?limit=20`);
    check(response, { 'catalog returns 200': (r) => r.status === 200 });
  });

  group('versioned cached catalog', () => {
    const response = http.get(`${BASE_URL}/api/v1/games?page=1&limit=20`);
    check(response, { 'versioned catalog returns 200': (r) => r.status === 200 });
  });

  sleep(1);
}
