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
test('PostHog sends only to the publisher after new consent, ignoring legacy user projects', async () => {
  const values = new Map([['usage.enabled', true], ['usage.host', 'https://user-project.example']]), secrets = new Map([['usage.projectToken', 'phc_user_project123']]); let allowed = true; const captured = [];
  const context = { globalState: { get: (key, fallback) => values.has(key) ? values.get(key) : fallback, update: async (key, value) => values.set(key, value) }, secrets: { get: async key => secrets.get(key), delete: async key => secrets.delete(key) } };
  const original = global.fetch;
  global.fetch = async (url, options) => { captured.push({ url, body: JSON.parse(options.body) }); return { ok: true }; };
  const usage = new UsageStatistics(context, () => allowed, { host: 'https://publisher.example', projectToken: 'phc_publisher_test123' });
  try {
    await usage.capture('build', true, 'en', '1.2.1'); assert.equal(captured.length, 0);
    await usage.enable(true); await usage.capture('build', true, 'en', '1.2.1'); assert.equal(captured.length, 1);
    assert.equal(captured[0].url, 'https://publisher.example/capture/');
    assert.equal(captured[0].body.api_key, 'phc_publisher_test123');
    assert.deepEqual(Object.keys(captured[0].body.properties).sort(), ['$geoip_disable', '$process_person_profile', 'distinct_id', 'feature', 'language', 'platform', 'success', 'version']);
    assert.ok(!JSON.stringify(usage.snapshot()).includes('phc_'));
    allowed = false; await usage.capture('flash', false, 'en', '1.2.1'); assert.equal(captured.length, 1);
    const identityKey = [...values.keys()].find(key => key.startsWith('usage.publisherId.'));
    await usage.enable(false); assert.equal(values.get(identityKey), undefined);
    assert.throws(() => ingestionHost('http://insecure.example'));
  } finally { usage.dispose(); global.fetch = original; }
});
test('PostHog requires fresh consent for a changed publisher and stays disabled without build configuration', async () => {
  const values = new Map();
  const context = { globalState: { get: key => values.get(key), update: async (key, value) => values.set(key, value) } };
  const first = new UsageStatistics(context, () => true, { host: 'https://publisher.example', projectToken: 'phc_project_one123' });
  await first.enable(true);
  const next = new UsageStatistics(context, () => true, { host: 'https://publisher.example', projectToken: 'phc_project_two123' });
  assert.equal(next.snapshot().enabled, false);
  const missing = new UsageStatistics(context, () => true, { host: 'https://publisher.example', projectToken: '' });
  assert.equal(missing.snapshot().configured, false);
  await assert.rejects(missing.enable(true)); first.dispose(); next.dispose(); missing.dispose();
});
test('first activation asks once, stays disabled before acceptance and remembers dismissal across projects', async () => {
  const values = new Map();
  const context = { globalState: { get: key => values.get(key), update: async (key, value) => values.set(key, value) } };
  const project = { host: 'https://publisher.example', projectToken: 'phc_startup_test123' };
  const usage = new UsageStatistics(context, () => true, project);
  let prompts = 0;
  const ask = async () => { prompts++; assert.equal(usage.snapshot().enabled, false); return undefined; };
  await Promise.all([usage.promptOnce(ask), usage.promptOnce(ask)]);
  assert.equal(prompts, 1);
  assert.equal(usage.snapshot().enabled, false);
  const reopened = new UsageStatistics(context, () => true, project);
  await reopened.promptOnce(ask);
  assert.equal(prompts, 1);
  usage.dispose(); reopened.dispose();
});
test('first activation accepts explicit consent and never asks after a later manual opt-out', async () => {
  const values = new Map();
  const context = { globalState: { get: key => values.get(key), update: async (key, value) => values.set(key, value) } };
  const project = { host: 'https://publisher.example', projectToken: 'phc_startup_test123' };
  const usage = new UsageStatistics(context, () => true, project);
  await usage.promptOnce(async () => true);
  assert.equal(usage.snapshot().enabled, true);
  await usage.enable(false);
  const reopened = new UsageStatistics(context, () => true, project);
  await reopened.promptOnce(() => assert.fail('Do not ask again after an opt-out'));
  assert.equal(reopened.snapshot().enabled, false);
  const changed = new UsageStatistics(context, () => true, { ...project, projectToken: 'phc_other_project123' });
  let prompts = 0;
  await changed.promptOnce(async () => { prompts++; return false; });
  assert.equal(prompts, 1);
  assert.equal(changed.snapshot().enabled, false);
  usage.dispose(); reopened.dispose(); changed.dispose();
});
test('startup consent skips disabled VS Code telemetry and unconfigured publisher builds', async () => {
  const values = new Map(); let allowed = false;
  const context = { globalState: { get: key => values.get(key), update: async (key, value) => values.set(key, value) } };
  const project = { host: 'https://publisher.example', projectToken: 'phc_startup_test123' };
  const usage = new UsageStatistics(context, () => allowed, project);
  await usage.promptOnce(() => assert.fail('Respect the global telemetry switch'));
  allowed = true;
  let prompts = 0;
  await usage.promptOnce(async () => { prompts++; return false; });
  assert.equal(prompts, 1);
  const missing = new UsageStatistics(context, () => true, { ...project, projectToken: '' });
  await missing.promptOnce(() => assert.fail('Do not offer unavailable statistics'));
  usage.dispose(); missing.dispose();
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
