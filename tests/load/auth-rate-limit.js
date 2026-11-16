// k6: verificerer gatewayens rate limit på /api/auth (10 pr. minut pr. IP).
// Kør: k6 run tests/load/auth-rate-limit.js   (eller via Docker, se README)
import http from 'k6/http';
import { Counter } from 'k6/metrics';

const limited = new Counter('rate_limited');
const passed = new Counter('passed_through');
const BASE = __ENV.GATEWAY || 'http://localhost:8080';

export const options = {
  scenarios: {
    burst: { executor: 'per-vu-iterations', vus: 1, iterations: 12, maxDuration: '30s' },
  },
  thresholds: {
    passed_through: ['count == 10'],
    rate_limited: ['count == 2'],
  },
};

export default function () {
  const res = http.post(`${BASE}/api/auth/login`,
    JSON.stringify({ username: 'nobody', password: 'wrong-password' }),
    { headers: { 'Content-Type': 'application/json' } });

  if (res.status === 429) limited.add(1);
  else passed.add(1); // 401 = nåede igennem gatewayen til AuthService
}
