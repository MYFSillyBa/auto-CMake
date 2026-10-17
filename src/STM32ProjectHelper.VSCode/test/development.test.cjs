'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { serialBytes, displayBytes } = require('../serial-session');
const { redactReport } = require('../fault-report');
const { UsageStatistics, ingestionHost } = require('../usage-statistics');
const { fingerprintProject, projectDelta } = require('../cubemx-sync');

test('serial formats bytes without changing HEX payloads or accepting incomplete bytes', () => {
  assert.deepEqual(serialBytes('00 a1 FF', 'HEX'), Buffer.from([0, 161, 255]));
  assert.equal(serialBytes('ok', 'ASCII', 'CRLF').toString(), 'ok\r\n');
  assert.throws(() => serialBytes('a', 'HEX'));
  assert.throws(() => serialBytes('中文', 'ASCII'));
  assert.equal(displayBytes(Buffer.from([0, 65, 255]), 'ASCII'), '\\x00A\\xff');
  assert.equal(displayBytes(Buffer.from([0, 65, 255]), 'HEX'), '00 41 FF');
});
test('local report redacts paths, tokens, email and IP addresses', () => {
  const text = redactReport('D:\\工程\\main.c error\nC:/Users/Name/key\napi_key=secret\nphc_testsecret\na@example.com 192.168.1.1', 'D:\\工程', 'C:/Users/Name');
  assert.ok(text.includes('<PROJECT>'));
  for (const secret of ['D:\\工程', 'C:/Users/Name', 'secret', 'phc_', 'example.com', '192.168.1.1']) assert.ok(!text.includes(secret), text);
});
test('PostHog requires explicit opt-in and only sends allowlisted metadata', async () => {
  const values = new Map(), secrets = new Map(); let allowed = true; const captured = [];
  const context = { globalState: { get: (key, fallback) => values.has(key) ? values.get(key) : fallback, update: async (key, value) => values.set(key, value) }, secrets: { get: async key => secrets.get(key), store: async (key, value) => secrets.set(key, value) } };
  const original = global.fetch;
  global.fetch = async (url, options) => { captured.push({ url, body: JSON.parse(options.body) }); return { ok: true }; };
  const usage = new UsageStatistics(context, () => allowed);
  try {
    await usage.capture('build', true, 'en', '1.2.1'); assert.equal(captured.length, 0);
    await usage.configure('https://us.i.posthog.com', 'phc_unit_test123');
    await usage.capture('build', true, 'en', '1.2.1'); assert.equal(captured.length, 0);
    await usage.enable(true); await usage.capture('build', true, 'en', '1.2.1'); assert.equal(captured.length, 1);
    assert.deepEqual(Object.keys(captured[0].body.properties).sort(), ['$geoip_disable', '$process_person_profile', 'distinct_id', 'feature', 'language', 'platform', 'success', 'version']);
    assert.ok(!JSON.stringify(usage.snapshot()).includes('phc_'));
    allowed = false; await usage.capture('flash', false, 'en', '1.2.1'); assert.equal(captured.length, 1);
    await usage.enable(false); assert.equal(values.get('usage.anonymousId'), undefined);
    assert.throws(() => ingestionHost('http://insecure.example'));
  } finally { usage.dispose(); global.fetch = original; }
});
test('CubeMX sync detects edits and deletions while excluding build outputs', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'stm32-sync-unit-'));
  try {
    await fs.writeFile(path.join(root, 'board.ioc'), 'Mcu.CPN=STM32F407VG');
    await fs.mkdir(path.join(root, 'build')); await fs.writeFile(path.join(root, 'build', 'CMakeLists.txt'), 'ignored');
    const initial = await fingerprintProject(root);
    await fs.writeFile(path.join(root, 'board.ioc'), 'Mcu.CPN=STM32H723VG');
    assert.deepEqual(projectDelta(initial, await fingerprintProject(root)), ['board.ioc']);
    await fs.unlink(path.join(root, 'board.ioc')); assert.deepEqual(projectDelta(initial, await fingerprintProject(root)), ['board.ioc']);
    assert.equal(initial.size, 1);
  } finally { assert.ok(path.basename(root).startsWith('stm32-sync-unit-')); await fs.rm(root, { recursive: true, force: true }); }
});
