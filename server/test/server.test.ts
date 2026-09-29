import { env, exports } from 'cloudflare:workers';
import { reset, runDurableObjectAlarm, runInDurableObject } from 'cloudflare:test';
import { afterEach, describe, expect, it } from 'vitest';
import { TEST_AUTH_TOKEN_HEX } from './constants.ts';

const BASE = 'https://clipsync.test';
const MIB = 1024 * 1024;

function b64url(hex: string): string {
  const bytes = Uint8Array.from(hex.match(/../g)!.map((h) => parseInt(h, 16)));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
const TOKEN = b64url(TEST_AUTH_TOKEN_HEX);

let ipCounter = 0;
function api(path: string, init: RequestInit & { token?: string | null } = {}): Promise<Response> {
  const { token = TOKEN, ...rest } = init;
  const headers = new Headers(rest.headers);
  if (token !== null && !headers.has('authorization')) headers.set('authorization', `Bearer ${token}`);
  if (!headers.has('cf-connecting-ip')) headers.set('cf-connecting-ip', `10.0.0.${(ipCounter++ % 250) + 1}`);
  return exports.default.fetch(new Request(BASE + path, { ...rest, headers }));
}

const hex = (n: number, seed = 0) => {
  let s = '';
  for (let i = 0; i < n; i++) s += ((i * 7 + seed * 13 + 1) & 0xff).toString(16).padStart(2, '0');
  return s;
};
let idn = 0;
const newId = () => (++idn).toString(16).padStart(32, '0');
const DEV_A = hex(16, 1);
const DEV_B = hex(16, 2);

function bytes(n: number, fill = 0xab): Uint8Array<ArrayBuffer> {
  return new Uint8Array(n).fill(fill);
}

const putBody = (id: string, data: Uint8Array, dev = DEV_A) =>
  api(`/v1/items/${id}/body`, { method: 'PUT', headers: { 'x-device-id': dev }, body: data });
const commit = (id: string, header = new Uint8Array([1, 2, 3]), dev = DEV_A) =>
  api('/v1/items', {
    method: 'POST',
    headers: { 'x-item-id': id, 'x-device-id': dev, 'x-created-at': '1700000000000' },
    body: header,
  });
async function upload(id: string, size = 100, dev = DEV_A): Promise<number> {
  expect((await putBody(id, bytes(size), dev)).status).toBe(204);
  const r = await commit(id, undefined, dev);
  expect(r.status).toBe(200);
  return ((await r.json()) as { seq: number }).seq;
}
async function sha256(b: ArrayBuffer | Uint8Array): Promise<string> {
  const d = new Uint8Array(await crypto.subtle.digest('SHA-256', b as BufferSource));
  return Array.from(d, (x) => x.toString(16).padStart(2, '0')).join('');
}
const stub = () => env.VAULT.get(env.VAULT.idFromName(env.VAULT_ID));
const r2Keys = async () => (await env.BODIES.list()).objects.map((o) => o.key);

async function connect(dev: string): Promise<{ ws: WebSocket; msgs: any[] }> {
  const res = await api(`/v1/ws?device_id=${dev}`, { headers: { upgrade: 'websocket' } });
  expect(res.status).toBe(101);
  const ws = res.webSocket!;
  const msgs: any[] = [];
  ws.addEventListener('message', (e) => {
    if (e.data !== 'pong') msgs.push(JSON.parse(e.data as string));
  });
  ws.accept();
  return { ws, msgs };
}
const settle = () => new Promise((r) => setTimeout(r, 50));

afterEach(async () => {
  await reset();
});

describe('인증 / allowlist', () => {
  it('토큰이 없거나 틀리거나 형식이 잘못되면 401', async () => {
    expect((await api('/v1/items', { token: null })).status).toBe(401);
    expect((await api('/v1/items', { token: b64url(hex(32, 9)) })).status).toBe(401);
    expect((await api('/v1/items', { token: TOKEN + '=' })).status).toBe(401); // 패딩 거부
    expect((await api('/v1/items', { token: TOKEN.slice(1) })).status).toBe(401);
    expect((await api('/v1/items', { token: TOKEN.replace(/.$/, 'B') })).status).toBe(401); // 비정규 마지막 문자 또는 다른 토큰
    expect((await api('/v1/items')).status).toBe(200);
  });

  it('인증 실패는 DO/R2를 건드리지 않는다', async () => {
    await api(`/v1/items/${newId()}/body`, { method: 'PUT', token: null, body: bytes(10) });
    expect(await r2Keys()).toEqual([]);
  });

  it('IP 기준 401 rate limit이 걸리면 429', async () => {
    const ip = { 'cf-connecting-ip': '203.0.113.7' };
    const codes: number[] = [];
    for (let i = 0; i < 14; i++) codes.push((await api('/v1/items', { token: null, headers: ip })).status);
    expect(codes.slice(0, 5)).toEqual([401, 401, 401, 401, 401]);
    expect(codes).toContain(429);
    // 다른 IP는 영향 없음
    expect((await api('/v1/items', { token: null })).status).toBe(401);
  });
});

describe('업로드 / 커밋 / 조회', () => {
  it('seq는 단조 증가하고 GET /items가 seq 순으로 돌려준다', async () => {
    const ids = [newId(), newId(), newId()];
    const seqs = [];
    for (const id of ids) seqs.push(await upload(id));
    expect(seqs[1]).toBe(seqs[0]! + 1);
    expect(seqs[2]).toBe(seqs[1]! + 1);
    const list = (await (await api('/v1/items?since=0')).json()) as any[];
    expect(list.map((i) => i.id)).toEqual(ids);
    expect(list[0]).toMatchObject({ device_id: DEV_A, purged: false, body_size: 100, header: 'AQID' });
    const after = (await (await api(`/v1/items?since=${seqs[1]}`)).json()) as any[];
    expect(after.map((i) => i.seq)).toEqual([seqs[2]]);
  });

  it('PUT/POST는 idempotent: 재PUT은 덮어쓰고, 재커밋은 같은 seq', async () => {
    const id = newId();
    expect((await putBody(id, bytes(10, 1))).status).toBe(204);
    expect((await putBody(id, bytes(20, 2))).status).toBe(204); // 커밋 전 덮어쓰기
    const s1 = ((await (await commit(id)).json()) as any).seq;
    const s2 = ((await (await commit(id)).json()) as any).seq;
    expect(s2).toBe(s1);
    const body = new Uint8Array(await (await api(`/v1/items/${id}/body`)).arrayBuffer());
    expect(body).toEqual(bytes(20, 2));
  });

  it('본문 없는 커밋은 409, 잘못된 헤더는 400, 커밋 헤더 64KiB 초과는 413', async () => {
    expect((await commit(newId())).status).toBe(409);
    expect((await api('/v1/items', { method: 'POST', headers: { 'x-item-id': 'zz' }, body: bytes(3) })).status).toBe(400);
    const id = newId();
    await putBody(id, bytes(10));
    expect((await commit(id, bytes(64 * 1024 + 1))).status).toBe(413);
    expect((await commit(id, bytes(64 * 1024))).status).toBe(200);
  });

  it('Content-Length가 없으면 411', async () => {
    const stream = new ReadableStream({
      start(c) {
        c.enqueue(bytes(5));
        c.close();
      },
    });
    const res = await api(`/v1/items/${newId()}/body`, {
      method: 'PUT',
      headers: { 'x-device-id': DEV_A },
      body: stream,
      // @ts-expect-error workerd 옵션
      duplex: 'half',
    });
    expect(res.status).toBe(411);
  });

  it('커밋되거나 purged된 id의 재PUT은 409', async () => {
    const id = newId();
    await upload(id);
    expect((await putBody(id, bytes(5))).status).toBe(409);
    expect((await api(`/v1/items/${id}/body`, { method: 'DELETE' })).status).toBe(204);
    expect((await putBody(id, bytes(5))).status).toBe(409); // 삭제된 본문을 되살리지 못함
    expect((await api(`/v1/items/${id}/body`)).status).toBe(410);
  });

  it('51 MiB 초과는 본문을 읽기 전에 413', async () => {
    const big = new Request(BASE + `/v1/items/${newId()}/body`, {
      method: 'PUT',
      headers: { authorization: `Bearer ${TOKEN}`, 'x-device-id': DEV_A, 'content-length': String(51 * MIB + 1) },
      body: bytes(8),
    });
    const res = await exports.default.fetch(big);
    expect(res.status).toBe(413);
  });
});

describe('DO / R2 경계와 스트리밍', () => {
  it('정확히 1.25 MiB는 DO, 1 바이트 초과는 R2', async () => {
    const a = newId();
    const b = newId();
    await upload(a, 1.25 * MIB);
    await upload(b, 1.25 * MIB + 1);
    const storage = await runInDurableObject(stub(), (_i, state) =>
      Object.fromEntries(state.storage.sql.exec('SELECT hex(id) AS id, storage FROM bodies').toArray().map((r: any) => [r.id.toLowerCase(), r.storage])),
    );
    expect(storage[a]).toBe('do');
    expect(storage[b]).toBe('r2');
    expect((await r2Keys()).length).toBe(1);
    expect((await r2Keys())[0]).toMatch(new RegExp(`^${env.VAULT_ID}/${b}/[0-9a-f]{32}$`));
  });

  it('20 MiB 본문이 R2를 거쳐 왕복되고, DELETE 후 R2 객체가 지워지며 GET=410', async () => {
    const id = newId();
    const data = new Uint8Array(20 * MIB);
    for (let i = 0; i < data.length; i += 4096) data[i] = i & 0xff;
    expect((await putBody(id, data)).status).toBe(204);
    expect((await commit(id)).status).toBe(200);
    const got = await api(`/v1/items/${id}/body`);
    expect(got.status).toBe(200);
    expect(got.headers.get('content-length')).toBe(String(20 * MIB));
    const back = await got.arrayBuffer();
    expect(back.byteLength).toBe(data.length);
    expect(await sha256(back)).toBe(await sha256(data)); // 20 MiB 배열을 toEqual로 비교하면 러너 힙이 터진다
    expect((await api(`/v1/items/${id}/body`, { method: 'DELETE' })).status).toBe(204);
    expect(await r2Keys()).toEqual([]);
    expect((await api(`/v1/items/${id}/body`)).status).toBe(410);
    expect((await api(`/v1/items/${id}/body`, { method: 'DELETE' })).status).toBe(204); // idempotent
  });

  it('R2 재PUT은 이전 객체를 지우고 하나만 남긴다', async () => {
    const id = newId();
    await putBody(id, bytes(1.25 * MIB + 10, 1));
    await putBody(id, bytes(1.25 * MIB + 20, 2));
    expect((await r2Keys()).length).toBe(1);
    await commit(id);
    const back = new Uint8Array(await (await api(`/v1/items/${id}/body`)).arrayBuffer());
    expect(back.length).toBe(1.25 * MIB + 20);
  });
});

describe('20개 cap / 만료 / 고아', () => {
  it('21번째 커밋이 가장 오래된 항목과 R2 객체를 지운다 (purged도 cap에 포함)', async () => {
    const first = newId();
    await upload(first, 1.25 * MIB + 1); // R2
    const seqs: number[] = [];
    for (let i = 0; i < 19; i++) seqs.push(await upload(newId()));
    expect((await r2Keys()).length).toBe(1);
    const last = await upload(newId());
    expect(last).toBe(21);
    const list = (await (await api('/v1/items?since=0')).json()) as any[];
    expect(list.length).toBe(20);
    expect(list[0].seq).toBe(2);
    expect((await api(`/v1/items/${first}/body`)).status).toBe(404);
    expect(await r2Keys()).toEqual([]);
  });

  it('24h 만료: alarm이 행과 본문(R2 포함)을 지운다', async () => {
    const small = newId();
    const large = newId();
    await upload(small);
    await upload(large, 1.25 * MIB + 1);
    await runInDurableObject(stub(), (_i, state) => {
      state.storage.sql.exec('UPDATE items SET expires_at = ?', Date.now() - 1);
    });
    // 만료 직후에는 alarm 전이라도 읽기에서 제외된다
    expect(((await (await api('/v1/items?since=0')).json()) as any[]).length).toBe(0);
    expect((await api(`/v1/items/${small}/body`)).status).toBe(404);
    expect(await runDurableObjectAlarm(stub())).toBe(true);
    expect(await r2Keys()).toEqual([]);
    const counts = await runInDurableObject(stub(), (_i, state) => ({
      items: state.storage.sql.exec('SELECT COUNT(*) AS n FROM items').one().n,
      bodies: state.storage.sql.exec('SELECT COUNT(*) AS n FROM bodies').one().n,
    }));
    expect(counts).toEqual({ items: 0, bodies: 0 });
  });

  it('커밋 없는 PUT은 10분 뒤 alarm이 지운다 (R2 포함)', async () => {
    const s = newId();
    const l = newId();
    await putBody(s, bytes(10));
    await putBody(l, bytes(1.25 * MIB + 1));
    expect((await r2Keys()).length).toBe(1);
    await runInDurableObject(stub(), (_i, state) => {
      state.storage.sql.exec('UPDATE bodies SET created_at = ?', Date.now() - 11 * 60 * 1000);
    });
    expect(await runDurableObjectAlarm(stub())).toBe(true);
    expect(await r2Keys()).toEqual([]);
    expect((await commit(s)).status).toBe(409);
  });

  it('alarm이 아직 이르면 커밋 안 된 본문을 지우지 않는다', async () => {
    const s = newId();
    await putBody(s, bytes(10));
    await runDurableObjectAlarm(stub());
    expect((await commit(s)).status).toBe(200);
  });

  it('hello.seq는 항목이 지워져도 줄지 않는다 (D-36)', async () => {
    await upload(newId());
    await upload(newId());
    await runInDurableObject(stub(), (_i, state) => {
      state.storage.sql.exec('UPDATE items SET expires_at = ?', Date.now() - 1);
    });
    await runDurableObjectAlarm(stub());
    const { ws, msgs } = await connect(DEV_A);
    await settle();
    expect(msgs[0]).toMatchObject({ t: 'hello', seq: 2, config: null });
    ws.close();
  });
});

describe('config', () => {
  it('없으면 404, If-Match 낙관적 동시성, 충돌은 412', async () => {
    expect((await api('/v1/config')).status).toBe(404);
    const put = (v: number, blob = bytes(8)) => api('/v1/config', { method: 'PUT', headers: { 'if-match': String(v) }, body: blob });
    expect(await (await put(0)).json()).toEqual({ version: 1 });
    const stale = await put(0);
    expect(stale.status).toBe(412);
    expect(await stale.json()).toEqual({ version: 1 });
    expect(await (await put(1, bytes(4, 7))).json()).toEqual({ version: 2 });
    const cur = (await (await api('/v1/config')).json()) as any;
    expect(cur).toEqual({ version: 2, blob: btoa(String.fromCharCode(...bytes(4, 7))) });
    expect((await api('/v1/config', { method: 'PUT', body: bytes(1) })).status).toBe(400); // If-Match 필요
    expect((await put(2, bytes(64 * 1024 + 1))).status).toBe(413);
  });
});

describe('WebSocket', () => {
  it('hello → item 이벤트(업로더 제외, 소형은 inline) → body_purged, config 이벤트', async () => {
    const a = await connect(DEV_A);
    const b = await connect(DEV_B);
    const id = newId();
    await putBody(id, bytes(5, 9), DEV_A);
    await commit(id, new Uint8Array([1, 2, 3]), DEV_A);
    await settle();
    expect(a.msgs.filter((m) => m.t === 'item')).toEqual([]); // 업로더 소켓엔 보내지 않는다
    const item = b.msgs.find((m) => m.t === 'item');
    expect(item).toMatchObject({
      t: 'item',
      seq: 1,
      id,
      device_id: DEV_A,
      created_at: 1700000000000,
      header: 'AQID',
      body_size: 5,
      inline_body: btoa(String.fromCharCode(...bytes(5, 9))),
    });

    await api(`/v1/items/${id}/body`, { method: 'DELETE' });
    await api('/v1/config', { method: 'PUT', headers: { 'if-match': '0' }, body: bytes(4) });
    await settle();
    expect(a.msgs.find((m) => m.t === 'body_purged')).toEqual({ t: 'body_purged', id });
    expect(b.msgs.find((m) => m.t === 'body_purged')).toEqual({ t: 'body_purged', id });
    expect(b.msgs.find((m) => m.t === 'config')).toMatchObject({ t: 'config', version: 1 });
    a.ws.close();
    b.ws.close();
  });

  it('32KB 초과 본문과 R2 본문은 inline_body가 null', async () => {
    const b = await connect(DEV_B);
    await upload(newId(), 32 * 1024 + 1, DEV_A);
    await settle();
    expect(b.msgs.find((m) => m.t === 'item').inline_body).toBeNull();
    b.ws.close();
  });

  it('ping은 auto-response로 pong', async () => {
    const res = await api(`/v1/ws?device_id=${DEV_A}`, { headers: { upgrade: 'websocket' } });
    const ws = res.webSocket!;
    const got = new Promise<string>((resolve) => ws.addEventListener('message', (e) => e.data === 'pong' && resolve('pong')));
    ws.accept();
    ws.send('ping');
    expect(await got).toBe('pong');
    ws.close();
  });

  it('Upgrade 헤더가 없거나 device_id가 잘못되면 거부', async () => {
    expect((await api(`/v1/ws?device_id=${DEV_A}`)).status).toBe(426);
    expect((await api('/v1/ws?device_id=xyz', { headers: { upgrade: 'websocket' } })).status).toBe(400);
  });
});
