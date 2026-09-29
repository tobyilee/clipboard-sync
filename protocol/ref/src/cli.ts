// 사용: CLIPSYNC_URL=... CLIPSYNC_PASSPHRASE="..." node src/cli.ts <command> (usage 참고)
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { MIB, Peer } from './peer.ts';

const url = process.env.CLIPSYNC_URL;
const pass = process.env.CLIPSYNC_PASSPHRASE;
if (!url || !pass) {
  console.error('CLIPSYNC_URL and CLIPSYNC_PASSPHRASE are required');
  process.exit(2);
}
const peer = new Peer(url, pass, process.env.CLIPSYNC_DEVICE_ID);
const [cmd, arg] = process.argv.slice(2);

const show = (r: Awaited<ReturnType<Peer['receive']>>) =>
  console.log(JSON.stringify({ seq: r.seq, id: r.id, from: r.deviceId, header: r.header,
    entries: r.entries?.map((e) => ({ type: e.type, name: e.name, bytes: e.data.length, text: e.type === 1 || e.type === 2 ? Buffer.from(e.data).toString('utf8').slice(0, 200) : undefined,
      sha256: e.type >= 3 ? createHash('sha256').update(e.data).digest('hex').slice(0, 16) : undefined })) ?? 'purged' }));

if (cmd === 'send' && arg !== undefined) {
  console.log(JSON.stringify(await peer.sendText(arg)));
} else if (cmd === 'send-html' && arg !== undefined) {
  // send-html HTML [PLAIN]: HTML fragment(+선택적 plain text fallback) 항목을 보낸다
  const plain = process.argv[4];
  const entries = [{ type: 1, name: '', data: Buffer.from(plain ?? '', 'utf8') }, { type: 2, name: '', data: Buffer.from(arg, 'utf8') }];
  console.log(JSON.stringify(await peer.send(plain === undefined ? entries.slice(1) : entries, plain === undefined ? ['html'] : ['text', 'html'], plain)));
} else if (cmd === 'list') {
  for (const i of await peer.list()) {
    try {
      show(await peer.receive(i, null, !i.purged));
    } catch (e) {
      // 복호화 실패 항목은 폐기 (spec 6.3-5). 나머지는 계속 출력한다.
      console.log(JSON.stringify({ seq: i.seq, id: i.id, error: String((e as Error).message) }));
    }
  }
} else if (cmd === 'delete' && arg) {
  await peer.deleteBody(arg);
  console.log('deleted', arg);
} else if (cmd === 'config' || cmd === 'config-get') {
  console.log(JSON.stringify(await peer.readConfig()));
} else if (cmd === 'config-set') {
  // config-set images=on files=off max=20   (max: 5|10|20|50 MiB)
  const args = process.argv.slice(3);
  const version = await peer.writeConfig((c) => {
    for (const a of args) {
      const [k, v] = a.split('=');
      if (k === 'images' || k === 'files') c[k] = v === 'on' || v === 'true';
      else if (k === 'max' && ['5', '10', '20', '50'].includes(v!)) c.max_media_bytes = Number(v) * MIB;
      else throw new Error(`bad arg ${a}`);
    }
    return c;
  });
  console.log(JSON.stringify({ version, ...(await peer.readConfig())?.config }));
} else if (cmd === 'send-image' && arg) {
  console.log(JSON.stringify(await peer.sendImage(readFileSync(arg))));
} else if (cmd === 'watch') {
  const w = peer.watch(async (m) => {
    if (m.t === 'item') show(await peer.receive(m, m.inline_body));
    else console.log(JSON.stringify(m));
  });
  await w.opened;
  console.error(`watching as device ${peer.deviceId} (Ctrl+C to stop)`);
} else {
  console.error('usage: send TEXT | send-html HTML [PLAIN] | send-image PNG | list | watch | delete ID | config-get | config-set images=on files=off max=20');
  process.exit(2);
}
