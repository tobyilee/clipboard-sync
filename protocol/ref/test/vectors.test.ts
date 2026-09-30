// Consumer-style tests: read protocol/test-vectors.json and verify every section.
// Swift and C# suites mirror exactly these checks.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import {
  configAad, decodeBundle, deriveKeys, encodeBundle, formatUuid, itemAad, normalizePassphrase, open, parseUuid, previewOf, sanitizeFileName, seal, sealWithNonce, uniqueFileNames, uuidHex,
} from '../src/protocol.ts';
import { buildVectors } from '../src/vectors.ts';

const path = new URL('../../test-vectors.json', import.meta.url);
const V: any = JSON.parse(readFileSync(path, 'utf8'));
const h = (s: string) => Buffer.from(s, 'hex');
const toEntries = (l: any[]) => l.map((e) => ({ type: e.type, name: e.name, data: h(e.data_hex) }));

test('committed vectors equal regenerated vectors', () => {
  assert.equal(JSON.stringify(buildVectors(), null, 2) + '\n', readFileSync(path, 'utf8'));
});

test('passphrases: normalization + key derivation', () => {
  for (const p of V.passphrases) {
    const k = deriveKeys(p.passphrase);
    assert.equal(k.passNorm.toString('hex'), p.pass_norm_hex, p.name);
    assert.equal(k.master.toString('hex'), p.master, p.name);
    assert.equal(k.encKey.toString('hex'), p.enc_key, p.name);
    assert.equal(k.authToken.toString('hex'), p.auth_token, p.name);
    assert.equal(k.vaultId, p.vault_id, p.name);
  }
  const by = (n: string) => V.passphrases.find((p: any) => p.name === n);
  assert.equal(by('ws-variant-of-ascii').vault_id, by('ascii').vault_id);
  assert.notEqual(by('case-differs').vault_id, by('ascii').vault_id);
  assert.notEqual(by('zwsp-feff-not-whitespace').vault_id, by('ascii').vault_id);
});

test('normalizePassphrase edge cases', () => {
  assert.equal(normalizePassphrase('   '), '');
  assert.equal(normalizePassphrase('a　　b'), 'a b');
  assert.equal(normalizePassphrase(' a  b '), 'a b');
  assert.equal(normalizePassphrase('a​b'), 'a​b');
  assert.equal(normalizePassphrase('a﻿b'), 'a﻿b');
  assert.equal(normalizePassphrase(' a '), 'a');
});

test('uuids: string -> RFC 4122 bytes', () => {
  for (const u of V.uuids) {
    const b = parseUuid(u.input);
    assert.equal(b.toString('hex'), u.bytes_hex);
    assert.equal(formatUuid(b), u.canonical);
    assert.equal(uuidHex(b), u.hex_id);
  }
});

test('seals: AAD rebuilt from fields, sealed bytes match, opens', () => {
  const keyOf = (n: string) => h(V.passphrases.find((p: any) => p.name === n).enc_key);
  for (const s of V.seals) {
    const aad = s.part === 'config'
      ? configAad(BigInt(s.config_version))
      : itemAad(parseUuid(s.item_id_uuid), parseUuid(s.device_id_uuid), BigInt(s.created_at), s.part === 'header' ? 1 : 2);
    assert.equal(aad.toString('hex'), s.aad_hex, s.name);
    const key = keyOf(s.key_from);
    assert.equal(sealWithNonce(key, h(s.nonce), aad, h(s.plaintext_hex)).toString('hex'), s.sealed_hex, s.name);
    const pt = open(key, aad, h(s.sealed_hex));
    assert.equal(pt.toString('hex'), s.plaintext_hex, s.name);
    if (s.expect_json) assert.deepEqual(JSON.parse(pt.toString('utf8')), s.expect_json, s.name); // compare parsed fields, never encoder bytes
  }
});

test('seal (random nonce) roundtrips and nonces differ', () => {
  const key = h(V.passphrases[0].enc_key), aad = h(V.seals[0].aad_hex);
  const a = seal(key, aad, Buffer.from('x')), b = seal(key, aad, Buffer.from('x'));
  assert.notEqual(a.toString('hex'), b.toString('hex'));
  assert.equal(open(key, aad, a).toString(), 'x');
});

test('negatives: every entry must fail', () => {
  for (const n of V.negatives) {
    assert.equal(n.expect, 'fail');
    assert.throws(() => open(h(n.key_hex), h(n.aad_hex), h(n.sealed_hex)), undefined, n.name);
  }
});

test('bundles: encode -> bytes and bytes -> decode', () => {
  for (const b of V.bundles) {
    const want = toEntries(b.entries);
    const got = decodeBundle(h(b.bytes_hex));
    assert.deepEqual(got.map((e) => ({ ...e, data: Buffer.from(e.data) })), want, b.name);
    if (!b.decode_only) assert.equal(encodeBundle(toEntries(b.encode_input ?? b.entries)).toString('hex'), b.bytes_hex, b.name);
  }
});

test('bundle_invalid: every entry must error', () => {
  for (const b of V.bundle_invalid) {
    assert.equal(b.expect, 'error');
    assert.throws(() => decodeBundle(h(b.bytes_hex)), undefined, b.name);
  }
});

test('previews: NFC first, then 200 code points', () => {
  for (const p of V.previews) assert.equal(previewOf(p.input), p.output, p.name);
});

test('file names: sanitize rules (D-57)', () => {
  for (const f of V.file_names) assert.equal(sanitizeFileName(f.input), f.expect, f.name);
  for (const f of V.file_name_sets) assert.deepEqual(uniqueFileNames(f.inputs), f.expect, f.name);
  // 불변식: 결과는 구분자·예약 문자·제어 문자가 없고 한도 안이며, 정리 결과를 다시 정리해도 같다
  for (const f of V.file_names) {
    assert.ok(!/[\/\\<>:"|?*\u0000-\u001f\u007f]/.test(f.expect), f.name);
    assert.ok(f.expect.length <= 150 && Buffer.byteLength(f.expect) <= 240, f.name);
    assert.ok(f.expect !== '' && !f.expect.endsWith('.') && !f.expect.endsWith(' '), f.name);
    assert.equal(sanitizeFileName(f.expect), f.expect, f.name + ' idempotent');
  }
});
