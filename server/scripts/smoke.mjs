// 사용법: BASE_URL=http://localhost:8787 AUTH_TOKEN_HEX=<64 hex> node scripts/smoke.mjs [--big]
// 20 MiB 업로드 → 커밋 → 다운로드(해시 비교) → DELETE → GET=410. --big이면 51 MiB+1 바이트가 413인지도 확인.
import { createHash, randomBytes } from 'node:crypto';

const base = process.env.BASE_URL;
const tokenHex = process.env.AUTH_TOKEN_HEX;
if (!base || !/^[0-9a-f]{64}$/.test(tokenHex ?? '')) {
  console.error('BASE_URL and AUTH_TOKEN_HEX (64 hex) required');
  process.exit(2);
}
const token = Buffer.from(tokenHex, 'hex').toString('base64url');
const id = randomBytes(16).toString('hex');
const dev = randomBytes(16).toString('hex');
const auth = { authorization: `Bearer ${token}` };
const sha = (b) => createHash('sha256').update(b).digest('hex');
let failed = false;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`);
  if (!ok) failed = true;
};
const t0 = Date.now();
const lap = () => `${((Date.now() - t0) / 1000).toFixed(1)}s`;

const data = randomBytes(20 * 1024 * 1024);
let r = await fetch(`${base}/v1/items/${id}/body`, { method: 'PUT', headers: { ...auth, 'x-device-id': dev }, body: data });
check('PUT 20 MiB body', r.status === 204, `status=${r.status} ${lap()}`);
r = await fetch(`${base}/v1/items`, {
  method: 'POST',
  headers: { ...auth, 'x-item-id': id, 'x-device-id': dev, 'x-created-at': String(Date.now()) },
  body: Buffer.from([1, 2, 3]),
});
const seq = (await r.json()).seq;
check('POST commit', r.status === 200 && Number.isInteger(seq), `seq=${seq}`);
r = await fetch(`${base}/v1/items/${id}/body`, { headers: auth });
const back = Buffer.from(await r.arrayBuffer());
check('GET body matches', r.status === 200 && sha(back) === sha(data), `${back.length} bytes ${lap()}`);
r = await fetch(`${base}/v1/items/${id}/body`, { method: 'DELETE', headers: auth });
check('DELETE body', r.status === 204, `status=${r.status}`);
r = await fetch(`${base}/v1/items/${id}/body`, { headers: auth });
check('GET after delete = 410', r.status === 410, `status=${r.status}`);
r = await fetch(`${base}/v1/items/${id}/body`, { method: 'PUT', headers: { ...auth, 'x-device-id': dev }, body: Buffer.alloc(10) });
check('re-PUT after delete = 409', r.status === 409, `status=${r.status}`);
r = await fetch(`${base}/v1/items?since=0`, { headers: { authorization: 'Bearer x' } });
check('bad token = 401', r.status === 401, `status=${r.status}`);

if (process.argv.includes('--big')) {
  const id2 = randomBytes(16).toString('hex');
  try {
    r = await fetch(`${base}/v1/items/${id2}/body`, {
      method: 'PUT',
      headers: { ...auth, 'x-device-id': dev },
      body: Buffer.alloc(51 * 1024 * 1024 + 1),
    });
    check('PUT 51 MiB + 1 = 413', r.status === 413, `status=${r.status}`);
  } catch (e) {
    check('PUT 51 MiB + 1 rejected', false, `network error: ${e.cause?.code ?? e.message}`);
  }
}
process.exit(failed ? 1 : 0);
