const { test } = require('node:test');
const assert = require('node:assert/strict');
const { mkdtemp, readFile, rm } = require('node:fs/promises');
const { join } = require('node:path');
const { tmpdir } = require('node:os');
const { once } = require('node:events');
const net = require('node:net');
const { TelemetrySession } = require('../src/session.cjs');
const { initialState, normalizeState } = require('../src/config.cjs');

test('refresh follows the display by default and supports manual limits above 60 FPS', () => {
  assert.equal(initialState().fps, 0);
  assert.equal(normalizeState({ fps: 30 }).fps, 0);
  assert.equal(normalizeState({ fps: 30, refreshVersion: 1 }).fps, 30);
  assert.equal(normalizeState({ fps: 144 }).fps, 144);
});

function storage() {
  try {
    return require('../src/samples.cjs');
  } catch (error) {
    if (error.code !== 'MODULE_NOT_FOUND') throw error;
    assert.fail('Sample storage module is not implemented');
  }
}

test('preserves aligned samples across channel expansion, wraparound and resize', () => {
  const { SampleRing } = storage();
  const ring = new SampleRing(3);
  ring.push([1]);
  ring.push([2, 20]);
  assert.deepEqual(ring.slice(0, 2).columns, [[1, 2], [null, 20]]);
  ring.push([3]);
  ring.push([4, 40]);
  assert.deepEqual(ring.slice(0, 4).columns, [[2, 3, 4], [20, null, 40]]);
  ring.resize(2);
  assert.deepEqual(ring.slice(0, 4).columns, [[3, 4], [null, 40]]);
});

test('keeps a one-sample positive and negative excursion when reducing a viewport', () => {
  const { SampleRing } = storage();
  const ring = new SampleRing(600);
  for (let i = 0; i < 600; i++) ring.push([i === 131 ? 37 : i === 183 ? -39 : 0]);
  const series = ring.slice(0, 600, 60);
  assert.ok(series.columns[0].length <= 60);
  assert.equal(series.columns[0][series.indices.indexOf(131)], 37);
  assert.equal(series.columns[0][series.indices.indexOf(183)], -39);
});

test('round-trips VOFA headers and raw numbers, with the time column opt-in', () => {
  const { parseCsv, snapshotCsv, SampleRing } = storage();
  const ring = new SampleRing(3);
  ring.push([1.25, -1e-8]);
  ring.push([3, NaN]);
  const text = [...snapshotCsv(ring, ['航向', 'X, Y'], 10)].join('');
  assert.deepEqual(parseCsv(text), { names: ['航向', 'X, Y'], rows: [[1.25, -1e-8], [3, NaN]], dtMs: undefined });
  const timed = [...snapshotCsv(ring, ['航向', 'X, Y'], 10, true)].join('');
  assert.equal(parseCsv(timed).dtMs, 10);
});

test('keeps every recording row after the live ring would have wrapped', async (t) => {
  const { CsvRecorder } = storage();
  const dir = await mkdtemp(join(tmpdir(), 'vofa-recording-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const path = join(dir, 'record.csv');
  const recorder = new CsvRecorder(path, ['I0'], 10, () => {});
  await recorder.ready;
  for (let i = 0; i < 8; i++) recorder.append([i], i);
  await recorder.close();
  const content = await readFile(path, 'utf8');
  assert.match(content, /^time_ms,I0\r?\n0,0/);
  assert.match(content, /70,7\r?\n$/);
});

test('loading a larger CSV retains all rows and expands the session buffer', async t => {
  const session = new TelemetrySession({ ...initialState(), capacity: 2 });
  t.after(() => session.clearSamples());
  await session.loadCsv({ names: ['I0'], rows: [[1], [2], [3], [4]] });
  assert.equal(session.state.capacity, 4);
  assert.deepEqual(session.ring.slice(0, 4).columns, [[1, 2, 3, 4]]);
});

test('switching TCP peer cancels repetitions to avoid redirecting a device command', async t => {
  const session = new TelemetrySession(initialState());
  t.after(() => session.disconnect());
  await session.connect({ kind: 'tcpServer', host: '127.0.0.1', port: 0, engine: 'rawdata' });
  const { port } = session.transport.address();
  const a = net.connect(port, '127.0.0.1');
  t.after(() => a.destroy());
  await once(a, 'connect');
  const b = net.connect(port, '127.0.0.1');
  t.after(() => b.destroy());
  await once(b, 'connect');
  const first = once(a, 'data');
  const job = session.sendFrames([{ bytes: Buffer.from('A') }], { count: 2, delayMs: 100 });
  assert.equal((await first)[0].toString(), 'A');
  session.selectPeer('127.0.0.1:' + b.localPort);
  await job;
  assert.equal(session.stats.tx, 1);
});
