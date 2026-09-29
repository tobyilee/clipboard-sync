export const HARD_CAP = 51 * 1024 * 1024; // 서버 하드 캡 (D-19)
export const DO_BODY_MAX = 1.25 * 1024 * 1024; // 이하는 DO, 초과는 R2 (D-6)
export const INLINE_MAX = 32 * 1024; // WS inline_body 한도 (5.3)
export const BLOB_MAX = 64 * 1024; // header/config blob 한도 (D-37)

export function toHex(b: Uint8Array): string {
  let s = '';
  for (const x of b) s += x.toString(16).padStart(2, '0');
  return s;
}

/** 소문자 hex 엄격 파싱. 길이(바이트)가 다르거나 대문자/비hex가 있으면 null. */
export function fromHex(s: string | null, bytes: number): Uint8Array | null {
  if (s === null || s.length !== bytes * 2 || !/^[0-9a-f]+$/.test(s)) return null;
  const out = new Uint8Array(bytes);
  for (let i = 0; i < bytes; i++) out[i] = parseInt(s.slice(i * 2, i * 2 + 2), 16);
  return out;
}

/** 표준 base64 (패딩 있음, D-35). */
export function toB64(buf: ArrayBuffer | Uint8Array): string {
  const b = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
  let s = '';
  for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode(...b.subarray(i, i + 0x8000));
  return btoa(s);
}

/** 패딩 없는 base64url을 엄격히 디코드해 정확히 32바이트일 때만 반환 (D-35). */
export function decodeToken(s: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]{43}$/.test(s)) return null;
  const std = s.replace(/-/g, '+').replace(/_/g, '/') + '=';
  let bin: string;
  try {
    bin = atob(std);
  } catch {
    return null;
  }
  const out = Uint8Array.from(bin, (c) => c.charCodeAt(0));
  // 마지막 문자의 남는 비트가 0이 아닌 비정규 인코딩 거부
  return out.length === 32 && toB64(out) === std ? out : null;
}

export function ctEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a[i]! ^ b[i]!;
  return d === 0;
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

export function text(status: number, msg: string): Response {
  return new Response(msg, { status });
}
