import { DurableObject } from 'cloudflare:workers';
import type { Env } from './env.ts';
import { INLINE_MAX, fromHex, toB64, toHex } from './util.ts';

const TTL_MS = 24 * 60 * 60 * 1000;
const ORPHAN_MS = 10 * 60 * 1000;
const CAP = 20;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS items (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  id BLOB NOT NULL UNIQUE,
  device_id BLOB NOT NULL,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  header BLOB NOT NULL,
  body_size INTEGER NOT NULL,
  purged INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS bodies (
  id BLOB PRIMARY KEY,
  storage TEXT NOT NULL,
  size INTEGER NOT NULL,
  body BLOB,
  r2_key TEXT,
  created_at INTEGER NOT NULL,
  committed INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS config (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  version INTEGER NOT NULL,
  blob BLOB NOT NULL
);`;

export type PutResult = 'ok' | 'conflict';
export type CommitResult = { seq: number } | 'nobody';
export type BodyMeta =
  | { status: 'notfound' }
  | { status: 'purged' }
  | { status: 'ok'; storage: 'do'; size: number; data: ArrayBuffer }
  | { status: 'ok'; storage: 'r2'; size: number; r2Key: string };

interface ItemRow {
  seq: number;
  id: ArrayBuffer;
  device_id: ArrayBuffer;
  created_at: number;
  header: ArrayBuffer;
  body_size: number;
  purged: number;
  [k: string]: SqlStorageValue;
}

/** vault당 하나. 항목 메타와 소형 본문을 SQLite에, 대형 본문은 R2에 두고 seq를 부여한다. */
export class Vault extends DurableObject<Env> {
  private sql: SqlStorage;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    ctx.blockConcurrencyWhile(async () => {
      this.sql.exec(SCHEMA);
    });
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('ping', 'pong'));
  }

  // ---- 업로드 ----

  /** 커밋됐거나 purged된 id는 재PUT 불가 (D-27). 만료로 지워진 id만 다시 쓸 수 있다. */
  private isFinal(id: ArrayBuffer): boolean {
    return this.sql.exec('SELECT 1 FROM items WHERE id = ?', id).toArray().length > 0;
  }

  beginPut(idHex: string): PutResult {
    return this.isFinal(fromHex(idHex, 16)!.buffer as ArrayBuffer) ? 'conflict' : 'ok';
  }

  async putSmall(idHex: string, data: ArrayBuffer): Promise<PutResult> {
    const id = fromHex(idHex, 16)!.buffer as ArrayBuffer;
    if (this.isFinal(id)) return 'conflict';
    const old = this.oldR2Key(id);
    this.sql.exec(
      `INSERT OR REPLACE INTO bodies (id, storage, size, body, r2_key, created_at, committed)
       VALUES (?, 'do', ?, ?, NULL, ?, 0)`,
      id,
      data.byteLength,
      data,
      Date.now(),
    );
    await this.rearm();
    if (old) await this.env.BODIES.delete(old);
    return 'ok';
  }

  /** R2 스트리밍이 끝난 뒤 호출. 그 사이 커밋/삭제됐다면 방금 쓴 객체를 지우고 conflict (D-34). */
  async finishLarge(idHex: string, r2Key: string, size: number): Promise<PutResult> {
    const id = fromHex(idHex, 16)!.buffer as ArrayBuffer;
    if (this.isFinal(id)) {
      await this.env.BODIES.delete(r2Key);
      return 'conflict';
    }
    const old = this.oldR2Key(id);
    this.sql.exec(
      `INSERT OR REPLACE INTO bodies (id, storage, size, body, r2_key, created_at, committed)
       VALUES (?, 'r2', ?, NULL, ?, ?, 0)`,
      id,
      size,
      r2Key,
      Date.now(),
    );
    await this.rearm();
    if (old && old !== r2Key) await this.env.BODIES.delete(old);
    return 'ok';
  }

  /** 업로드 실패 시 Worker가 방금 쓴 R2 객체를 정리한다. */
  async discardObject(r2Key: string): Promise<void> {
    await this.env.BODIES.delete(r2Key);
  }

  private oldR2Key(id: ArrayBuffer): string | null {
    const r = this.sql.exec<{ r2_key: string | null }>('SELECT r2_key FROM bodies WHERE id = ?', id).toArray();
    return r[0]?.r2_key ?? null;
  }

  async commit(idHex: string, deviceHex: string, createdAt: number, header: ArrayBuffer): Promise<CommitResult> {
    const id = fromHex(idHex, 16)!.buffer as ArrayBuffer;
    const dev = fromHex(deviceHex, 16)!.buffer as ArrayBuffer;
    const existing = this.sql.exec<{ seq: number }>('SELECT seq FROM items WHERE id = ?', id).toArray();
    if (existing[0]) return { seq: existing[0].seq };

    const body = this.sql
      .exec<{ storage: string; size: number; body: ArrayBuffer | null }>(
        'SELECT storage, size, body FROM bodies WHERE id = ?',
        id,
      )
      .toArray()[0];
    if (!body) return 'nobody';

    const now = Date.now();
    const seq = this.sql
      .exec<{ seq: number }>(
        `INSERT INTO items (id, device_id, created_at, expires_at, header, body_size, purged)
         VALUES (?, ?, ?, ?, ?, ?, 0) RETURNING seq`,
        id,
        dev,
        createdAt,
        now + TTL_MS,
        header,
        body.size,
      )
      .one().seq;
    this.sql.exec('UPDATE bodies SET committed = 1 WHERE id = ?', id);

    const r2Keys = this.enforceCap();

    const inline = body.storage === 'do' && body.body && body.size <= INLINE_MAX ? toB64(body.body) : null;
    this.broadcast(
      {
        t: 'item',
        seq,
        id: idHex,
        device_id: deviceHex,
        created_at: createdAt,
        header: toB64(header),
        body_size: body.size,
        inline_body: inline,
      },
      deviceHex,
    );

    await this.rearm();
    await this.deleteObjects(r2Keys);
    return { seq };
  }

  /** 최신 20개만 남기고 나머지 항목과 본문을 지운다 (purged도 포함, D-37). 삭제할 R2 키를 반환. */
  private enforceCap(): string[] {
    const stale = this.sql
      .exec<{ id: ArrayBuffer }>('SELECT id FROM items ORDER BY seq DESC LIMIT -1 OFFSET ?', CAP)
      .toArray();
    return this.dropItems(stale.map((r) => r.id));
  }

  /** 항목 행과 본문 행을 지우고, 함께 지워야 할 R2 키를 돌려준다. */
  private dropItems(ids: ArrayBuffer[]): string[] {
    const keys: string[] = [];
    for (const id of ids) {
      const k = this.oldR2Key(id);
      if (k) keys.push(k);
      this.sql.exec('DELETE FROM bodies WHERE id = ?', id);
      this.sql.exec('DELETE FROM items WHERE id = ?', id);
    }
    return keys;
  }

  private async deleteObjects(keys: string[]): Promise<void> {
    if (keys.length > 0) await this.env.BODIES.delete(keys);
  }

  // ---- 조회 ----

  list(since: number): unknown[] {
    return this.sql
      .exec<ItemRow>(
        `SELECT seq, id, device_id, created_at, header, body_size, purged FROM items
         WHERE seq > ? AND expires_at > ? ORDER BY seq ASC LIMIT ${CAP}`,
        since,
        Date.now(),
      )
      .toArray()
      .map((r) => ({
        seq: r.seq,
        id: toHex(new Uint8Array(r.id)),
        device_id: toHex(new Uint8Array(r.device_id)),
        created_at: r.created_at,
        header: toB64(r.header),
        body_size: r.body_size,
        purged: r.purged === 1,
      }));
  }

  getBodyMeta(idHex: string): BodyMeta {
    const id = fromHex(idHex, 16)!.buffer as ArrayBuffer;
    const item = this.sql
      .exec<{ purged: number }>('SELECT purged FROM items WHERE id = ? AND expires_at > ?', id, Date.now())
      .toArray()[0];
    if (!item) return { status: 'notfound' };
    if (item.purged) return { status: 'purged' };
    const b = this.sql
      .exec<{ storage: string; size: number; body: ArrayBuffer | null; r2_key: string | null }>(
        'SELECT storage, size, body, r2_key FROM bodies WHERE id = ?',
        id,
      )
      .toArray()[0];
    if (!b) return { status: 'notfound' };
    if (b.storage === 'do') return { status: 'ok', storage: 'do', size: b.size, data: b.body! };
    return { status: 'ok', storage: 'r2', size: b.size, r2Key: b.r2_key! };
  }

  /** 수신 후 삭제 (D-16). 없거나 미커밋/만료면 'notfound', 이미 purged여도 'ok' (idempotent). */
  async deleteBody(idHex: string): Promise<'ok' | 'notfound'> {
    const id = fromHex(idHex, 16)!.buffer as ArrayBuffer;
    const item = this.sql
      .exec<{ purged: number }>('SELECT purged FROM items WHERE id = ? AND expires_at > ?', id, Date.now())
      .toArray()[0];
    if (!item) return 'notfound';
    if (item.purged) return 'ok';
    const key = this.oldR2Key(id);
    this.sql.exec('DELETE FROM bodies WHERE id = ?', id);
    this.sql.exec('UPDATE items SET purged = 1 WHERE id = ?', id);
    this.broadcast({ t: 'body_purged', id: idHex });
    if (key) await this.env.BODIES.delete(key);
    return 'ok';
  }

  // ---- config ----

  getConfig(): { version: number; blob: string } | null {
    const r = this.sql.exec<{ version: number; blob: ArrayBuffer }>('SELECT version, blob FROM config WHERE id = 1').toArray()[0];
    return r ? { version: r.version, blob: toB64(r.blob) } : null;
  }

  putConfig(ifMatch: number, blob: ArrayBuffer): { ok: true; version: number } | { ok: false; version: number } {
    const cur = this.sql.exec<{ version: number }>('SELECT version FROM config WHERE id = 1').toArray()[0]?.version ?? 0;
    if (ifMatch !== cur) return { ok: false, version: cur };
    const version = cur + 1;
    this.sql.exec('INSERT OR REPLACE INTO config (id, version, blob) VALUES (1, ?, ?)', version, blob);
    this.broadcast({ t: 'config', version, blob: toB64(blob) });
    return { ok: true, version };
  }

  // ---- WebSocket (hibernation) ----

  override async fetch(request: Request): Promise<Response> {
    const device = new URL(request.url).searchParams.get('device_id') ?? '';
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server, [device]);
    server.send(JSON.stringify({ t: 'hello', seq: this.lastSeq(), config: this.getConfig() }));
    return new Response(null, { status: 101, webSocket: client });
  }

  override webSocketMessage(): void {
    // 클라이언트→서버 메시지는 없다. ping/pong은 auto-response가 처리한다.
  }

  override webSocketClose(ws: WebSocket): void {
    try {
      ws.close(1000);
    } catch {
      // 이미 닫힘
    }
  }

  /** 마지막으로 부여된 seq. 행이 지워져도 줄지 않는다 (D-36). */
  private lastSeq(): number {
    return this.sql.exec<{ seq: number }>("SELECT seq FROM sqlite_sequence WHERE name = 'items'").toArray()[0]?.seq ?? 0;
  }

  private broadcast(msg: unknown, exceptDevice?: string): void {
    const data = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      if (exceptDevice && this.ctx.getTags(ws).includes(exceptDevice)) continue;
      try {
        ws.send(data);
      } catch {
        // 끊어진 소켓은 무시
      }
    }
  }

  // ---- alarm: 만료 / 고아 정리 ----

  override async alarm(): Promise<void> {
    const now = Date.now();
    const expired = this.sql.exec<{ id: ArrayBuffer }>('SELECT id FROM items WHERE expires_at <= ?', now).toArray();
    const keys = this.dropItems(expired.map((r) => r.id));

    const orphans = this.sql
      .exec<{ id: ArrayBuffer }>('SELECT id FROM bodies WHERE committed = 0 AND created_at <= ?', now - ORPHAN_MS)
      .toArray();
    keys.push(...this.dropItems(orphans.map((r) => r.id)));

    await this.deleteObjects(keys);
    await this.rearm();
  }

  /** alarm은 하나만 쓴다: min(다음 만료, 가장 오래된 미커밋 본문 + 10분). */
  private async rearm(): Promise<void> {
    const exp = this.sql.exec<{ m: number | null }>('SELECT MIN(expires_at) AS m FROM items').one().m;
    const orph = this.sql.exec<{ m: number | null }>('SELECT MIN(created_at) AS m FROM bodies WHERE committed = 0').one().m;
    const times = [exp, orph === null ? null : orph + ORPHAN_MS].filter((t): t is number => t !== null);
    if (times.length === 0) await this.ctx.storage.deleteAlarm();
    else await this.ctx.storage.setAlarm(Math.min(...times));
  }
}
