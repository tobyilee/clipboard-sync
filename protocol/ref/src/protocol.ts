// Reference implementation of protocol v1 (see protocol/PROTOCOL.md). Erasable TypeScript only (runs on Node 24 as-is).
import { createCipheriv, createDecipheriv, createHash, hkdfSync, pbkdf2Sync, randomBytes } from 'node:crypto';

// ---------- 2. key derivation ----------
const WS = (cp: number): boolean =>
  (cp >= 0x09 && cp <= 0x0d) || cp === 0x20 || cp === 0x85 || cp === 0xa0 || cp === 0x1680 ||
  (cp >= 0x2000 && cp <= 0x200a) || cp === 0x2028 || cp === 0x2029 || cp === 0x202f || cp === 0x205f || cp === 0x3000;

export function normalizePassphrase(input: string): string {
  let out = '';
  let pendingSpace = false;
  for (const ch of input.normalize('NFKD')) {
    if (WS(ch.codePointAt(0)!)) { pendingSpace = out.length > 0; continue; }
    if (pendingSpace) { out += ' '; pendingSpace = false; }
    out += ch;
  }
  return out;
}

export interface Keys { passNorm: Buffer; master: Buffer; encKey: Buffer; authToken: Buffer; vaultId: string }

export function deriveKeys(passphrase: string): Keys {
  const passNorm = Buffer.from(normalizePassphrase(passphrase), 'utf8');
  const master = pbkdf2Sync(passNorm, Buffer.from('clipsync/v1/salt', 'utf8'), 600_000, 32, 'sha256');
  const hkdf = (info: string) => Buffer.from(hkdfSync('sha256', master, Buffer.alloc(0), Buffer.from(info, 'utf8'), 32));
  const encKey = hkdf('clipsync/v1/enc');
  const authToken = hkdf('clipsync/v1/auth');
  return { passNorm, master, encKey, authToken, vaultId: createHash('sha256').update(authToken).digest('hex') };
}

// ---------- 3. identifiers ----------
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function parseUuid(s: string): Buffer {
  if (!UUID_RE.test(s)) throw new Error(`invalid uuid: ${s}`);
  return Buffer.from(s.replaceAll('-', ''), 'hex'); // RFC 4122 order, left to right
}

export function formatUuid(b: Uint8Array): string {
  if (b.length !== 16) throw new Error('uuid must be 16 bytes');
  const h = Buffer.from(b).toString('hex');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export const uuidHex = (b: Uint8Array): string => Buffer.from(b).toString('hex');

// ---------- 4. encryption ----------
const u64 = (n: bigint): Buffer => { const b = Buffer.alloc(8); b.writeBigUInt64BE(n); return b; };

export const PART_HEADER = 1, PART_BODY = 2, PART_CONFIG = 3;

export function itemAad(itemId: Uint8Array, deviceId: Uint8Array, createdAtMs: bigint, part: 1 | 2): Buffer {
  if (itemId.length !== 16 || deviceId.length !== 16) throw new Error('ids must be 16 bytes');
  return Buffer.concat([Buffer.from([1]), itemId, deviceId, u64(createdAtMs), Buffer.from([part])]);
}

export function configAad(configVersion: bigint): Buffer {
  return Buffer.concat([Buffer.from([1]), u64(configVersion), Buffer.from([PART_CONFIG])]);
}

// Test-only entry point: fixed nonces must never reach production code paths.
export function sealWithNonce(key: Uint8Array, nonce: Uint8Array, aad: Uint8Array, plaintext: Uint8Array): Buffer {
  if (nonce.length !== 12) throw new Error('nonce must be 12 bytes');
  const c = createCipheriv('aes-256-gcm', key, nonce);
  c.setAAD(aad);
  const ct = Buffer.concat([c.update(plaintext), c.final()]);
  return Buffer.concat([nonce, ct, c.getAuthTag()]);
}

export function seal(key: Uint8Array, aad: Uint8Array, plaintext: Uint8Array): Buffer {
  return sealWithNonce(key, randomBytes(12), aad, plaintext);
}

export function open(key: Uint8Array, aad: Uint8Array, sealed: Uint8Array): Buffer {
  if (sealed.length < 28) throw new Error('sealed data too short');
  const s = Buffer.from(sealed);
  const d = createDecipheriv('aes-256-gcm', key, s.subarray(0, 12));
  d.setAAD(aad);
  d.setAuthTag(s.subarray(s.length - 16));
  return Buffer.concat([d.update(s.subarray(12, s.length - 16)), d.final()]);
}

// ---------- 5. preview ----------
export function previewOf(text: string): string {
  return Array.from(text.normalize('NFC')).slice(0, 200).join('');
}

// ---------- 6. bundle ----------
export interface BundleEntry { type: number; name: string; data: Uint8Array }
const MAGIC = Buffer.from('CSB1', 'ascii');

export function encodeBundle(entries: BundleEntry[]): Buffer {
  const sorted = [...entries].sort((a, b) => a.type - b.type); // stable: file order preserved within a type
  if (sorted.length > 0xffff) throw new Error('too many entries');
  const parts: Buffer[] = [MAGIC, Buffer.from([1]), Buffer.from([sorted.length >> 8, sorted.length & 0xff])];
  for (const e of sorted) {
    const name = Buffer.from(e.name, 'utf8');
    if (e.type >= 1 && e.type <= 3 && name.length !== 0) throw new Error(`type ${e.type} must have empty name`);
    if (name.length > 0xffff) throw new Error('name too long');
    const head = Buffer.alloc(3 + name.length + 4);
    head[0] = e.type; head.writeUInt16BE(name.length, 1); name.copy(head, 3); head.writeUInt32BE(e.data.length, 3 + name.length);
    parts.push(head, Buffer.from(e.data));
  }
  return Buffer.concat(parts);
}

export function decodeBundle(bytes: Uint8Array): BundleEntry[] {
  const b = Buffer.from(bytes);
  if (b.length < 7) throw new Error('bundle truncated (header)');
  if (!b.subarray(0, 4).equals(MAGIC)) throw new Error('bad magic');
  if (b[4] !== 1) throw new Error(`unsupported bundle version ${b[4]}`);
  const count = b.readUInt16BE(5);
  let off = 7;
  const out: BundleEntry[] = [];
  for (let i = 0; i < count; i++) {
    if (off + 3 > b.length) throw new Error('bundle truncated (entry header)');
    const type = b[off], nameLen = b.readUInt16BE(off + 1);
    off += 3;
    if (off + nameLen + 4 > b.length) throw new Error('bundle truncated (name)');
    let name: string;
    try { name = new TextDecoder('utf-8', { fatal: true }).decode(b.subarray(off, off + nameLen)); } catch { throw new Error('entry name is not valid UTF-8'); }
    off += nameLen;
    const dataLen = b.readUInt32BE(off);
    off += 4;
    if (off + dataLen > b.length) throw new Error('bundle truncated (data)');
    const data = Buffer.from(b.subarray(off, off + dataLen));
    off += dataLen;
    if (type >= 1 && type <= 3 && nameLen !== 0) throw new Error(`type ${type} must have empty name`);
    if (type >= 1 && type <= 4) out.push({ type, name, data });   // unknown types are skipped
  }
  if (off !== b.length) throw new Error('trailing bytes after last entry');
  return out;
}
