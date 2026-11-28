const dgram = require('node:dgram');
const { once } = require('node:events');
const { readdir, readFile } = require('node:fs/promises');
const net = require('node:net');
const { join } = require('node:path');

function call(operation) {
  return new Promise((resolve, reject) => operation(error => error ? reject(error) : resolve()));
}

function managedClose(close) {
  let pending;
  return () => pending ??= Promise.resolve().then(close);
}

function portNumber(value, fallback, allowZero = false) {
  const port = Number(value ?? fallback);
  if (!Number.isInteger(port) || port < Number(!allowZero) || port > 65535) {
    throw new Error('Port must be an integer in the valid range.');
  }
  return port;
}

function socketWriter(socket, data) {
  if (socket.destroyed || !socket.writable) return Promise.reject(new Error('TCP connection is closed.'));
  return call(done => socket.write(data, done));
}

async function listPorts() {
  return require('serialport').SerialPort.list();
}

async function openSerial(config, callbacks) {
  const { SerialPort } = require('serialport');
  const software = config.flowControl === 'software';
  const hardware = config.flowControl === 'hardware';
  const port = new SerialPort({
    path: config.path,
    baudRate: Number(config.baudRate ?? 115200),
    dataBits: Number(config.dataBits ?? 8),
    parity: config.parity || 'none',
    stopBits: Number(config.stopBits ?? 1),
    rtscts: hardware,
    xon: software,
    xoff: software,
    autoOpen: false
  });
  const signals = { dtr: false, rts: false, brk: false };
  let ready = false;
  port.on('data', bytes => callbacks.onData?.(bytes, ''));
  port.on('error', error => { if (ready) callbacks.onError?.(error); });
  port.once('close', () => { if (ready) callbacks.onClose?.(); });

  await call(done => port.open(done));
  const close = managedClose(() => port.isOpen ? call(done => port.close(done)) : undefined);
  let pendingSignals = Promise.resolve();
  function setSignals(next) {
    const apply = pendingSignals.then(async () => {
      const updated = { ...signals };
      for (const name of Object.keys(signals)) {
        if (next[name] !== undefined) updated[name] = Boolean(next[name]);
      }
      const { rts, ...other } = updated;
      await call(done => port.set(hardware ? other : { ...other, rts }, done));
      Object.assign(signals, updated);
    });
    pendingSignals = apply.catch(() => {});
    return apply;
  }
  try {
    if (config.dtr !== undefined || config.rts !== undefined || config.brk !== undefined) {
      await setSignals(config);
    }
  } catch (error) {
    await close().catch(() => {});
    throw error;
  }
  ready = true;
  return {
    close,
    setSignals,
    async write(data) {
      if (!port.isOpen) throw new Error('Serial port is closed.');
      await call(done => port.write(data, done));
      await call(done => port.drain(done));
    }
  };
}

async function openTcpClient(config, callbacks) {
  const port = portNumber(config.port, 9000);
  const socket = new net.Socket();
  const closed = new Promise(resolve => socket.once('close', resolve));
  let ready = false;
  socket.setNoDelay(true);
  socket.on('data', bytes => callbacks.onData?.(bytes, ''));
  socket.on('error', error => { if (ready) callbacks.onError?.(error); });
  socket.once('close', () => { if (ready) callbacks.onClose?.(); });
  const timeout = setTimeout(() => socket.destroy(new Error('TCP connection timed out.')), 10000);
  const connected = once(socket, 'connect');
  try {
    socket.connect({ host: config.host || '127.0.0.1', port });
    await connected;
    if (config.banner) await socketWriter(socket, Buffer.from(config.banner, 'utf8'));
  } catch (error) {
    socket.destroy();
    throw error;
  } finally {
    clearTimeout(timeout);
  }
  ready = true;
  return {
    address: () => socket.address(),
    write: data => socketWriter(socket, data),
    close: managedClose(() => {
      socket.destroy();
      return closed;
    })
  };
}

function peerList(callbacks) {
  const entries = new Map();
  let selected = '';
  function peers() {
    return Array.from(entries.keys(), id => ({ id, label: id }));
  }
  function announce() {
    callbacks.onPeers?.(peers(), selected);
  }
  return {
    peers,
    has: id => entries.has(id),
    getSelected: () => entries.get(selected),
    values: () => entries.values(),
    add(id, endpoint) {
      entries.set(id, endpoint);
      if (!selected) selected = id;
      announce();
    },
    remove(id) {
      if (!entries.delete(id)) return;
      if (selected === id) selected = entries.keys().next().value ?? '';
      announce();
    },
    clear() {
      if (!entries.size) return;
      entries.clear();
      selected = '';
      announce();
    },
    selectPeer(id) {
      if (!entries.has(id) || id === selected) return;
      selected = id;
      announce();
    }
  };
}

function endpointId(host, port) {
  return host.includes(':') ? '[' + host + ']:' + port : host + ':' + port;
}

async function openTcpServer(config, callbacks) {
  const port = portNumber(config.port, 9000, true);
  const members = peerList(callbacks);
  const server = net.createServer(socket => {
    socket.setNoDelay(true);
    const id = endpointId(socket.remoteAddress, socket.remotePort);
    socket.on('data', bytes => callbacks.onData?.(bytes, id));
    socket.on('error', error => callbacks.onError?.(error));
    socket.once('close', () => members.remove(id));
    members.add(id, socket);
  });
  let ready = false;
  server.on('error', error => { if (ready) callbacks.onError?.(error); });
  server.once('close', () => { if (ready) callbacks.onClose?.(); });
  const listening = once(server, 'listening');
  server.listen(port, config.host || config.localHost || '0.0.0.0');
  await listening;
  ready = true;
  return {
    peers: members.peers,
    selectPeer: members.selectPeer,
    address: () => server.address(),
    async write(data) {
      const socket = members.getSelected();
      if (!socket) throw new Error('No TCP peer is connected.');
      await socketWriter(socket, data);
    },
    close: managedClose(async () => {
      const closed = call(done => server.close(done));
      for (const socket of members.values()) socket.destroy();
      await closed;
    })
  };
}

async function openUdp(config, callbacks) {
  const localPort = portNumber(config.localPort, 9000, true);
  const remotePort = portNumber(config.remotePort, 0, true);
  const remoteHost = config.remoteHost || '127.0.0.1';
  const ipv6 = net.isIP(config.localHost) === 6 || net.isIP(remoteHost) === 6;
  const socket = dgram.createSocket(ipv6 ? 'udp6' : 'udp4');
  const members = peerList(callbacks);
  let ready = false;
  let closed = false;
  socket.on('error', error => { if (ready) callbacks.onError?.(error); });
  socket.once('close', () => {
    closed = true;
    members.clear();
    if (ready) callbacks.onClose?.();
  });
  socket.on('message', (bytes, info) => {
    const id = endpointId(info.address, info.port);
    if (!members.has(id)) members.add(id, info);
    callbacks.onData?.(bytes, id);
  });
  const listening = once(socket, 'listening');
  try {
    socket.bind(localPort, config.localHost || (ipv6 ? '::' : '0.0.0.0'));
    await listening;
  } catch (error) {
    try { socket.close(); } catch {}
    throw error;
  }
  ready = true;
  // A configured remote is the initial TX destination, even before it replies.
  if (remotePort) members.add(endpointId(remoteHost, remotePort), { address: remoteHost, port: remotePort });
  return {
    peers: members.peers,
    selectPeer: members.selectPeer,
    address: () => socket.address(),
    async write(data) {
      if (closed) throw new Error('UDP socket is closed.');
      const peer = members.getSelected();
      if (!peer) throw new Error('Set a remote UDP endpoint or wait for a datagram.');
      await call(done => socket.send(data, peer.port, peer.address, done));
    },
    close: managedClose(() => closed ? undefined : new Promise(resolve => socket.close(resolve)))
  };
}

function sampleValue(signal, phase) {
  const cycle = phase / (2 * Math.PI) % 1;
  switch (signal) {
    case 'square': return cycle < 0.5 ? 1 : -1;
    case 'saw':
    case 'sawtooth': return 2 * cycle - 1;
    case 'triangle': return 1 - 4 * Math.abs(cycle - 0.5);
    case 'noise':
    case 'random': return 2 * Math.random() - 1;
    default: return Math.sin(phase);
  }
}

async function openDemo(config, callbacks) {
  const { encodeSample, encodeImage } = require('./protocol.cjs');
  const engine = String(config.engine ?? 'justfloat').toLowerCase();
  const signal = String(config.demoSignal ?? 'sine').toLowerCase();
  const period = Math.max(1, Math.min(Number(config.demoMs) || 50, 3600000));
  const channels = Math.max(1, Math.min(Math.trunc(Number(config.demoChannels)) || 3, 256));
  const pictures = ['image', 'jpg', 'jpeg'].includes(signal);
  const files = pictures || signal === 'csv' ? (await readdir(config.demoDir, { withFileTypes: true }))
    .filter(entry => entry.isFile() && (pictures ? /\.jpe?g$/i : /\.csv$/i).test(entry.name))
    .map(entry => entry.name)
    .sort((a, b) => a.localeCompare(b, 'en')) : null;
  if (files?.length === 0) throw new Error('The Demo folder contains no supported files.');
  const playback = new AbortController();
  const { signal: abort } = playback;
  let step = 0;
  let timer;

  function deliver(frame) {
    if (engine === 'rawdata' && callbacks.onDemo) callbacks.onDemo(frame);
    else callbacks.onData?.(frame.bytes, '');
  }

  function emitSample(values) {
    deliver({ kind: 'sample', values, bytes: encodeSample(engine, values) });
  }

  async function tick() {
    try {
      if (files) {
        const filename = files[step % files.length];
        const data = await readFile(join(config.demoDir, filename), { signal: abort });
        if (abort.aborted) return;
        if (pictures) {
          const image = { id: 0, width: -1, height: -1, format: 27, data };
          const bytes = engine === 'rawdata' ? data : encodeImage(engine, image);
          deliver({ kind: 'image', ...image, bytes });
        } else {
          const { parseCsv } = require('./samples.cjs');
          const csv = data.toString('utf8').replace(/^\uFEFF?\s*(?:"(?:time|timestamp)"|time|timestamp)(?=\s*,)/i, 'time_ms');
          const { rows } = parseCsv(csv);
          let skipped = 0;
          for (const values of rows) {
            if (values.length && values.every(Number.isFinite)) emitSample(values);
            else skipped++;
          }
          if (skipped) callbacks.onError?.(new Error(filename + ' skipped ' + skipped + ' non-finite CSV sample(s).'));
        }
      } else {
        const phase = step * period * 2 * Math.PI / 2000;
        emitSample(Array.from({ length: channels }, (_, i) => sampleValue(signal, phase + i * 0.6)));
      }
    } catch (error) {
      if (!abort.aborted) callbacks.onError?.(error);
    } finally {
      step++;
      if (!abort.aborted) timer = setTimeout(tick, period);
    }
  }

  timer = setTimeout(tick, period);
  return {
    async write() { throw new Error('Demo is a read-only input.'); },
    close: managedClose(() => {
      playback.abort();
      clearTimeout(timer);
      callbacks.onClose?.();
    })
  };
}

async function openTransport(config, callbacks = {}) {
  switch (config.kind) {
    case 'serial': return openSerial(config, callbacks);
    case 'tcpClient': return openTcpClient(config, callbacks);
    case 'tcpServer': return openTcpServer(config, callbacks);
    case 'udp': return openUdp(config, callbacks);
    case 'demo': return openDemo(config, callbacks);
    default: throw new Error('Unknown transport type: ' + config.kind);
  }
}

module.exports = { listPorts, openTransport };
