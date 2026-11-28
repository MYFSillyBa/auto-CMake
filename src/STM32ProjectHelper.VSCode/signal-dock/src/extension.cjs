const vscode = require('vscode');
const { randomBytes } = require('node:crypto');
const { readFile, stat, writeFile } = require('node:fs/promises');
const { readFileSync } = require('node:fs');
const iconv = require('iconv-lite');
const { TelemetrySession } = require('./session.cjs');
const { listPorts } = require('./transport.cjs');
const { migrateLegacy } = require('./legacy.cjs');
const { encodeCommand } = require('./commands.cjs');
const { parseCsv, SampleRing } = require('./samples.cjs');
const { initialState, normalizeState, bounded, serialPortId, selectSerialPort } = require('./config.cjs');

const STATE_KEY = 'signal-dock.state.v1';
const MAX_CSV_SIZE = 64 * 1024 * 1024;

async function openFile(label, filters, limit = MAX_CSV_SIZE) {
  const [uri] = await vscode.window.showOpenDialog({ openLabel: label, filters }) ?? [];
  if (!uri) return;
  if ((await stat(uri.fsPath)).size > limit) throw new Error('File exceeds the ' + (limit / 1024 / 1024) + ' MB limit');
  return uri;
}

function saveFile(label, name, filters) {
  const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
  return vscode.window.showSaveDialog({
    saveLabel: label, filters, defaultUri: folder ? vscode.Uri.joinPath(folder, name) : vscode.Uri.file(name)
  });
}

function commandBytes(node, params, terminal) {
  const leaf = node.children ? node.children.filter(child => !child.hidden).flatMap(child => commandBytes(child, params, terminal)) : [{
    bytes: encodeCommand({ ...node, encoding: terminal.encoding }, params),
    count: node.count ?? 1, delayMs: node.delayMs ?? 100
  }];
  return leaf;
}

class SignalDock {
  constructor(context, host) {
    this.context = context;
    this.host = host;
    this.decoders = new Map();
    this.savingRecording = false;
    this.state = normalizeState(context.workspaceState.get(STATE_KEY, initialState()));
    this.session = new TelemetrySession(this.state, this.decoders);
    const assets = vscode.Uri.joinPath(context.extensionUri, 'signal-dock', 'dist');
    const read = name => readFileSync(vscode.Uri.joinPath(assets, name).fsPath, 'utf8');
    this.template = read('index.html');
    this.styles = read('app.css').replace(/<\/style/gi, '<\\/style');
    this.script = read('app.js').replace(/<\/script/gi, '<\\/script');
  }

  html(nonce) {
    return this.template
      .replaceAll('__NONCE__', nonce)
      .replaceAll('__LANGUAGE__', this.host.language)
      .replace(/__(STYLE|SCRIPT)__/g, (_, key) => key === 'STYLE' ? this.styles : this.script);
  }

  attach(webview, nonce) {
    if (this.view?.webview === webview) return;
    this.view = { webview, span: this.state.view.span, start: null, log: this.session.logClearedAt ?? 0, image: -1, force: true };
    void webview.postMessage({ type: 'signalsDocument', html: this.html(nonce) });
    this.updateVisibility();
  }

  receive(message) {
    const view = this.view;
    if (!view || !message || typeof message.type !== 'string') return;
    void this.handle(view, message).then(result => {
      if (message.id) return this.post(view, { type: 'result', id: message.id, result });
    }).catch(error => {
      const errorText = this.host.t(error instanceof Error ? error.message : String(error));
      if (message.id) return this.post(view, { type: 'result', id: message.id, error: errorText });
      return this.post(view, { type: 'notice', text: errorText, level: 'error' });
    });
  }

  post(view, payload) {
    return view === this.view ? view.webview.postMessage({ type: 'signalMessage', payload }) : Promise.resolve(false);
  }

  updateVisibility() {
    const view = this.view;
    const visible = !!view && this.host.page === 'signals' && this.host.panel?.visible;
    if (visible && !this.clock) {
      view.force = true;
      view.inFlight = undefined;
      this.clock = setInterval(() => this.tick(), 4);
    } else if (!visible && this.clock) {
      clearInterval(this.clock);
      this.clock = undefined;
    }
  }

  detach() {
    this.view = undefined;
    this.updateVisibility();
    void this.session.disconnect().catch(error => this.host.output.appendLine(error.message));
  }

  init(view) {
    view.ready = true;
    view.force = true;
    view.inFlight = undefined;
    return this.post(view, {
      type: 'init', state: this.state, trusted: vscode.workspace.isTrusted,
      engines: [...this.decoders.keys()], language: this.host.language
    });
  }

  refreshLanguage() {
    if (this.view?.ready) void this.post(this.view, { type: 'language', language: this.host.language });
  }

  async save(value, sender) {
    this.state = normalizeState(value);
    this.session.configure(this.state);
    const view = this.view;
    if (view) {
      if (view.ready && view !== sender) void this.post(view, { type: 'state', state: this.state });
      view.force = true;
    }
    await this.context.workspaceState.update(STATE_KEY, this.state);
  }

  tick() {
    const now = performance.now();
    const session = this.session;
    const ring = session.ring;
    const view = this.view;
    if (!view?.ready || view.busy || view.inFlight !== undefined) return;
    if (!view.force && this.state.fps && now - (view.lastFrame ?? -Infinity) < 1000 / this.state.fps) return;
    const end = view.start == null ? ring.end : Math.min(ring.end, Math.max(view.start, ring.start) + view.span);
    const start = Math.max(ring.start, end - view.span);
    const budget = Math.min(16384, Math.max(256, Math.floor(196608 / Math.max(1, ring.columns.length))));
    const seriesChanged = view.force || (view.seriesStart !== start || view.seriesEnd !== end)
      && now - (view.lastSeries ?? -Infinity) >= (view.seriesCost ?? 0) * 2;
    const latestChanged = view.force || view.version !== session.version;
    const imageChanged = view.image !== session.imageSeq;
    const clear = view.log < (session.logClearedAt ?? 0);
    const after = clear ? session.logClearedAt : view.log;
    const statusKey = [session.status, session.stats.rx, session.stats.tx, session.stats.invalid, session.error,
      session.peer, session.peers.map(peer => peer.id).join(','), session.capture?.count ?? 0,
      session.captureError, session.recordingStatus, session.pendingSends > 0].join('|');
    if (!seriesChanged && !latestChanged && !imageChanged && !clear && after === session.logSeq && statusKey === view.statusKey) return;
    const log = (after === session.logSeq ? [] : session.log.slice(-100).filter(entry => entry.seq > after)).map(entry => {
      const encoding = session.state.terminal.encoding;
      return {
        seq: entry.seq, direction: entry.direction, kind: entry.kind, length: entry.length,
        peer: entry.peer, time: entry.time, hex: entry.bytes.toString('hex'),
        text: iconv.decode(entry.bytes, entry.direction !== 'sys' && iconv.encodingExists(encoding) ? encoding : 'utf8')
      };
    });
    const channelCount = ring.columns.length;
    const channels = (view.force || view.channelCount !== channelCount) ? session.channels : undefined;
    const frameId = (view.frameId ?? 0) + 1;
    const seriesStarted = performance.now();
    const message = {
      type: 'tick', frameId, viewRevision: view.revision, status: session.status, stats: session.stats, count: ring.count,
      start: ring.start, end: ring.end, capacity: ring.capacity, dtMs: session.state.dtMs,
      error: session.error, peers: session.peers, peer: session.peer,
      captured: session.capture?.count ?? 0, recording: session.recordingStatus,
      sending: session.pendingSends > 0, log, clear, channels,
      latest: latestChanged ? (ring.count ? ring.row(ring.end - 1).map(value => Number.isFinite(value) ? value : null) : []) : undefined,
      series: seriesChanged ? ring.slice(start, end, budget) : undefined,
      analysisSeries: seriesChanged && view.analysis && end - start > budget
        ? ring.slice(Math.max(start, end - budget), end) : undefined,
      images: imageChanged ? Object.fromEntries(session.images) : undefined
    };
    const seriesCost = message.series?.indices ? performance.now() - seriesStarted : 0;
    view.frameId = frameId;
    view.inFlight = frameId;
    view.force = false;
    view.busy = true;
    view.lastFrame = now;
    const version = session.version;
    const imageSeq = session.imageSeq;
    void this.post(view, message).then(sent => {
      if (!sent) return;
      if (seriesChanged) {
        view.lastSeries = now;
        view.seriesStart = start;
        view.seriesEnd = end;
        view.seriesCost = seriesCost;
      }
      view.version = version;
      view.statusKey = statusKey;
      view.channelCount = channelCount;
      if (imageChanged) view.image = imageSeq;
      view.log = log.at(-1)?.seq ?? after;
      return true;
    }, () => false).then(sent => {
      if (!sent && view.inFlight === frameId) {
        view.inFlight = undefined;
        view.force = true;
      }
    }).finally(() => { view.busy = false; });
  }

  async demo(view) {
    await this.session.disconnect();
    if (view !== this.view) return;
    const state = normalizeState({ ...this.state, connection: {
      ...this.state.connection, kind: 'demo', engine: 'justfloat', demoSignal: 'sine', demoDir: '', demoChannels: 4, expectedChannels: 0
    }, dtMs: this.state.connection.demoMs });
    await this.save(state);
    if (view === this.view) await this.session.connect(state.connection);
  }

  async importData() {
    const uri = await openFile(this.host.t('载入 CSV'), { CSV: ['csv'] });
    if (!uri) return;
    const data = parseCsv(await readFile(uri.fsPath, 'utf8'));
    if (data.rows.length > SampleRing.maxCapacity(data.names.length)) throw new Error('CSV exceeds the sample buffer limit for ' + data.names.length + ' channels');
    if (this.session.ring.count || this.session.status === 'connected') {
      const label = this.host.t('载入');
      const choice = await vscode.window.showWarningMessage(this.host.t('载入 CSV 会替换当前采样并断开设备。'), { modal: true }, label);
      if (choice !== label) return;
    }
    await this.session.disconnect();
    await this.session.loadCsv(data);
    this.resetViewport();
    const { channelSettings, dtMs, capacity } = this.session.state;
    await this.save({ ...this.state, channelSettings, dtMs, capacity });
  }

  resetViewport() {
    const view = this.view;
    if (!view) return;
    view.start = null;
    view.force = true;
    if (view.ready) void this.post(view, { type: 'resetViewport' });
  }

  async importArchive() {
    const uri = await openFile(this.host.t('载入配置'), { JSON: ['json'] }, 16 * 1024 * 1024);
    if (!uri) return;
    const archive = JSON.parse((await readFile(uri.fsPath, 'utf8')).replace(/^\uFEFF/, ''));
    const parsed = archive?.format === 'signal-dock' && archive.version === 1
      ? archive : migrateLegacy(archive);
    if (!parsed || typeof parsed !== 'object') throw new Error('Unsupported configuration');
    const { kind, value } = parsed;
    const warnings = [...(parsed.warnings ?? [])];
    switch (kind) {
      case 'all': {
        if (!value || typeof value !== 'object' || !Array.isArray(value.tabs) || !Array.isArray(value.commands)) throw new Error('Invalid workspace archive');
        const next = normalizeState(value);
        if (next.plugins.length) warnings.push(this.host.t('导入的自定义控件已停用。可在协议和控件扩展中核对代码并启用。'));
        next.plugins = next.plugins.map(plugin => ({ ...plugin, enabled: false }));
        await this.session.disconnect();
        await this.save(next);
        break;
      }
      case 'commands': await this.save({ ...this.state, commands: value }); break;
      case 'layout': {
        if (!Array.isArray(value) || !value.length) throw new Error('No tabs in layout archive');
        if (archive.type === 'tab') {
          const item = { ...value[0], id: randomBytes(16).toString('hex') };
          await this.save({ ...this.state, tabs: [...this.state.tabs, item], activeTab: item.id });
        } else await this.save({ ...this.state, tabs: value, activeTab: value[0]?.id });
        break;
      }
      case 'tab': await this.save({ ...this.state, tabs: [...this.state.tabs.filter(tab => tab.id !== value.id), value], activeTab: value.id }); break;
      case 'settings': {
        const { theme, appearance, ...settings } = value;
        if (settings.connection) await this.session.disconnect();
        await this.save({ ...this.state, ...settings,
          connection: { ...this.state.connection, ...settings.connection },
          terminal: { ...this.state.terminal, ...settings.terminal },
          appearance: { ...this.state.appearance, ...appearance, ...(theme ? { theme } : {}) }
        });
        break;
      }
      case 'theme':
        await this.save({ ...this.state, themes: [...this.state.themes.filter(theme => theme.id !== value.id), value],
          appearance: { ...this.state.appearance, theme: value.id } });
        break;
      default: throw new Error('Unsupported configuration type');
    }
    return warnings ?? [];
  }

  async exportArchive(message) {
    const { kind, tab } = message;
    const values = {
      all: this.state, commands: this.state.commands, layout: this.state.tabs,
      tab: this.state.tabs.find(item => item.id === tab),
      theme: this.state.themes.find(item => item.id === this.state.appearance.theme),
      settings: {
        connection: this.state.connection, terminal: this.state.terminal, appearance: this.state.appearance,
        channelSettings: this.state.channelSettings, capacity: this.state.capacity, dtMs: this.state.dtMs, fps: this.state.fps
      }
    };
    const value = values[kind];
    if (value === undefined) throw new Error('There is no item to export');
    const uri = await saveFile(this.host.t('导出 JSON'), 'signal-dock-' + kind + '.json', { JSON: ['json'] });
    if (!uri) return false;
    await writeFile(uri.fsPath, JSON.stringify({ format: 'signal-dock', version: 1, kind, value }, null, 2), 'utf8');
    return true;
  }

  async handle(view, message) {
    if (!message || typeof message.type !== 'string') return;
    const session = this.session;
    switch (message.type) {
      case 'ready': return this.init(view);
      case 'demo': return this.demo(view);
      case 'frameAck':
        if (view.inFlight === message.frameId) view.inFlight = undefined;
        return;
      case 'save': return this.save(message.state, view);
      case 'viewport':
        view.span = Math.round(bounded(message.span, 2, 2_000_000, 500));
        view.start = Number.isFinite(message.start) ? Math.max(0, Math.floor(message.start)) : null;
        view.analysis = !!message.analysis;
        view.revision = message.revision;
        view.force = true;
        return;
      case 'ports': return listPorts();
      case 'connect':
        if (this.decoders.has(this.state.connection.engine) && !vscode.workspace.isTrusted) throw new Error('Trust the workspace before running a custom decoder');
        if (this.state.connection.kind === 'serial') {
          const ports = await listPorts();
          const port = selectSerialPort(ports, this.state.connection);
          if (!port) throw new Error(ports.length ? '有多个候选串口，请在顶部选择设备。' : '未检测到串口。连接设备后会自动刷新。');
          const portId = serialPortId(port);
          if (port.path !== this.state.connection.path || portId !== this.state.connection.portId) {
            await this.save({ ...this.state, connection: { ...this.state.connection, path: port.path, portId } });
          }
        }
        if (view === this.view) return session.connect(this.state.connection);
        return;
      case 'disconnect': return session.disconnect();
      case 'peer': return session.selectPeer(message.peer);
      case 'signals': return session.setSignals(message.signals);
      case 'send': {
        const terminal = this.state.terminal;
        const text = terminal.sendHex ? message.text : message.text.replaceAll('%', '%%');
        return session.sendFrames([{ bytes: encodeCommand({ text, hex: terminal.sendHex, eol: terminal.eol, encoding: terminal.encoding }) }], terminal);
      }
      case 'command': {
        const find = commands => {
          for (const command of commands) {
            if (command.id === message.commandId) return command;
            const nested = command.children && find(command.children);
            if (nested) return nested;
          }
        };
        const command = find(this.state.commands);
        if (!command) throw new Error('Command binding is missing');
        return session.sendFrames(commandBytes(command, message.params ?? [], this.state.terminal), this.state.terminal);
      }
      case 'cancelSend': return session.abortSending();
      case 'sendFile': {
        const [file] = await vscode.window.showOpenDialog({ openLabel: this.host.t('发送原始文件') }) ?? [];
        if (file) return session.sendFile(file.fsPath);
        return;
      }
      case 'clearLog': return session.clearLog();
      case 'copyLog':
        if (typeof message.text !== 'string' || message.text.length > 8 * 1024 * 1024) throw new Error('Invalid log selection');
        return vscode.env.clipboard.writeText(message.text);
      case 'clearSamples':
        await session.clearSamples();
        this.resetViewport();
        return;
      case 'loadCsv': return this.importData();
      case 'startRecording': return session.startRecording();
      case 'saveRecording': {
        if (this.savingRecording) return false;
        if (!session.stopRecording()) throw new Error('没有可保存的采样数据');
        this.savingRecording = true;
        try {
          const uri = await saveFile(this.host.t('保存记录 CSV'), 'signal-dock-recording.csv', { CSV: ['csv'] });
          if (!uri) return false;
          await session.saveRecording(uri.fsPath);
          return true;
        } finally {
          this.savingRecording = false;
        }
      }
      case 'saveCsv': {
        if (!session.capture?.count) throw new Error('没有可保存的采样数据');
        const uri = await saveFile(this.host.t('保存数据'), 'signal-dock-samples.csv', { CSV: ['csv'] });
        if (!uri) return false;
        await session.saveCsv(uri.fsPath, !!message.time);
        return true;
      }
      case 'loadArchive': return this.importArchive();
      case 'saveArchive': return this.exportArchive(message);
      case 'demoDir': {
        const [uri] = await vscode.window.showOpenDialog({
          canSelectFiles: false, canSelectFolders: true, openLabel: this.host.t('选择 Demo 文件夹')
        }) ?? [];
        return uri?.fsPath;
      }
      case 'loadAsset': {
        const model = message.asset === 'model';
        const uri = await openFile(this.host.t(model ? '选择模型' : '选择背景图'),
          model ? { '3D model': ['obj', 'stl'] } : { Images: ['png', 'jpg', 'jpeg', 'webp'] }, 4 * 1024 * 1024);
        if (!uri) return;
        return { name: uri.fsPath.split(/[\\/]/).at(-1), data: (await readFile(uri.fsPath)).toString('base64') };
      }
      case 'screenshot': {
        if (typeof message.png !== 'string' || !message.png.startsWith('data:image/png;base64,') || message.png.length > 24 * 1024 * 1024) throw new Error('Invalid screenshot');
        const uri = await saveFile(this.host.t('保存截图'), 'signal-dock-dashboard.png', { PNG: ['png'] });
        if (!uri) return false;
        await writeFile(uri.fsPath, Buffer.from(message.png.slice(22), 'base64'));
        return true;
      }
      case 'help': return vscode.env.openExternal(vscode.Uri.parse('https://www.vofa.plus/docs/learning/'));
      default: return undefined;
    }
  }

  async dispose() {
    this.view = undefined;
    this.updateVisibility();
    await this.session.dispose();
  }

  registerDecoder(name, factory) {
    if (!/^[a-zA-Z][\w.-]{1,47}$/.test(name) || typeof factory !== 'function' ||
        ['justfloat', 'firewater', 'rawdata'].includes(name) || this.decoders.has(name)) {
      throw new Error('Invalid or duplicate decoder registration');
    }
    this.decoders.set(name, factory);
    const refresh = () => { if (this.view?.ready) void this.init(this.view); };
    refresh();
    return new vscode.Disposable(() => {
      this.decoders.delete(name);
      refresh();
    });
  }
}

module.exports = { SignalDock };
