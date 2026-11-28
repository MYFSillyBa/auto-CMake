const { test } = require('node:test');
const assert = require('node:assert/strict');
const { mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { join } = require('node:path');
const { tmpdir } = require('node:os');
const { once } = require('node:events');
const net = require('node:net');
const { TelemetrySession } = require('../src/session.cjs');
const { initialState } = require('../src/config.cjs');
const { encodeSample } = require('../src/protocol.cjs');
const { parseCsv } = require('../src/samples.cjs');

async function fixture(t) {
  const dir = await mkdtemp(join(tmpdir(), 'signal-dock-capture-test-'));
  const session = new TelemetrySession({ ...initialState(), capacity: 2 });
  t.after(async () => {
    await session.dispose();
    await rm(dir, { recursive: true, force: true });
  });
  return { dir, session };
}

test('connection captures all rows without a record command, including after ring wrap and disconnect', async t => {
  const { dir, session } = await fixture(t);
  await session.connect({ kind: 'tcpServer', host: '127.0.0.1', port: 0, engine: 'justfloat' });
  const peer = net.connect(session.transport.address().port, '127.0.0.1');
  t.after(() => peer.destroy());
  await once(peer, 'connect');
  peer.write(Buffer.concat(Array.from({ length: 8 }, (_, i) => encodeSample('justfloat', [i]))));
  for (let i = 0; i < 100 && session.stats.samples < 8; i++) {
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.equal(session.stats.samples, 8);
  assert.equal(session.ring.count, 2);
  await session.disconnect();
  const path = join(dir, 'all.csv');
  await session.saveCsv(path, true);
  const data = parseCsv(await readFile(path, 'utf8'));
  assert.deepEqual(data.rows, Array.from({ length: 8 }, (_, i) => [i]));
  assert.equal(data.dtMs, 10);
});

test('clear starts a new capture and saving twice includes later arrivals and expanded channels', async t => {
  const { dir, session } = await fixture(t);
  const sample = values => session.consume({ kind: 'sample', values }, '');
  sample([99]);
  await session.clearSamples();
  sample([1]);
  sample([2, 20]);
  const path = join(dir, 'saved.csv');
  await session.saveCsv(path, false);
  assert.deepEqual(parseCsv(await readFile(path, 'utf8')).rows, [[1, NaN], [2, 20]]);
  sample([3, 30]);
  await session.saveCsv(path, false);
  assert.deepEqual(parseCsv(await readFile(path, 'utf8')).rows, [[1, NaN], [2, 20], [3, 30]]);
});

test('export snapshots remain valid when new samples arrive and capture is cleared', async t => {
  const { dir, session } = await fixture(t);
  for (let i = 0; i < 100; i++) session.consume({ kind: 'sample', values: [i] }, '');
  const path = join(dir, 'snapshot.csv');
  const save = session.saveCsv(path, false);
  session.consume({ kind: 'sample', values: [100] }, '');
  const clear = session.clearSamples();
  session.consume({ kind: 'sample', values: [200] }, '');
  await Promise.all([save, clear]);
  assert.equal(parseCsv(await readFile(path, 'utf8')).rows.length, 100);
  const next = join(dir, 'next.csv');
  await session.saveCsv(next, false);
  assert.deepEqual(parseCsv(await readFile(next, 'utf8')).rows, [[200]]);
});

test('empty or failed captures leave existing destination files intact', async t => {
  const { dir, session } = await fixture(t);
  const path = join(dir, 'existing.csv');
  await writeFile(path, 'keep me');
  await assert.rejects(() => session.saveCsv(path, false));
  assert.equal(await readFile(path, 'utf8'), 'keep me');
});

test('manual recordings include only samples between start and stop and can be saved after a failed attempt', async t => {
  const { dir, session } = await fixture(t);
  const sample = values => session.consume({ kind: 'sample', values }, '');
  assert.throws(() => session.startRecording(), /请先连接设备/);
  await session.connect({ kind: 'tcpServer', host: '127.0.0.1', port: 0, engine: 'justfloat' });
  sample([99]);
  session.startRecording();
  for (let i = 1; i <= 5; i++) sample([i, i * 10]);
  assert.equal(session.ring.count, 2);
  assert.equal(session.stopRecording(), true);
  sample([6, 60]);
  await assert.rejects(() => session.saveRecording(join(dir, 'missing', 'failed.csv')), { code: 'ENOENT' });
  assert.equal(session.recordingStatus, 'pending');
  assert.throws(() => session.startRecording());
  const path = join(dir, 'recording.csv');
  await session.saveRecording(path);
  assert.deepEqual(parseCsv(await readFile(path, 'utf8')), {
    names: ['I0', 'I1'], rows: [[1, 10], [2, 20], [3, 30], [4, 40], [5, 50]], dtMs: 10
  });
  assert.equal(session.recordingStatus, 'idle');
});

test('disconnect finalizes an active recording for saving and allows the next recording to start fresh', async t => {
  const { dir, session } = await fixture(t);
  const connection = { kind: 'tcpServer', host: '127.0.0.1', port: 0, engine: 'justfloat' };
  await session.connect(connection);
  session.startRecording();
  session.consume({ kind: 'sample', values: [42] }, '');
  await session.disconnect();
  assert.equal(session.recordingStatus, 'pending');
  const path = join(dir, 'disconnected.csv');
  await session.saveRecording(path);
  assert.deepEqual(parseCsv(await readFile(path, 'utf8')).rows, [[42]]);
  await session.connect(connection);
  session.startRecording();
  assert.equal(session.stopRecording(), false);
  assert.equal(session.recordingStatus, 'idle');
});
