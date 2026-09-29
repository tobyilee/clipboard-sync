// CLI 테스트 피어의 네트워킹: 서버 API(spec 5장)를 호출하는 최소 클라이언트. 의존성 없음(Node 24 내장 fetch/WebSocket).
import { randomBytes } from 'node:crypto';
import {
  decodeBundle, deriveKeys, encodeBundle, itemAad, open, previewOf, seal, uuidHex,
  type BundleEntry, type Keys,
} from './protocol.ts';

export interface ServerItem {
  seq: number; id: string; device_id: string; created_at: number;
  header: string; body_size: number; purged: boolean;
}
export interface Received { seq: number; id: string; deviceId: string; header: any; entries: BundleEntry[] | null }

const hexToBuf = (h: string) => Buffer.from(h, 'hex');

export class Peer {
  readonly baseUrl: string;
  readonly keys: Keys;
  readonly deviceId: string;
  private readonly auth: Record<string, string>;

  constructor(baseUrl: string, passphrase: string, deviceId = randomBytes(16).toString('hex')) {
    this.baseUrl = baseUrl;
    this.keys = deriveKeys(passphrase);
    this.deviceId = deviceId;
    this.auth = { authorization: `Bearer ${this.keys.authToken.toString('base64url')}` };
  }

  private req(path: string, init: RequestInit = {}): Promise<Response> {
    return fetch(this.baseUrl + path, { ...init, headers: { ...this.auth, ...(init.headers as object) } });
  }

  /** 2단계 업로드(PUT body → POST commit). 반환: seq. */
  async send(entries: BundleEntry[], kinds: string[], previewText?: string): Promise<{ id: string; seq: number }> {
    const id = randomBytes(16);
    const createdAt = BigInt(Date.now());
    const body = encodeBundle(entries);
    const header = { v: 1, kinds, ...(previewText !== undefined ? { preview: previewOf(previewText) } : {}), body_plain_size: body.length };
    const sealedBody = seal(this.keys.encKey, itemAad(id, hexToBuf(this.deviceId), createdAt, 2), body);
    const sealedHeader = seal(this.keys.encKey, itemAad(id, hexToBuf(this.deviceId), createdAt, 1), Buffer.from(JSON.stringify(header)));
    const idHex = uuidHex(id);

    let r = await this.req(`/v1/items/${idHex}/body`, { method: 'PUT', headers: { 'x-device-id': this.deviceId }, body: sealedBody });
    if (r.status !== 204) throw new Error(`PUT body: ${r.status} ${await r.text()}`);
    r = await this.req('/v1/items', {
      method: 'POST',
      headers: { 'x-item-id': idHex, 'x-device-id': this.deviceId, 'x-created-at': String(createdAt) },
      body: sealedHeader,
    });
    if (r.status !== 200) throw new Error(`POST commit: ${r.status} ${await r.text()}`);
    return { id: idHex, seq: ((await r.json()) as { seq: number }).seq };
  }

  sendText(text: string) {
    return this.send([{ type: 1, name: '', data: Buffer.from(text, 'utf8') }], ['text'], text);
  }

  async list(since = 0): Promise<ServerItem[]> {
    const r = await this.req(`/v1/items?since=${since}`);
    if (r.status !== 200) throw new Error(`list: ${r.status}`);
    return (await r.json()) as ServerItem[];
  }

  /** header 복호화 후 필요하면 본문도 받아 복호화. 본문이 purged면 entries=null. */
  async receive(item: Pick<ServerItem, 'seq' | 'id' | 'device_id' | 'created_at' | 'header'>, inlineBody?: string | null, withBody = true): Promise<Received> {
    const id = hexToBuf(item.id), dev = hexToBuf(item.device_id), at = BigInt(item.created_at);
    const header = JSON.parse(open(this.keys.encKey, itemAad(id, dev, at, 1), Buffer.from(item.header, 'base64')).toString('utf8'));
    let entries: BundleEntry[] | null = null;
    if (withBody) {
      let sealed: Buffer | null = inlineBody ? Buffer.from(inlineBody, 'base64') : null;
      if (!sealed) {
        const r = await this.req(`/v1/items/${item.id}/body`);
        if (r.status === 410 || r.status === 404) sealed = null;
        else if (r.status !== 200) throw new Error(`GET body: ${r.status}`);
        else sealed = Buffer.from(await r.arrayBuffer());
      }
      if (sealed) entries = decodeBundle(open(this.keys.encKey, itemAad(id, dev, at, 2), sealed));
    }
    return { seq: item.seq, id: item.id, deviceId: item.device_id, header, entries };
  }

  /** 수신 후 삭제 (D-16). */
  async deleteBody(id: string): Promise<void> {
    const r = await this.req(`/v1/items/${id}/body`, { method: 'DELETE' });
    if (r.status !== 204) throw new Error(`DELETE body: ${r.status}`);
  }

  async getConfig(): Promise<{ version: number; blob: string } | null> {
    const r = await this.req('/v1/config');
    if (r.status === 404) return null;
    if (r.status !== 200) throw new Error(`GET config: ${r.status}`);
    return (await r.json()) as { version: number; blob: string };
  }

  /** WebSocket 연결. onMessage로 서버 이벤트(JSON)를 전달한다. 30초마다 ping. */
  watch(onMessage: (m: any) => void): { close(): void; opened: Promise<void> } {
    const url = this.baseUrl.replace(/^http/, 'ws') + `/v1/ws?device_id=${this.deviceId}`;
    // Node/undici의 WebSocket은 두 번째 인자로 headers를 받는다 (브라우저 표준은 아님).
    const ws = new (WebSocket as any)(url, { headers: this.auth }) as WebSocket;
    const timer = setInterval(() => ws.readyState === 1 && ws.send('ping'), 30_000);
    ws.addEventListener('message', (e) => e.data !== 'pong' && onMessage(JSON.parse(String(e.data))));
    ws.addEventListener('close', () => clearInterval(timer));
    const opened = new Promise<void>((res, rej) => {
      ws.addEventListener('open', () => res());
      ws.addEventListener('error', () => rej(new Error('websocket error')));
    });
    return { close: () => { clearInterval(timer); ws.close(); }, opened };
  }
}
