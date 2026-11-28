const assert = require('node:assert/strict');
const { once } = require('node:events');
const { mkdtemp, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { listPorts, openTransport } = require('../src/transport.cjs');
const { createDecoder } = require('../src/protocol.cjs');

function watch() {
  const queue = [];
  const readers = [];
  return {
    push(value) {
      if (readers.length) readers.shift()(value);
      else queue.push(value);
    },
    next() {
      return queue.length ? Promise.resolve(queue.shift()) : new Promise(resolve => readers.push(resolve));
    }
  };
}

test('TCP server exposes separate peers and routes writes to the selected peer', { timeout: 5000 }, async t => {
  const serverRx = watch();
  const updates = watch();
  const server = await openTransport({ kind: 'tcpServer', host: '127.0.0.1', port: 0 }, {
    onData: (bytes, peerId) => serverRx.push([bytes.toString(), peerId]),
    onPeers: (peers, selected) => updates.push({ peers, selected })
  });
  t.after(() => server.close());

  const connect = async rx => {
    const client = await openTransport({ kind: 'tcpClient', host: '127.0.0.1', port: server.address().port }, {
      onData: bytes => rx.push(bytes.toString())
    });
    t.after(() => client.close());
    return client;
  };

  const firstRx = watch();
  const first = await connect(firstRx);
  const secondRx = watch();
  const second = await connect(secondRx);
  await first.write(Buffer.from('first'));
  await second.write(Buffer.from('second'));
  const received = new Map([await serverRx.next(), await serverRx.next()]);
  assert.notEqual(received.get('first'), received.get('second'));
  assert.equal(server.peers().length, 2);
  server.selectPeer(received.get('second'));
  await server.write(Buffer.from('to second'));
  assert.equal(await secondRx.next(), 'to second');

  let state;
  do {
    state = await updates.next();
  } while (state.peers.length !== 2 || state.selected !== received.get('second'));
  await second.close();
  do {
    state = await updates.next();
  } while (state.peers.length !== 1 || state.selected !== received.get('first'));
  await server.write(Buffer.from('to first'));
  assert.equal(await firstRx.next(), 'to first');
});

test('closing a listening TCP transport also closes connected clients', { timeout: 5000 }, async t => {
  const server = await openTransport({ kind: 'tcpServer', host: '127.0.0.1', port: 0 });
  t.after(() => server.close());
  const closeEvents = watch();
  const client = await openTransport({ kind: 'tcpClient', host: '127.0.0.1', port: server.address().port }, {
    onClose: () => closeEvents.push(true)
  });
  t.after(() => client.close());
  await server.close();
  assert.equal(await closeEvents.next(), true);
  await assert.rejects(client.write(Buffer.from('closed')));
});

test('UDP attributes datagrams from two loopback peers to different sources', { timeout: 5000 }, async t => {
  const rx = watch();
  const receiver = await openTransport({ kind: 'udp', localHost: '127.0.0.1', localPort: 0 }, {
    onData: (bytes, peerId) => rx.push([bytes.toString(), peerId])
  });
  t.after(() => receiver.close());
  const senders = [];
  for (let i = 0; i < 2; i++) {
    const incoming = watch();
    const sender = await openTransport({
      kind: 'udp', localHost: '127.0.0.1', localPort: 0, remoteHost: '127.0.0.1', remotePort: receiver.address().port
    }, { onData: bytes => incoming.push(bytes.toString()) });
    t.after(() => sender.close());
    senders.push(incoming);
    await sender.write(Buffer.from(String(i)));
  }
  const datagrams = new Map([await rx.next(), await rx.next()]);
  assert.notEqual(datagrams.get('0'), datagrams.get('1'));
  receiver.selectPeer(datagrams.get('1'));
  await receiver.write(Buffer.from('reply'));
  assert.equal(await senders[1].next(), 'reply');
});

test('serial enumeration uses the native binding and failed open rejects', async () => {
  const ports = await listPorts();
  assert.ok(Array.isArray(ports));
  for (const port of ports) assert.equal(typeof port.path, 'string');
  await assert.rejects(openTransport({ kind: 'serial', path: '__vofa_missing_port__', baudRate: 100000 }));
});

test('Demo emits configured channel count using the selected engine', { timeout: 5000 }, async t => {
  const decoder = createDecoder('justfloat');
  const frames = watch();
  const transport = await openTransport({ kind: 'demo', demoMs: 5, demoSignal: 'sine', demoChannels: 3, engine: 'justfloat' }, {
    onData: bytes => decoder.push(bytes).forEach(frames.push)
  });
  t.after(() => transport.close());
  const first = await frames.next();
  const second = await frames.next();
  assert.equal(first.kind, 'sample');
  assert.equal(first.values.length, 3);
  assert.notDeepEqual(first.values, second.values);
});

test('CSV Demo processes files in filename order including headerless samples', { timeout: 5000 }, async t => {
  const dir = await mkdtemp(join(tmpdir(), 'vofa-demo-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  await Promise.all([
    writeFile(join(dir, 'b.csv'), '5,6\n'),
    writeFile(join(dir, 'a.csv'), '\ufeffTime,I0,I1\r\n0,1,2\r\n1,3,4\r\n')
  ]);
  const frames = watch();
  const decoder = createDecoder('firewater');
  const transport = await openTransport({ kind: 'demo', demoMs: 5, demoSignal: 'csv', demoDir: dir, engine: 'firewater' }, {
    onData: bytes => decoder.push(bytes).forEach(frames.push)
  });
  try {
    assert.deepEqual((await frames.next()).values, [1, 2]);
    assert.deepEqual((await frames.next()).values, [3, 4]);
    assert.deepEqual((await frames.next()).values, [5, 6]);
  } finally {
    await transport.close();
  }
});

test('JPG Demo preserves file bytes and their filename order', { timeout: 5000 }, async t => {
  const dir = await mkdtemp(join(tmpdir(), 'vofa-images-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const first = Buffer.from([0xff, 0xd8, 1, 0xff, 0xd9]);
  const second = Buffer.from([0xff, 0xd8, 2, 0xff, 0xd9]);
  await Promise.all([
    writeFile(join(dir, 'z.jpg'), second),
    writeFile(join(dir, 'a.JPEG'), first)
  ]);
  const frames = watch();
  const decoder = createDecoder('justfloat');
  const transport = await openTransport({ kind: 'demo', demoMs: 5, demoSignal: 'jpg', demoDir: dir, engine: 'justfloat' }, {
    onData: bytes => decoder.push(bytes).forEach(frames.push)
  });
  try {
    assert.deepEqual((await frames.next()).data, first);
    assert.deepEqual((await frames.next()).data, second);
  } finally {
    await transport.close();
  }
});


test('serial signal updates preserve other flags and writes drain through the binding', async t => {
  const { SerialPort, SerialPortMock } = require('serialport');
  const { binding } = SerialPortMock;
  const path = '/dev/vofa-serial-test';
  binding.createPort(path, { record: true });
  let opened;
  const signals = [];
  t.mock.method(SerialPort.binding, 'open', async options => {
    opened = await binding.open(options);
    t.mock.method(opened, 'set', async options => { signals.push(options); });
    return opened;
  });

  const transport = await openTransport({ kind: 'serial', path, flowControl: 'software', dtr: true });
  t.after(() => transport.close());
  assert.equal(opened.openOptions.xon, true);
  assert.equal(opened.openOptions.xoff, true);
  assert.equal(opened.openOptions.rtscts, false);

  await Promise.all([transport.setSignals({ rts: true }), transport.setSignals({ brk: true })]);
  assert.deepEqual(signals.map(({ dtr, rts, brk }) => [dtr, rts, brk]), [
    [true, false, false], [true, true, false], [true, true, true]
  ]);
  await transport.write(Buffer.from([1, 2, 3]));
  assert.deepEqual(opened.recording, Buffer.from([1, 2, 3]));
});

test('TCP client sends the configured handshake on connection', { timeout: 5000 }, async t => {
  const rx = watch();
  const server = await openTransport({ kind: 'tcpServer', host: '127.0.0.1', port: 0 }, {
    onData: bytes => rx.push(bytes.toString())
  });
  t.after(() => server.close());
  const client = await openTransport({
    kind: 'tcpClient', host: '127.0.0.1', port: server.address().port, banner: 'HELLO\r\n'
  });
  t.after(() => client.close());
  assert.equal(await rx.next(), 'HELLO\r\n');
});

test('RawData Demo delivers one direct sample when a frame callback is provided', { timeout: 5000 }, async t => {
  const frames = watch();
  const raw = [];
  const transport = await openTransport({ kind: 'demo', demoMs: 5, demoSignal: 'sine', demoChannels: 2, engine: 'rawdata' }, {
    onDemo: frames.push,
    onData: bytes => raw.push(bytes)
  });
  t.after(() => transport.close());
  const frame = await frames.next();
  assert.equal(frame.kind, 'sample');
  assert.equal(frame.values.length, 2);
  assert.ok(frame.bytes.length > 0);
  assert.deepEqual(raw, []);
});
