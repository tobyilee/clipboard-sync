import type { Env } from './env.ts';
import { BLOB_MAX, DO_BODY_MAX, HARD_CAP, ctEqual, decodeToken, fromHex, json, text, toHex } from './util.ts';

export { Vault } from './vault.ts';

/** Bearer 토큰 → SHA-256이 VAULT_ID와 (상수시간으로) 일치하는지. */
async function authenticate(request: Request, env: Env): Promise<boolean> {
  const m = /^Bearer (\S+)$/.exec(request.headers.get('authorization') ?? '');
  const token = m ? decodeToken(m[1]!) : null;
  const vault = fromHex(env.VAULT_ID, 32);
  if (!token || !vault) return false;
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', token));
  return ctEqual(digest, vault);
}

/** Content-Length를 엄격히 파싱. 없거나 잘못되면 null. */
function contentLength(request: Request): number | null {
  const v = request.headers.get('content-length');
  return v !== null && /^\d{1,15}$/.test(v) ? Number(v) : null;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    if (!(await authenticate(request, env))) {
      if (!fromHex(env.VAULT_ID, 32)) return text(500, 'server not configured');
      const ip = request.headers.get('cf-connecting-ip') ?? 'unknown';
      const { success } = await env.AUTH_LIMITER.limit({ key: ip });
      return text(success ? 401 : 429, success ? 'unauthorized' : 'too many requests');
    }

    const url = new URL(request.url);
    const stub = env.VAULT.get(env.VAULT.idFromName(env.VAULT_ID));
    const path = url.pathname;
    const method = request.method;

    if (path === '/v1/ws') {
      if (method !== 'GET') return text(405, 'method not allowed');
      if (request.headers.get('upgrade')?.toLowerCase() !== 'websocket') return text(426, 'upgrade required');
      if (!fromHex(url.searchParams.get('device_id'), 16)) return text(400, 'bad device_id');
      return stub.fetch(request);
    }

    if (path === '/v1/items') {
      if (method === 'GET') {
        const raw = url.searchParams.get('since') ?? '0';
        if (!/^\d{1,15}$/.test(raw)) return text(400, 'bad since');
        return json(await stub.list(Number(raw)));
      }
      if (method === 'POST') return commit(request, stub);
      return text(405, 'method not allowed');
    }

    const bodyRoute = /^\/v1\/items\/([0-9a-f]{32})\/body$/.exec(path);
    if (bodyRoute) {
      const idHex = bodyRoute[1]!;
      if (method === 'PUT') return putBody(request, env, stub, idHex);
      if (method === 'GET') return getBody(env, stub, idHex);
      if (method === 'DELETE') {
        return (await stub.deleteBody(idHex)) === 'ok' ? new Response(null, { status: 204 }) : text(404, 'not found');
      }
      return text(405, 'method not allowed');
    }

    if (path === '/v1/config') {
      if (method === 'GET') {
        const c = await stub.getConfig();
        return c ? json(c) : text(404, 'no config');
      }
      if (method === 'PUT') return putConfig(request, stub);
      return text(405, 'method not allowed');
    }

    return text(404, 'not found');
  },
} satisfies ExportedHandler<Env>;

type VaultStub = DurableObjectStub<import('./vault.ts').Vault>;

async function putBody(request: Request, env: Env, stub: VaultStub, idHex: string): Promise<Response> {
  if (!fromHex(request.headers.get('x-device-id'), 16)) return text(400, 'bad X-Device-Id');
  const len = contentLength(request);
  if (len === null) return text(411, 'content-length required');
  if (len > HARD_CAP) return text(413, 'too large');
  if (!request.body) return text(400, 'body required');

  if (len <= DO_BODY_MAX) {
    const data = await request.arrayBuffer();
    if (data.byteLength !== len) return text(400, 'length mismatch');
    return (await stub.putSmall(idHex, data)) === 'ok' ? new Response(null, { status: 204 }) : text(409, 'conflict');
  }

  // 대용량: 스트리밍 전에 409를 사전 검사하고, 끝난 뒤 DO가 다시 검사한다 (D-34).
  if ((await stub.beginPut(idHex)) === 'conflict') return text(409, 'conflict');
  const key = `${env.VAULT_ID}/${idHex}/${toHex(crypto.getRandomValues(new Uint8Array(16)))}`;
  try {
    const { readable, writable } = new FixedLengthStream(len);
    const pipe = request.body.pipeTo(writable);
    const [obj] = await Promise.all([env.BODIES.put(key, readable), pipe]);
    if (!obj || obj.size !== len) throw new Error('length mismatch');
  } catch {
    await stub.discardObject(key);
    return text(400, 'upload failed');
  }
  return (await stub.finishLarge(idHex, key, len)) === 'ok' ? new Response(null, { status: 204 }) : text(409, 'conflict');
}

async function commit(request: Request, stub: VaultStub): Promise<Response> {
  const id = request.headers.get('x-item-id');
  const dev = request.headers.get('x-device-id');
  const created = request.headers.get('x-created-at');
  if (!fromHex(id, 16) || !fromHex(dev, 16) || !created || !/^\d{1,16}$/.test(created)) {
    return text(400, 'bad headers');
  }
  const len = contentLength(request);
  if (len === null) return text(411, 'content-length required');
  if (len > BLOB_MAX) return text(413, 'header too large');
  const header = await request.arrayBuffer();
  if (header.byteLength !== len) return text(400, 'length mismatch');
  const r = await stub.commit(id!, dev!, Number(created), header);
  return r === 'nobody' ? text(409, 'body not uploaded') : json({ seq: r.seq });
}

async function putConfig(request: Request, stub: VaultStub): Promise<Response> {
  const m = request.headers.get('if-match');
  if (!m || !/^\d{1,15}$/.test(m)) return text(400, 'bad If-Match');
  const len = contentLength(request);
  if (len === null) return text(411, 'content-length required');
  if (len === 0 || len > BLOB_MAX) return text(413, 'bad blob size');
  const blob = await request.arrayBuffer();
  if (blob.byteLength !== len) return text(400, 'length mismatch');
  const r = await stub.putConfig(Number(m), blob);
  return r.ok ? json({ version: r.version }) : json({ version: r.version }, 412);
}

async function getBody(env: Env, stub: VaultStub, idHex: string): Promise<Response> {
  const meta = await stub.getBodyMeta(idHex);
  if (meta.status === 'notfound') return text(404, 'not found');
  if (meta.status === 'purged') return text(410, 'purged');
  if (meta.status !== 'ok') return text(500, 'unreachable');
  const headers = { 'content-type': 'application/octet-stream', 'content-length': String(meta.size) };
  if (meta.storage === 'do') return new Response(meta.data, { headers });
  const obj = await env.BODIES.get(meta.r2Key);
  return obj ? new Response(obj.body, { headers }) : text(404, 'not found');
}
