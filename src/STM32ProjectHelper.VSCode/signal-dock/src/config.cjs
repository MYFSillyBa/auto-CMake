const CONNECTION = {
  kind: 'serial', engine: 'justfloat', path: '', portId: '', baudRate: 115200, dataBits: 8, parity: 'none',
  stopBits: 1, flowControl: 'none', dtr: false, rts: false, brk: false,
  host: '127.0.0.1', port: 9000, localHost: '0.0.0.0', localPort: 9000,
  remoteHost: '127.0.0.1', remotePort: 9001, demoMs: 10, demoChannels: 4,
  demoSignal: 'sine', demoDir: '', expectedChannels: 0
};

const TERMINAL = {
  hex: false, sendHex: false, rxEcho: true, txEcho: true, showPlotData: false, showImageData: false,
  timestamps: true, encoding: 'utf8', eol: 'lf', enterToSend: true, count: 1,
  delayMs: 100, groupMs: 20, pageSize: 120, pages: 8, zoom: 12, ansi: true
};

function initialState() {
  return {
    connection: { ...CONNECTION },
    terminal: { ...TERMINAL },
    dtMs: 10, fps: 0, refreshVersion: 1, capacity: 50000,
    view: { span: 500, align: 10, sidebar: true, inspector: true, terminal: true, terminalHeight: 205 },
    appearance: { theme: 'auto', scale: 1, font: '', background: '', language: 'zh' },
    themes: [], commands: [], channelSettings: {}, plugins: [],
    tabs: [{ id: 'default', title: '实时监测', widgets: [
      { id: 'live-wave', kind: 'wave', title: '实时波形', channels: [0, 1, 2, 3], x: 1, y: 2, w: 98, h: 96 }
    ] }],
    activeTab: 'default'
  };
}

function bounded(value, low, high, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(high, Math.max(low, number)) : fallback;
}

function validObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function serialPortId(port) {
  return port.pnpId || (port.serialNumber ? [port.vendorId, port.productId, port.serialNumber].join(':') : '');
}

function selectSerialPort(ports, settings) {
  const available = ports.filter(port => typeof port.path === 'string' && port.path);
  const selectedPath = typeof settings.path === 'string' ? settings.path.toLowerCase() : '';
  const samePath = available.find(port => port.path.toLowerCase() === selectedPath);
  if (settings.portId) {
    if (samePath && serialPortId(samePath) === settings.portId) return samePath;
    const matches = available.filter(port => serialPortId(port) === settings.portId);
    if (matches.length === 1) return matches[0];
  } else if (samePath) return samePath;
  if (available.length === 1) return available[0];
  const usb = available.filter(port => {
    const id = port.pnpId ?? '';
    return /^(USB|FTDIBUS)\\/i.test(id) || !/^BTH/i.test(id) && !!(port.vendorId && port.productId);
  });
  return usb.length === 1 ? usb[0] : undefined;
}

function normalizeTabs(tabs, fallback) {
  if (!Array.isArray(tabs)) return fallback;
  const valid = tabs.filter(tab => validObject(tab) && typeof tab.id === 'string' && typeof tab.title === 'string' && Array.isArray(tab.widgets));
  return valid.length ? valid.map(tab => ({
    ...tab,
    widgets: tab.widgets.filter(widget => validObject(widget) && typeof widget.kind === 'string' && typeof widget.id === 'string')
      .map(widget => {
        const x = bounded(widget.x, 0, 99, 0);
        const y = bounded(widget.y, 0, 99, 0);
        return {
          ...widget, x, y, w: bounded(widget.w, 1, 100 - x, Math.min(40, 100 - x)),
          h: bounded(widget.h, 1, 100 - y, Math.min(40, 100 - y))
        };
      })
  })) : fallback;
}

function normalizeCommands(nodes, depth = 0) {
  if (!Array.isArray(nodes) || depth > 16) return [];
  return nodes.filter(item => validObject(item) && typeof item.id === 'string' && typeof item.name === 'string')
    .map(item => ({
      ...item, text: typeof item.text === 'string' ? item.text : '',
      ...(item.children === undefined ? {} : { children: normalizeCommands(item.children, depth + 1) })
    }));
}

function normalizeState(value) {
  const base = initialState();
  const state = value && typeof value === 'object' ? value : {};
  return {
    ...base,
    ...state,
    connection: { ...base.connection, ...state.connection },
    terminal: { ...base.terminal, ...state.terminal },
    view: { ...base.view, ...state.view },
    appearance: { ...base.appearance, ...state.appearance },
    capacity: Math.round(bounded(state.capacity, 1, 2_000_000, base.capacity)),
    dtMs: bounded(state.dtMs, 0.000001, 3_600_000, base.dtMs),
    fps: state.refreshVersion !== 1 && state.fps === 30 ? 0 : Math.round(bounded(state.fps, 0, 240, base.fps)),
    refreshVersion: 1,
    commands: normalizeCommands(state.commands),
    tabs: normalizeTabs(state.tabs, base.tabs),
    themes: Array.isArray(state.themes) ? state.themes.filter(validObject) : [],
    plugins: Array.isArray(state.plugins) ? state.plugins.filter(plugin => validObject(plugin) &&
      typeof plugin.id === 'string' && typeof plugin.name === 'string' &&
      typeof plugin.source === 'string' && plugin.source.length <= 50000) : [],
    channelSettings: validObject(state.channelSettings) ? state.channelSettings : {}
  };
}

const CHANNEL_COLORS = ['#42d6b7', '#80aaff', '#ffc46d', '#e891dc', '#c3e777', '#b0a2ff', '#ff887e', '#60cfef', '#f9ec8c', '#acbed7'];

function channelAt(index, settings) {
  return {
    name: 'I' + index, color: CHANNEL_COLORS[index % CHANNEL_COLORS.length], scale: 1,
    offset: 0, xOffset: 0, decimals: 3, visible: true,
    ...settings[index]
  };
}

module.exports = { CONNECTION, TERMINAL, initialState, normalizeState, channelAt, bounded, serialPortId, selectSerialPort };
