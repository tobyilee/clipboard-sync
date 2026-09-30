// CLI 테스트 피어의 네트워킹: 서버 API(spec 5장)를 호출하는 최소 클라이언트. 의존성 없음(Node 24 내장 fetch/WebSocket).
import { randomBytes } from 'node:crypto';
import {
  configAad, decodeBundle, deriveKeys, encodeBundle, itemAad, open, previewOf, seal, uuidHex,
  type BundleEntry, type Keys,
} from './protocol.ts';

/** vault 설정 (spec 4.4). 없거나 못 받으면 텍스트만 (fail-closed). */
export interface VaultConfig { v: 1; images: boolean; files: boolean; max_media_bytes: number }
export const MIB = 1024 * 1024;
export const DEFAULT_CONFIG: VaultConfig = { v: 1, images: false, files: false, max_media_bytes: 20 * MIB };
export const MEDIA_SIZES = [5 * MIB, 10 * MIB, 20 * MIB, 50 * MIB];

/** PNG IHDR에서 가로·세로를 읽는다 (디코드하지 않음). PNG가 아니면 null. */
export function pngSize(b: Uint8Array): { w: number; h: number } | null {
  const sig = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (b.length < 24 || !sig.every((x, i) => b[i] === x) || Buffer.from(b.subarray(12, 16)).toString('latin1') !== 'IHDR') return null;
  const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
  return { w: v.getUint32(16), h: v.getUint32(20) };
}

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
  async send(entries: BundleEntry[], kinds: string[], previewText?: string, extraHeader: object = {}): Promise<{ id: string; seq: number }> {
    const id = randomBytes(16);
    const createdAt = BigInt(Date.now());
    const body = encodeBundle(entries);
    const header = { v: 1, kinds, ...(previewText !== undefined ? { preview: previewOf(previewText) } : {}), ...extraHeader, body_plain_size: body.length };
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

  sendImage(png: Uint8Array) {
    const size = pngSize(png);
    if (!size) throw new Error('not a PNG');
    return this.send([{ type: 3, name: '', data: png }], ['image'], undefined, { image: size });
  }

  /** 파일 항목 (type=4만, 파일명은 NFC; `rawNames`면 정규화하지 않아 수신 측 정리를 시험). */
  sendFiles(files: { name: string; data: Uint8Array }[], rawNames = false) {
    const named = files.map((f) => ({ ...f, name: rawNames ? f.name : f.name.normalize('NFC') }));
    return this.send(named.map((f) => ({ type: 4, name: f.name, data: f.data })), ['files'], named.map((f) => f.name).join(', '),
      { files: named.map((f) => ({ name: f.name, size: f.data.length })) });
  }

  /** 복호화한 설정. 서버에 없으면 null (= 텍스트만). */
  async readConfig(): Promise<{ version: number; config: VaultConfig } | null> {
    const c = await this.getConfig();
    if (!c) return null;
    const plain = open(this.keys.encKey, configAad(BigInt(c.version)), Buffer.from(c.blob, 'base64'));
    return { version: c.version, config: JSON.parse(plain.toString('utf8')) as VaultConfig };
  }

  /** 설정 변경: 최신을 읽어 update를 적용하고 If-Match로 저장. 412면 한 번 다시 읽어 재적용 (D-50). 반환: 새 version. */
  async writeConfig(update: (c: VaultConfig) => VaultConfig): Promise<number> {
    for (let attempt = 0; attempt < 2; attempt++) {
      const cur = await this.readConfig();
      const version = cur?.version ?? 0;
      const next = update({ ...(cur?.config ?? DEFAULT_CONFIG) });
      const sealed = seal(this.keys.encKey, configAad(BigInt(version + 1)), Buffer.from(JSON.stringify(next), 'utf8'));
      const r = await this.req('/v1/config', { method: 'PUT', headers: { 'if-match': String(version) }, body: sealed });
      if (r.status === 200) return ((await r.json()) as { version: number }).version;
      if (r.status !== 412) throw new Error(`PUT config: ${r.status}`);
    }
    throw new Error('PUT config: conflict after retry');
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
