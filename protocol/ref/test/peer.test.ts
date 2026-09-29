// 서버 통합 테스트. CLIPSYNC_URL과 CLIPSYNC_PASSPHRASE(서버의 VAULT_ID와 같은 vault)가 있을 때만 실행한다.
//   CLIPSYNC_URL=http://localhost:8787 CLIPSYNC_PASSPHRASE="abacus abdomen abide abnormal abrasion abroad absence" npm test
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { Peer } from '../src/peer.ts';

const url = process.env.CLIPSYNC_URL;
const pass = process.env.CLIPSYNC_PASSPHRASE;
const opts = { skip: url && pass ? false : 'CLIPSYNC_URL / CLIPSYNC_PASSPHRASE not set' };

function nextMessage(msgs: any[], pred: (m: any) => boolean, ms = 5000): Promise<any> {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    const tick = () => {
      const m = msgs.find(pred);
      if (m) return resolve(m);
      if (Date.now() - t0 > ms) return reject(new Error('timeout waiting for message'));
      setTimeout(tick, 20);
    };
    tick();
  });
}

test('Mac→Win 텍스트: 한쪽이 보내면 다른 기기가 inline으로 받아 복호화한다', opts, async () => {
  const a = new Peer(url!, pass!), b = new Peer(url!, pass!);
  const msgs: any[] = [];
  const w = b.watch((m) => msgs.push(m));
  await w.opened;
  try {
    const text = `hello 한글 🎉 ${Date.now()}`;
    const { id } = await a.sendText(text);
    const m = await nextMessage(msgs, (x) => x.t === 'item' && x.id === id);
    assert.notEqual(m.inline_body, null);
    const r = await b.receive(m, m.inline_body);
    assert.equal(r.header.kinds[0], 'text');
    assert.equal(Buffer.from(r.entries![0]!.data).toString('utf8'), text);
    await b.deleteBody(id);
    await nextMessage(msgs, (x) => x.t === 'body_purged' && x.id === id);
  } finally {
    w.close();
  }
});

test('대용량(2 MiB, R2 경로) 왕복 후 수신 삭제 → purged', opts, async () => {
  const a = new Peer(url!, pass!), b = new Peer(url!, pass!);
  const data = randomBytes(2 * 1024 * 1024);
  const { id } = await a.send([{ type: 4, name: 'a.bin', data }], ['files']);
  const item = (await b.list()).find((i) => i.id === id)!;
  assert.equal(item.purged, false);
  const r = await b.receive(item);
  assert.equal(r.entries![0]!.name, 'a.bin');
  assert.deepEqual(Buffer.from(r.entries![0]!.data), data);
  await b.deleteBody(id);
  const after = (await b.list()).find((i) => i.id === id)!;
  assert.equal(after.purged, true);
  assert.equal((await b.receive(after)).entries, null);
});

test('다른 passphrase는 401', opts, async () => {
  const bad = new Peer(url!, 'wrong passphrase that is not registered');
  await assert.rejects(bad.list(), /list: 401/);
});
