// Builds protocol/test-vectors.json deterministically (fixed inputs, fixed nonces).
import {
  configAad, deriveKeys, encodeBundle, formatUuid, itemAad, parseUuid, previewOf, sanitizeFileName, sealWithNonce, uniqueFileNames, uuidHex,
  type BundleEntry, type Keys,
} from './protocol.ts';

const hex = (b: Uint8Array): string => Buffer.from(b).toString('hex');
const utf8 = (s: string): Buffer => Buffer.from(s, 'utf8');

const PASSPHRASES = [
  { name: 'ascii', text: 'abacus abdomen abide abnormal abrasion abroad absence' },
  { name: 'nfkd', text: 'Café 한글 ＡＢＣ①' },
  // whitespace variants of `ascii` (NBSP, tab, U+3000, U+2003, newline, U+2028, leading/trailing) must converge on the same keys
  { name: 'ws-variant-of-ascii', text: '  abacus  abdomen\tabide　abnormal abrasion  abroad\nabsence  ' },
  // case is not folded
  { name: 'case-differs', text: 'Abacus abdomen abide abnormal abrasion abroad absence' },
  // U+200B / U+FEFF are NOT in the whitespace set (JS \s and some runtimes treat FEFF as whitespace)
  { name: 'zwsp-feff-not-whitespace', text: 'abacus​abdomen﻿abide abnormal abrasion abroad absence' },
];

const ITEM_ID = '00112233-4455-6677-8899-aabbccddeeff';
const DEVICE_ID = 'ffeeddcc-bbaa-9988-7766-554433221100';
const OTHER_ITEM_ID = 'f47ac10b-58cc-4372-a567-0e02b2c3d479';
const CREATED_AT = 1790000000123n;
const CONFIG_VERSION = 7n;
const NONCES = { header: '000102030405060708090a0b', body: '0c0d0e0f1011121314151617', config: '18191a1b1c1d1e1f20212223', bundle: '2425262728292a2b2c2d2e2f' };

const png = Buffer.from('89504e470d0a1a0a0000000d49484452', 'hex');
const entry = (type: number, name: string, data: Uint8Array): BundleEntry => ({ type, name, data });
const jsonEntry = (e: BundleEntry) => ({ type: e.type, name: e.name, data_hex: hex(e.data) });

function rawBundle(opts: { magic?: string; version?: number; count?: number; entries?: { type: number; name?: string; data?: Buffer }[]; trailing?: Buffer }): Buffer {
  const parts: Buffer[] = [Buffer.from(opts.magic ?? 'CSB1', 'ascii'), Buffer.from([opts.version ?? 1])];
  const list = opts.entries ?? [];
  const c = Buffer.alloc(2); c.writeUInt16BE(opts.count ?? list.length); parts.push(c);
  for (const e of list) {
    const n = utf8(e.name ?? ''); const d = e.data ?? Buffer.alloc(0);
    const h = Buffer.alloc(3 + n.length + 4);
    h[0] = e.type; h.writeUInt16BE(n.length, 1); n.copy(h, 3); h.writeUInt32BE(d.length, 3 + n.length);
    parts.push(h, d);
  }
  if (opts.trailing) parts.push(opts.trailing);
  return Buffer.concat(parts);
}

export function buildVectors(): unknown {
  const keys: Record<string, Keys> = {};
  const passphrases = PASSPHRASES.map((p) => {
    const k = deriveKeys(p.text);
    keys[p.name] = k;
    return { name: p.name, passphrase: p.text, pass_norm_hex: hex(k.passNorm), master: hex(k.master), enc_key: hex(k.encKey), auth_token: hex(k.authToken), vault_id: k.vaultId };
  });

  const uuids = [ITEM_ID, DEVICE_ID, OTHER_ITEM_ID, 'F47AC10B-58CC-4372-A567-0E02B2C3D479'].map((s) => {
    const b = parseUuid(s);
    return { input: s, bytes_hex: hex(b), canonical: formatUuid(b), hex_id: uuidHex(b) };
  });

  // ----- seals -----
  const seals: unknown[] = [];
  const bundleForBody = encodeBundle([entry(2, '', utf8('<b>hi</b>')), entry(1, '', utf8('hi'))]);
  const plain = {
    header: utf8('{"kind":"text","size":5}'),
    body: utf8('hello, 클립보드'),
    config: utf8('{"image":false,"maxMB":20}'),
  };
  for (const who of ['ascii', 'nfkd']) {
    const key = keys[who].encKey;
    const item = { item_id_uuid: ITEM_ID, device_id_uuid: DEVICE_ID, created_at: String(CREATED_AT) };
    const mk = (name: string, part: 'header' | 'body' | 'config', aad: Buffer, pt: Buffer, nonce: string, extra: object) =>
      seals.push({ name: `${who}/${name}`, key_from: who, part, ...extra, nonce, plaintext_hex: hex(pt), aad_hex: hex(aad), sealed_hex: hex(sealWithNonce(key, Buffer.from(nonce, 'hex'), aad, pt)) });
    mk('header', 'header', itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 1), plain.header, NONCES.header, { ...item, expect_json: JSON.parse(plain.header.toString()) });
    mk('body', 'body', itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 2), plain.body, NONCES.body, item);
    mk('config', 'config', configAad(CONFIG_VERSION), plain.config, NONCES.config, { config_version: String(CONFIG_VERSION), expect_json: JSON.parse(plain.config.toString()) });
  }
  seals.push({
    name: 'ascii/body-bundle', key_from: 'ascii', part: 'body', item_id_uuid: ITEM_ID, device_id_uuid: DEVICE_ID, created_at: String(CREATED_AT),
    nonce: NONCES.bundle, plaintext_hex: hex(bundleForBody),
    aad_hex: hex(itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 2)),
    sealed_hex: hex(sealWithNonce(keys.ascii.encKey, Buffer.from(NONCES.bundle, 'hex'), itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 2), bundleForBody)),
  });

  // ----- negatives (all data-driven; every one must fail to open) -----
  const enc = keys.ascii.encKey;
  const aadH = itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 1);
  const aadB = itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 2);
  const sealedH = sealWithNonce(enc, Buffer.from(NONCES.header, 'hex'), aadH, plain.header);
  const sealedB = sealWithNonce(enc, Buffer.from(NONCES.body, 'hex'), aadB, plain.body);
  const sealedC = sealWithNonce(enc, Buffer.from(NONCES.config, 'hex'), configAad(CONFIG_VERSION), plain.config);
  const flip = (b: Buffer, i: number) => { const c = Buffer.from(b); c[i < 0 ? c.length + i : i] ^= 1; return c; };
  const neg = (name: string, description: string, key: Uint8Array, aad: Uint8Array, sealed: Uint8Array) =>
    ({ name, description, key_hex: hex(key), aad_hex: hex(aad), sealed_hex: hex(sealed), expect: 'fail' });
  const negatives = [
    neg('header-opened-as-body', 'header ciphertext with part=2 AAD (header/body swap)', enc, aadB, sealedH),
    neg('body-opened-as-header', 'body ciphertext with part=1 AAD', enc, aadH, sealedB),
    neg('body-with-other-item-id', 'body bound to a different item_id', enc, itemAad(parseUuid(OTHER_ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT, 2), sealedB),
    neg('body-with-other-device-id', 'body bound to a different origin device', enc, itemAad(parseUuid(ITEM_ID), parseUuid(OTHER_ITEM_ID), CREATED_AT, 2), sealedB),
    neg('body-with-other-created-at', 'created_at changed by one ms', enc, itemAad(parseUuid(ITEM_ID), parseUuid(DEVICE_ID), CREATED_AT + 1n, 2), sealedB),
    neg('wrong-key', 'valid ciphertext opened with auth_token as key', keys.ascii.authToken, aadH, sealedH),
    neg('key-from-other-passphrase', 'valid ciphertext opened with another vault key', keys.nfkd.encKey, aadH, sealedH),
    neg('truncated-last-byte', 'ciphertext missing its last byte', enc, aadH, sealedH.subarray(0, sealedH.length - 1)),
    neg('too-short', 'fewer than 28 bytes (nonce+tag)', enc, aadH, sealedH.subarray(0, 27)),
    neg('flipped-tag-bit', 'one bit flipped in the tag', enc, aadH, flip(sealedH, -1)),
    neg('flipped-ciphertext-bit', 'one bit flipped in the ciphertext body', enc, aadH, flip(sealedH, 12)),
    neg('flipped-nonce-bit', 'one bit flipped in the nonce', enc, aadH, flip(sealedH, 0)),
    neg('config-wrong-version', 'config sealed at version 7 opened with version 8 AAD', enc, configAad(CONFIG_VERSION + 1n), sealedC),
    neg('config-opened-as-header', 'config ciphertext with header AAD', enc, aadH, sealedC),
  ];

  // ----- bundles -----
  const kor = entry(4, '한글 파일.txt', utf8('korean'));
  const emo = entry(4, 'emoji😀.txt', utf8('emoji'));
  const okBundles = [
    { name: 'empty', entries: [] as BundleEntry[] },
    { name: 'text-only', entries: [entry(1, '', utf8('hello'))] },
    { name: 'text-html', entries: [entry(1, '', utf8('한글 hello')), entry(2, '', utf8('<b>한글</b> hello'))] },
    { name: 'text-html-png', entries: [entry(1, '', utf8('x')), entry(2, '', utf8('<i>x</i>')), entry(3, '', png)] },
    { name: 'files-korean-emoji', entries: [kor, emo] },
    { name: 'input-order-is-sorted', entries: [entry(1, '', utf8('t')), kor, emo], input: [kor, entry(1, '', utf8('t')), emo] },
  ].map((v) => {
    const bytes = encodeBundle(v.input ?? v.entries);
    return { name: v.name, entries: v.entries.map(jsonEntry), ...(v.input ? { encode_input: v.input.map(jsonEntry) } : {}), bytes_hex: hex(bytes) };
  });
  const unknown = rawBundle({ entries: [{ type: 1, data: utf8('a') }, { type: 9, data: Buffer.from('zz') }, { type: 4, name: 'f.txt', data: utf8('b') }] });
  okBundles.push({
    name: 'unknown-type-skipped', entries: [entry(1, '', utf8('a')), entry(4, 'f.txt', utf8('b'))].map(jsonEntry), decode_only: true, bytes_hex: hex(unknown),
  } as never);

  const bundleInvalid = [
    { name: 'bad-magic', bytes: rawBundle({ magic: 'XSB1' }) },
    { name: 'version-2', bytes: rawBundle({ version: 2 }) },
    { name: 'truncated-header', bytes: Buffer.from('CSB1\u0001', 'ascii') },
    { name: 'truncated-entry-data', bytes: rawBundle({ entries: [{ type: 1, data: utf8('hello') }] }).subarray(0, -2) },
    { name: 'count-too-many', bytes: rawBundle({ count: 2, entries: [{ type: 1, data: utf8('a') }] }) },
    { name: 'count-too-few', bytes: rawBundle({ count: 1, entries: [{ type: 1, data: utf8('a') }, { type: 1, data: utf8('b') }] }) },
    { name: 'trailing-garbage', bytes: rawBundle({ entries: [{ type: 1, data: utf8('a') }], trailing: Buffer.from([0]) }) },
    { name: 'text-with-name', bytes: rawBundle({ entries: [{ type: 1, name: 'x', data: utf8('a') }] }) },
    // type 4 entry whose name bytes are 0xFF 0xFE (invalid UTF-8): built by hand because rawBundle encodes names as UTF-8
    { name: 'invalid-utf8-name', bytes: Buffer.concat([Buffer.from('CSB1', 'ascii'), Buffer.from([1, 0, 1, 4, 0, 2, 0xff, 0xfe, 0, 0, 0, 1, 0x61])]) },
  ].map((v) => ({ name: v.name, bytes_hex: hex(v.bytes), expect: 'error' }));

  // ----- previews -----
  const nfdHangul = '한글'; // NFD of 한글
  const previews = [
    { name: 'short', input: 'hello', output: 'hello' },
    { name: 'emoji-on-boundary', input: 'a'.repeat(199) + '😀zzz', output: 'a'.repeat(199) + '😀' },
    { name: 'nfd-to-nfc', input: nfdHangul, output: '한글' },
    { name: 'nfc-before-cut', input: nfdHangul.repeat(150), output: '한글'.repeat(100) },
    { name: 'combining', input: 'éx', output: 'éx' },
  ].map((p) => ({ ...p, output: previewOf(p.input) === p.output ? p.output : (() => { throw new Error(`preview vector ${p.name} disagrees with previewOf`); })() }));

  // ----- file names (spec D-57) -----
  const longKo = '가'.repeat(100) + '.txt';                 // UTF-8 바이트 한도(240)가 먼저 걸림
  const longAscii = 'a'.repeat(200) + '.pdf';              // UTF-16 한도(150)
  const emojiEdge = 'x'.repeat(147) + '😀😀.png';          // 서로게이트 쌍을 쪼개지 않음
  const fileNameInputs: [string, string][] = [
    ['plain', 'report.pdf'],
    ['nfd-korean', '\u1112\u1161\u11ab\u1100\u1173\u11af.txt'],
    ['traversal', '../../etc/passwd'],
    ['windows-path', 'C:\\Users\\x\\a.txt'],
    ['unc', '\\\\server\\share\\a.txt'],
    ['reserved-chars', 'a<b>c:d"e|f?g*h.txt'],
    ['control', 'a\u0000b\u001fc\u007f.txt'],
    ['trailing-dots-spaces', 'name. . .'],
    ['leading-trailing-spaces', '   spaced name.txt   '],
    ['empty', ''],
    ['dot', '.'],
    ['dotdot', '..'],
    ['only-spaces', '    '],
    ['reserved-con', 'CON'],
    ['reserved-nul-ext', 'nul.txt'],
    ['reserved-com1-multi-ext', 'Com1.tar.gz'],
    ['reserved-lpt9-space', 'LPT9 .log'],
    ['not-reserved-com0', 'COM0.txt'],
    ['not-reserved-console', 'console.txt'],
    ['dotfile', '.gitignore'],
    ['long-korean', longKo],
    ['long-ascii', longAscii],
    ['emoji-boundary', emojiEdge],
    ['long-ext-dropped', 'b'.repeat(160) + '.' + 'e'.repeat(30)],
  ];
  const file_names = fileNameInputs.map(([name, input]) => ({ name, input, expect: sanitizeFileName(input) }));
  const fileSetInputs: [string, string[]][] = [
    ['case-insensitive-dupes', ['a.txt', 'A.txt', 'a.TXT', 'b.txt']],
    ['dupes-after-sanitize', ['x/y.txt', 'x\\y.txt', 'x_y.txt']],
    ['no-ext-dupes', ['README', 'readme']],
  ];
  const file_name_sets = fileSetInputs.map(([name, inputs]) => ({ name, inputs, expect: uniqueFileNames(inputs) }));

  return { version: 1, note: 'Generated by protocol/ref (npm run gen). Do not edit by hand.', passphrases, uuids, seals, negatives, bundles: okBundles, bundle_invalid: bundleInvalid, previews, file_names, file_name_sets };
}
