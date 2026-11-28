const { EventEmitter } = require('node:events');
const { createReadStream } = require('node:fs');
const { createDecoder } = require('./protocol.cjs');
const { openTransport } = require('./transport.cjs');
const { repeatSend } = require('./commands.cjs');
const { SampleRing } = require('./samples.cjs');
const { CaptureFile } = require('./capture.cjs');
const { normalizeState, channelAt, bounded } = require('./config.cjs');

const MAX_LOG_BYTES = 2 * 1024 * 1024;
const MAX_IMAGE_BYTES = 24 * 1024 * 1024;

class TelemetrySession extends EventEmitter {
  constructor(state, decoders = new Map()) {
    super();
    this.state = normalizeState(state);
    this.ring = new SampleRing(this.state.capacity);
    this.customDecoders = decoders;
    this.decoders = new Map();
    this.images = new Map();
    this.log = [];
    this.logBytes = this.logSeq = this.imageSeq = this.version = 0;
    this.stats = { rx: 0, tx: 0, samples: 0, images: 0, invalid: 0 };
    this.status = 'idle';
    this.peers = [];
    this.queue = Promise.resolve();
    this.sends = Promise.resolve();
    this.sendAbort = new AbortController();
  }

  configure(state) {
    this.state = normalizeState(state);
    this.ring.resize(this.state.capacity);
  }

  get channels() {
    const known = Object.keys(this.state.channelSettings).map(Number).filter(i => Number.isInteger(i) && i >= 0 && i < 4096);
    const count = Math.max(this.ring.columns.length, this.state.connection.expectedChannels || 0, ...known.map(i => i + 1));
    return Array.from({ length: Math.min(count, 4096) }, (_, i) => channelAt(i, this.state.channelSettings));
  }

  lifecycle(action) {
    this.queue = this.queue.catch(() => {}).then(action);
    return this.queue;
  }

  connect(config) {
    return this.lifecycle(async () => {
      await this.closeTransport();
      this.status = 'connecting';
      this.connection = { ...config };
      this.error = '';
      this.stats = { rx: 0, tx: 0, samples: 0, images: 0, invalid: 0 };
      const generation = Symbol('connection');
      this.generation = generation;
      let disconnected = false;
      try {
        this.transport = await openTransport(config, {
          onData: (bytes, peer) => {
            if (this.generation === generation) this.receive(bytes, peer);
          },
          onDemo: frame => {
            if (this.generation !== generation) return;
            this.stats.rx += frame.bytes?.length ?? 0;
            this.consume(frame, 'demo');
          },
          onError: error => {
            if (this.generation === generation) this.report(error);
          },
          onClose: () => {
            if (this.generation !== generation) return;
            disconnected = true;
            this.transport = null;
            this.status = 'idle';
            this.abortSending();
            this.flushDecoders();
            this.stopRecording();
            this.flushLog();
            this.peers = [];
            this.peer = '';
          },
          onPeers: (peers, selectedId) => {
            if (this.generation === generation) {
              if (config.kind === 'tcpServer') {
                const connected = new Set(peers.map(peer => peer.id));
                for (const peer of this.peers) {
                  if (connected.has(peer.id)) continue;
                  this.flushDecoder(peer.id);
                }
              }
              this.peers = peers;
              if (this.peer !== selectedId) this.abortSending();
              this.peer = selectedId;
            }
          }
        });
        if (disconnected) {
          await this.closeTransport();
          throw new Error('Connection closed during setup');
        }
        this.status = 'connected';
      } catch (error) {
        this.status = 'idle';
        this.report(error);
        throw error;
      }
    });
  }

  disconnect() {
    this.abortSending();
    return this.lifecycle(() => this.closeTransport());
  }

  async closeTransport() {
    this.abortSending();
    const transport = this.transport;
    this.transport = null;
    this.generation = null;
    try {
      await transport?.close();
    } finally {
      this.flushDecoders();
      this.stopRecording();
      this.flushLog();
      this.status = 'idle';
      this.peers = [];
      this.peer = '';
    }
  }

  receive(bytes, peer = '') {
    this.stats.rx += bytes.length;
    try {
      if (!this.decoders.has(peer)) {
        if (this.decoders.size >= 128) this.decoders.delete(this.decoders.keys().next().value);
        const engine = this.connection.engine;
        const options = { expectedChannels: this.connection.expectedChannels || undefined };
        const create = this.customDecoders.get(engine);
        this.decoders.set(peer, create ? create(options) : createDecoder(engine, options));
      }
      const decoder = this.decoders.get(peer);
      for (const frame of decoder.push(bytes)) this.consume(frame, peer);
    } catch (error) {
      this.stats.invalid++;
      this.appendLog('rx', 'invalid', bytes, peer);
      this.report(error);
    }
  }

  flushDecoders() {
    for (const peer of this.decoders.keys()) this.flushDecoder(peer);
  }

  flushDecoder(peer) {
    const decoder = this.decoders.get(peer);
    if (!decoder) return;
    this.decoders.delete(peer);
    try {
      for (const frame of decoder.flush?.() ?? []) this.consume(frame, peer);
    } catch (error) {
      this.report(error);
    }
  }

  consume(frame, peer) {
    if (frame.kind === 'sample') {
      this.ring.push(frame.values);
      this.stats.samples++;
      this.version++;
      this.captureSample(frame.values);
      if (this.recording?.active) this.recording.capture.append(frame.values);
    } else if (frame.kind === 'image') {
      this.stats.images++;
      this.images.delete(frame.id);
      this.images.set(frame.id, {
        id: frame.id, width: frame.width, height: frame.height, format: frame.format,
        data: frame.data.toString('base64'), seq: ++this.imageSeq, size: frame.data.length
      });
      let size = [...this.images.values()].reduce((total, item) => total + item.size, 0);
      while ((size > MAX_IMAGE_BYTES || this.images.size > 32) && this.images.size > 1) {
        const oldest = this.images.keys().next().value;
        size -= this.images.get(oldest).size;
        this.images.delete(oldest);
      }
    } else if (frame.kind === 'invalid') {
      this.stats.invalid++;
    }
    const { showPlotData, showImageData } = this.state.terminal;
    if (frame.bytes?.length && (frame.kind !== 'sample' || showPlotData) && (frame.kind !== 'image' || showImageData)) {
      this.appendLog('rx', frame.kind, frame.bytes, peer);
    }
  }

  appendLog(direction, kind, bytes, peer = '') {
    const delay = direction === 'rx' && kind === 'raw' ? bounded(this.state.terminal.groupMs, 0, 2000, 20) : 0;
    const entry = { direction, kind, bytes: Buffer.from(bytes.subarray(0, 16384)), length: bytes.length, peer, time: Date.now() };
    if (delay) {
      const previous = this.pendingLog;
      if (previous?.peer === peer && previous.length + bytes.length <= 16384) {
        entry.bytes = Buffer.concat([previous.bytes, bytes]);
        entry.length += previous.length;
        entry.time = previous.time;
      } else {
        this.flushLog();
      }
      clearTimeout(this.logTimer);
      this.pendingLog = entry;
      this.logTimer = setTimeout(() => this.flushLog(), delay);
      this.logTimer.unref?.();
    } else {
      this.flushLog();
      this.saveLog(entry);
    }
  }

  flushLog() {
    clearTimeout(this.logTimer);
    if (this.pendingLog) {
      this.saveLog(this.pendingLog);
      this.pendingLog = null;
    }
  }

  saveLog(entry) {
    this.log.push({ ...entry, seq: ++this.logSeq });
    this.logBytes += entry.bytes.length;
    while (this.log.length > 8000 || this.logBytes > MAX_LOG_BYTES) this.logBytes -= this.log.shift().bytes.length;
  }

  clearLog() {
    this.pendingLog = null;
    this.log = [];
    this.logBytes = 0;
    this.logClearedAt = ++this.logSeq;
  }

  report(error) {
    this.error = error instanceof Error ? error.message : String(error);
    this.appendLog('sys', 'error', Buffer.from(this.error, 'utf8'));
  }

  async clearSamples() {
    const previous = this.capture;
    this.capture = undefined;
    if (this.error === this.captureError) this.error = '';
    this.captureError = '';
    this.ring.clear();
    this.images.clear();
    this.imageSeq++;
    this.version++;
    this.stats.samples = this.stats.images = 0;
    await previous?.dispose();
  }

  async loadCsv(data) {
    if (data.rows.length > SampleRing.maxCapacity(data.names.length)) {
      throw new Error('CSV exceeds the sample buffer limit for ' + data.names.length + ' channels');
    }
    await this.clearSamples();
    this.state.capacity = Math.max(this.state.capacity, data.rows.length);
    this.ring.resize(this.state.capacity, data.names.length);
    this.state.channelSettings = Object.fromEntries(data.names.map((name, i) => [i, { name }]));
    if (data.dtMs) this.state.dtMs = data.dtMs;
    for (const row of data.rows) {
      this.ring.push(row);
      this.captureSample(row);
    }
    this.version++;
  }

  captureSample(values) {
    if (this.captureError) return;
    try {
      if (!this.capture) {
        const capture = new CaptureFile(error => {
          if (this.capture !== capture) return;
          this.captureError = error.message;
          this.report(error);
        });
        this.capture = capture;
      }
      this.capture.append(values);
    } catch (error) {
      this.captureError = error.message;
      this.report(error);
    }
  }

  async saveCsv(path, time = false) {
    if (this.captureError) throw new Error(this.captureError);
    if (!this.capture?.count) throw new Error('没有可保存的采样数据');
    const names = this.channels.map(channel => channel.name);
    await this.capture.save(path, names, this.state.dtMs, time);
  }

  get recordingStatus() {
    if (!this.recording) return 'idle';
    return this.recording.active ? 'active' : 'pending';
  }

  startRecording() {
    if (this.status !== 'connected') throw new Error('请先连接设备。');
    if (this.recording) throw new Error('当前记录尚未保存。');
    const capture = new CaptureFile(error => {
      if (this.recording?.capture !== capture) return;
      this.recording = undefined;
      this.report(error);
      void capture.dispose().catch(error => this.report(error));
    });
    this.recording = { capture, active: true, dtMs: this.state.dtMs };
  }

  stopRecording() {
    const recording = this.recording;
    if (!recording?.active) return !!recording;
    const { capture } = recording;
    capture.finish();
    if (!capture.count) {
      this.recording = undefined;
      void capture.dispose().catch(error => this.report(error));
      return false;
    }
    recording.active = false;
    recording.names = Array.from({ length: capture.width }, (_, i) => channelAt(i, this.state.channelSettings).name);
    return true;
  }

  async saveRecording(path) {
    const recording = this.recording;
    if (!recording || recording.active) throw new Error('没有可保存的采样数据');
    await recording.capture.save(path, recording.names, recording.dtMs, true);
    this.recording = undefined;
    await recording.capture.dispose().catch(error => this.report(error));
  }

  async dispose() {
    try {
      await this.disconnect();
      await this.clearSamples();
    } finally {
      const recording = this.recording;
      this.recording = undefined;
      await recording?.capture.dispose();
    }
  }

  sendFrames(frames, options) {
    return this.sendJob(async (write, signal) => {
      for (const frame of frames) {
        if (signal.aborted) return;
        await repeatSend(() => write(frame.bytes), {
          count: frame.count ?? options.count, delayMs: frame.delayMs ?? options.delayMs, signal
        });
      }
    });
  }

  sendFile(path) {
    return this.sendJob(async (write, signal) => {
      if (signal.aborted) return;
      const stream = createReadStream(path, { highWaterMark: 8192, signal });
      for await (const bytes of stream) await write(bytes);
    });
  }

  sendJob(job) {
    const transport = this.transport;
    if (!transport || this.status !== 'connected') return Promise.reject(new Error('Connect a device first'));
    if (this.pendingSends >= 128) return Promise.reject(new Error('Send queue is full. Cancel pending sends or wait for them to finish.'));
    const { signal } = this.sendAbort;
    this.pendingSends = (this.pendingSends ?? 0) + 1;
    const write = async bytes => {
      if (signal.aborted || transport !== this.transport) return;
      await transport.write(bytes);
      if (transport !== this.transport) return;
      this.stats.tx += bytes.length;
      this.appendLog('tx', 'raw', bytes);
    };
    const result = this.sends.catch(() => {}).then(() => job(write, signal)).catch(error => {
      if (!signal.aborted) throw error;
    }).finally(() => this.pendingSends--);
    this.sends = result;
    return result;
  }

  abortSending() {
    this.sendAbort.abort();
    this.sendAbort = new AbortController();
  }

  selectPeer(id) {
    this.transport?.selectPeer?.(id);
  }

  async setSignals(signals) {
    if (!this.transport?.setSignals) throw new Error('Open a serial connection to control DTR, RTS or Break');
    await this.transport.setSignals(signals);
  }
}

module.exports = { TelemetrySession };
