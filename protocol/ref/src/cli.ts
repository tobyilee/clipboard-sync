// 사용: CLIPSYNC_URL=... CLIPSYNC_PASSPHRASE="..." node src/cli.ts <send TEXT | list | watch | delete ID | config>
import { Peer } from './peer.ts';

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
    entries: r.entries?.map((e) => ({ type: e.type, name: e.name, bytes: e.data.length, text: e.type === 1 || e.type === 2 ? Buffer.from(e.data).toString('utf8').slice(0, 200) : undefined })) ?? 'purged' }));

if (cmd === 'send' && arg !== undefined) {
  console.log(JSON.stringify(await peer.sendText(arg)));
} else if (cmd === 'list') {
  for (const i of await peer.list()) show(await peer.receive(i, null, !i.purged));
} else if (cmd === 'delete' && arg) {
  await peer.deleteBody(arg);
  console.log('deleted', arg);
} else if (cmd === 'config') {
  console.log(JSON.stringify(await peer.getConfig()));
} else if (cmd === 'watch') {
  const w = peer.watch(async (m) => {
    if (m.t === 'item') show(await peer.receive(m, m.inline_body));
    else console.log(JSON.stringify(m));
  });
  await w.opened;
  console.error(`watching as device ${peer.deviceId} (Ctrl+C to stop)`);
} else {
  console.error('usage: send TEXT | list | watch | delete ID | config');
  process.exit(2);
}
