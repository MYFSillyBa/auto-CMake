import './app.css';
import './instruments.css';
import { toPng } from 'html-to-image';
import config from '../src/config.cjs';
import { INSTRUMENTS, defaults, mountInstrument, instrumentFields } from './instruments.mjs';
import { $, $$, t, el, button, options, form, modal, contextMenu, setLanguage, logContent } from './ui.mjs';
import {
  TRANSPORTS, ENGINES, connectionForm, updatePeers, terminalSettings, sampleSettings, channelSettings,
  editText, commandTree, flatCommands, appearanceSettings, applyAppearance
} from './panels.mjs';
import { pluginDialog, mountPlugin } from './plugins.mjs';

const { initialState, normalizeState, bounded, serialPortId, selectSerialPort } = config;
const model = {
  state: initialState(), data: { status: 'idle', stats: {}, channels: [], latest: [], images: {}, series: { start: 0, end: 0, columns: [] }, dtMs: 10, end: 0, start: 0, count: 0 },
  sidebar: 'instruments', selected: '', full: '', position: null, page: 0, logs: [], history: [],
  engines: [], cards: new Map(), channelRows: [], request: new Map(), counter: 0, ports: []
};
const compact = matchMedia('(max-width: 640px)');
const compactViews = { sidebar: false, inspector: false };

function viewVisible(key) {
  return compact.matches && Object.hasOwn(compactViews, key) ? compactViews[key] : model.state.view[key];
}

function setViewVisible(key, visible) {
  if (compact.matches && Object.hasOwn(compactViews, key)) {
    if (visible) compactViews[key === 'sidebar' ? 'inspector' : 'sidebar'] = false;
    compactViews[key] = visible;
  } else {
    model.state.view[key] = visible;
  }
}

function tell(type, details = {}) {
  window.parent.postMessage({ type: 'signalMessage', payload: { ...details, type } }, '*');
}

function rpc(type, details = {}) {
  const id = ++model.counter;
  return new Promise(resolve => {
    model.request.set(id, resolve);
    tell(type, { ...details, id });
  });
}

let toastTimer;
function notify(text, level = '') {
  if (!text) return;
  const toast = $('#toast');
  toast.dataset.level = level;
  toast.textContent = t(text);
  toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toast.hidden = true; }, 5500);
}

function persist() {
  tell('save', { state: model.state });
}

function tab() {
  return model.state.tabs.find(item => item.id === model.state.activeTab) ?? model.state.tabs[0];
}
const widgetTitle = item => item.id === 'live-wave' && item.title === '实时波形' ? t(item.title) : item.title || t(INSTRUMENTS[item.kind] ?? '控件');

function viewSpan() {
  return model.zoomSpan ?? model.state.view.span;
}

function viewport() {
  model.revision = (model.revision ?? 0) + 1;
  if (!model.viewportFrame) {
    model.viewportFrame = requestAnimationFrame(() => {
      model.viewportFrame = 0;
      const analysis = tab().widgets.some(widget => widget.kind === 'wave');
      tell('viewport', { span: viewSpan(), start: model.position, analysis, revision: model.revision });
    });
  }
  return model.revision;
}

function navigateWave(range) {
  if (range) {
    model.zoomSpan = Math.max(2, Math.round(range[1] - range[0]));
    model.position = range[1] >= model.data.end - 0.5 ? null : Math.round(range[0]);
  } else {
    model.zoomSpan = null;
    model.position = null;
  }
  renderTimeline();
  return viewport();
}

function archive(kind) {
  if (kind === 'load') {
    return rpc('loadArchive').then(warnings => {
      if (warnings?.length) notify(warnings.join(' · '));
      else if (warnings) notify('配置已载入');
    });
  }
  return rpc('saveArchive', { kind, tab: tab().id }).then(saved => { if (saved) notify('配置已导出'); });
}

function renderShell() {
  const state = model.state;
  setLanguage(model.language ?? document.documentElement.lang);
  document.title = t('实时波形');
  applyAppearance(state);
  options($('#transport'), TRANSPORTS, state.connection.kind);
  renderPorts();
  const engines = [...ENGINES, ...model.engines.map(name => [name, name])];
  if (!engines.some(([name]) => name === state.connection.engine)) engines.push([state.connection.engine, state.connection.engine + ' (unavailable)']);
  options($('#engine'), engines, state.connection.engine);
  $('#sidebar').hidden = !viewVisible('sidebar');
  $('#inspector').hidden = !viewVisible('inspector');
  $('#terminal').hidden = !state.view.terminal;
  document.documentElement.style.setProperty('--terminal-height', state.view.terminalHeight + 'px');
  $('#span').value = viewSpan();
  $('#span').step = state.view.align || 1;
  $('#board').dataset.locked = !!tab().locked;
  $('#lockLayout').classList.toggle('is-active', !!tab().locked);
  $('#lockLayout').ariaPressed = String(!!tab().locked);
  $('#sendMode').value = state.terminal.sendHex ? 'hex' : 'text';
  $('#toggleTerminal').classList.toggle('is-active', state.view.terminal);
  $('#toggleInspector').classList.toggle('is-active', viewVisible('inspector'));
  for (const node of $$('[data-sidebar]')) node.classList.toggle('is-active', viewVisible('sidebar') && model.sidebar === node.dataset.sidebar);
  renderStatus();
}

function renderTabs() {
  const current = tab();
  $('#tabs').replaceChildren(...model.state.tabs.map(item => {
    const node = button(item.id === 'default' && item.title === '实时监测' ? t(item.title) : item.title, () => switchTab(item.id));
    node.setAttribute('role', 'tab');
    node.setAttribute('aria-selected', String(item === current));
    node.addEventListener('contextmenu', event => tabMenu(event, item));
    return node;
  }));
}

function switchTab(id) {
  model.state.activeTab = id;
  persist();
  model.selected = model.full = '';
  renderTabs();
  renderBoard();
  renderInspector();
  viewport();
}

function tabMenu(event, item = tab()) {
  contextMenu(event, [
    ['重命名', () => editText('重命名', item.title, title => { item.title = title; persist(); renderTabs(); })],
    ['复制标签', () => {
      const copy = structuredClone(item);
      copy.id = crypto.randomUUID();
      copy.title += ' · Copy';
      for (const widget of copy.widgets) widget.id = crypto.randomUUID();
      model.state.tabs.push(copy);
      persist();
      switchTab(copy.id);
    }],
    ['导出当前标签', () => rpc('saveArchive', { kind: 'tab', tab: item.id })],
    ['关闭标签', () => {
      if (model.state.tabs.length === 1) {
        notify('至少保留一个标签');
        return;
      }
      model.state.tabs = model.state.tabs.filter(entry => entry !== item);
      switchTab(tab().id);
      persist();
    }]
  ]);
}

const glyphs = {
  wave: '∿', button: '⏺', delayButton: '◷', toggle: '⏼', slider: '⇥', dial: '◴',
  materialDial: '◉', joystick: '✥', light: '●', gauge: '▥', circularGauge: '◔', cube: '◇', image: '▧'
};

function renderSidebar() {
  const root = $('#sidebar');
  const header = title => el('div', 'panel-heading', t(title));
  if (model.sidebar === 'commands') {
    root.replaceChildren(header('命令树'), commandTree(model.state, () => {
      persist();
      renderSidebar();
      renderInspector();
    }, sendCommand, archive));
  } else if (model.sidebar === 'connection') {
    root.replaceChildren(header('连接与设备'), connectionForm(model.state, model.data, connectionChanged, rpc, renderSidebar));
    updatePeers(model.data, rpc);
    renderPorts();
  } else {
    const palette = el('div', 'palette');
    const entries = [...Object.entries(INSTRUMENTS), ...model.state.plugins.filter(plugin => plugin.enabled).map(plugin => ['plugin:' + plugin.id, plugin.name])];
    for (const [kind, label] of entries) {
      const entry = button('', () => addWidget(kind), '', label);
      entry.append(el('span', '', glyphs[kind] ?? '◫'), document.createTextNode(t(label)));
      entry.disabled = !!tab().locked || (kind.startsWith('plugin:') && !model.trusted);
      entry.draggable = !entry.disabled;
      entry.addEventListener('dragstart', event => {
        event.dataTransfer.effectAllowed = 'copy';
        event.dataTransfer.setData('application/x-signal-dock-instrument', kind);
      });
      palette.append(entry);
    }
    root.replaceChildren(header('控件库'), palette, el('div', 'hint', t('拖入画布，或点击添加。右键设置通道与命令。')));
  }
}

function addWidget(kind, point) {
  if (tab().locked) return;
  const isPlugin = kind.startsWith('plugin:');
  const plugin = model.state.plugins.find(item => 'plugin:' + item.id === kind);
  if (isPlugin ? !plugin?.enabled || !model.trusted : !Object.hasOwn(INSTRUMENTS, kind)) return;
  const board = $('#board');
  const width = kind === 'wave' ? 58 : kind === 'cube' || kind === 'image' ? 40 : 28;
  const height = kind === 'wave' ? 55 : kind === 'cube' || kind === 'image' ? 49 : 36;
  const bump = (tab().widgets.length * 6) % 42;
  const item = {
    ...(isPlugin ? {} : defaults(kind)),
    id: crypto.randomUUID(), kind, title: t(plugin?.name ?? INSTRUMENTS[kind]),
    x: bounded(point ? (point.clientX - board.getBoundingClientRect().left) / board.clientWidth * 100 : 5 + bump, 0, 100 - width, 5),
    y: bounded(point ? (point.clientY - board.getBoundingClientRect().top) / board.clientHeight * 100 : 5 + bump, 0, 100 - height, 5),
    w: width, h: height
  };
  tab().widgets.push(item);
  setViewVisible('inspector', true);
  model.selected = item.id;
  persist();
  renderShell();
  renderBoard();
  renderInspector();
  viewport();
}

function setWidget(item, patch) {
  Object.assign(item, patch);
  persist();
  if (Object.hasOwn(patch, 'antialias')) {
    renderBoard();
    return;
  }
  const card = model.cards.get(item.id);
  if (card) {
    card.title.textContent = widgetTitle(item);
    card.controller.update(model.data);
  }
  if (item.kind === 'wave' && ('channels' in patch || 'title' in patch || 'mode' in patch)) renderWaveChannelPicker();
  if (model.selected === item.id && Object.keys(patch).some(key => [
    'mode', 'analysisOverlay', 'rotation', 'quaternionOrder', 'axisAuto', 'trigger', 'feedback', 'filter', 'threeState'
  ].includes(key))) renderInspector();
}

function selectWidget(item, showInspector = false) {
  model.selected = item?.id ?? '';
  if (showInspector && !viewVisible('inspector')) {
    setViewVisible('inspector', true);
    persist();
    renderShell();
  }
  for (const [id, card] of model.cards) card.node.classList.toggle('selected', id === model.selected);
  renderInspector();
}

function fullWidget(item) {
  model.full = model.full === item.id ? '' : item.id;
  for (const [id, card] of model.cards) card.node.classList.toggle('is-full', id === model.full);
}

function removeWidget(item) {
  if (tab().locked) return;
  tab().widgets = tab().widgets.filter(widget => widget !== item);
  if (model.selected === item.id) model.selected = '';
  if (model.full === item.id) model.full = '';
  persist();
  renderBoard();
  renderInspector();
  viewport();
}

function widgetMenu(event, item) {
  contextMenu(event, [
    ['设置控件', () => selectWidget(item, true)], ['全屏控件', () => fullWidget(item)],
    ...(!tab().locked ? [['移除控件', () => removeWidget(item)]] : [])
  ]);
}

function positionWidget(node, item) {
  node.style.left = item.x + '%';
  node.style.top = item.y + '%';
  node.style.width = item.w + '%';
  node.style.height = item.h + '%';
}

function widgetDrag(handle, item, node, edges = '') {
  handle.addEventListener('pointerdown', event => {
    if (event.button || event.target.closest('button') || tab().locked || model.full === item.id) return;
    const { clientWidth, clientHeight } = $('#board');
    const origin = { x: item.x, y: item.y, w: item.w, h: item.h };
    const dx = event.clientX;
    const dy = event.clientY;
    const id = event.pointerId;
    handle.setPointerCapture(id);
    const move = current => {
      const x = (current.clientX - dx) / clientWidth * 100;
      const y = (current.clientY - dy) / clientHeight * 100;
      if (edges) {
        const right = origin.x + origin.w;
        const bottom = origin.y + origin.h;
        if (edges.includes('w')) {
          item.x = bounded(origin.x + x, 0, right - 12, origin.x);
          item.w = right - item.x;
        }
        if (edges.includes('e')) item.w = bounded(origin.w + x, 12, 100 - origin.x, origin.w);
        if (edges.includes('n')) {
          item.y = bounded(origin.y + y, 0, bottom - 12, origin.y);
          item.h = bottom - item.y;
        }
        if (edges.includes('s')) item.h = bounded(origin.h + y, 12, 100 - origin.y, origin.h);
      } else {
        item.x = bounded(Math.round(origin.x + x), 0, 100 - item.w, origin.x);
        item.y = bounded(Math.round(origin.y + y), 0, 100 - item.h, origin.y);
      }
      positionWidget(node, item);
    };
    const finish = () => { handle.removeEventListener('pointermove', move); persist(); };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('lostpointercapture', finish, { once: true });
  });
}

function renderBoard() {
  for (const { controller } of model.cards.values()) controller.dispose();
  model.cards.clear();
  const board = $('#board');
  board.replaceChildren();
  board.dataset.locked = !!tab().locked;
  const widgets = tab().widgets;
  if (!widgets.length) board.append(el('div', 'board-empty', t('从控件库添加一个仪表，开始搭建监测面板。')));
  for (const item of widgets) {
    const isPlugin = item.kind.startsWith('plugin:');
    if (!isPlugin && Object.hasOwn(INSTRUMENTS, item.kind)) {
      const merged = { ...defaults(item.kind), ...item };
      Object.assign(item, merged);
    }
    const node = el('article', 'instrument');
    node.dataset.kind = item.kind;
    node.classList.toggle('selected', item.id === model.selected);
    node.classList.toggle('is-full', item.id === model.full);
    positionWidget(node, item);
    const head = el('header', 'instrument-head');
    const title = el('span', '', widgetTitle(item));
    head.append(title, button('⋯', event => widgetMenu(event, item), '', '设置控件'), button('⤢', () => fullWidget(item), '', '全屏控件'));
    head.addEventListener('dblclick', event => { if (!event.target.closest('button')) fullWidget(item); });
    node.addEventListener('contextmenu', event => { if (!event.defaultPrevented) widgetMenu(event, item); });
    node.addEventListener('pointerdown', () => { if (model.selected !== item.id) selectWidget(item); });
    const body = el('div', 'instrument-body');
    node.append(head, body);
    board.append(node);
    widgetDrag(head, item, node);
    for (const edge of item.kind === 'wave' ? ['n', 's', 'e', 'w', 'nw', 'ne', 'sw', 'se'] : ['se']) {
      const handle = el('div', 'sizer');
      handle.dataset.edge = edge;
      handle.addEventListener('dblclick', () => fullWidget(item));
      node.append(handle);
      widgetDrag(handle, item, node, edge);
    }
    const api = {
      change: patch => setWidget(item, patch),
      navigate: navigateWave,
      menu: contextMenu,
      send: sendCommand,
      chooseModel: () => rpc('loadAsset', { asset: 'model' }),
      notify
    };
    let controller;
    try {
      const plugin = model.state.plugins.find(entry => 'plugin:' + entry.id === item.kind);
      if (isPlugin && model.trusted && plugin?.enabled) controller = mountPlugin(body, plugin, item, api);
      else if (isPlugin || !Object.hasOwn(INSTRUMENTS, item.kind)) throw new Error('控件扩展未加载');
      else controller = mountInstrument(body, item, api);
      controller.update(model.data);
    } catch (error) {
      controller?.dispose();
      body.replaceChildren(el('div', 'hint', error.message));
      controller = { dispose() {}, update() {} };
    }
    model.cards.set(item.id, { node, controller, title });
  }
}

function renderInspector() {
  renderWaveChannelPicker();
  const area = $('#widgetInspector');
  const item = tab().widgets.find(widget => widget.id === model.selected);
  area.hidden = !item;
  if (!item) return;
  const commands = flatCommands(model.state.commands).map(({ node }) => node);
  const channels = model.data.channels;
  const images = Object.values(model.data.images);
  const configFields = item.kind.startsWith('plugin:')
    ? [{ key: 'commandId', label: '绑定命令', type: 'select', options: [['', '无'], ...commands.map(cmd => [cmd.id, cmd.name])] }]
    : Object.hasOwn(INSTRUMENTS, item.kind) ? instrumentFields(item, { commands, channels, images }) : [];
  const fields = configFields.filter(field => field.key !== 'title' &&
    !(field.key === 'channels' && item.kind === 'wave' && item.mode !== 'xy'));
  const formFields = [{ key: 'title', label: '标题', type: 'text' }, ...fields];
  const values = { ...item };
  for (let i = 0; i < 4; i++) values['channelPosition' + i] = item.channels?.[i] ?? i;
  area.replaceChildren(el('div', 'subheading', t('设置控件')), form(formFields, values, (key, value) => {
    if (key.startsWith('channelPosition')) {
      const channels = Array.from({ length: item.rotation === 'quaternion' ? 4 : 3 }, (_, i) => item.channels?.[i] ?? i);
      channels[Number(key.slice(-1))] = value;
      setWidget(item, { channels });
    } else if (key === 'rotation' && value === 'quaternion' && item.channels?.length === 3) {
      setWidget(item, { rotation: value, channels: [...item.channels, 3] });
    } else setWidget(item, { [key]: value });
  }, () => rpc('loadAsset', { asset: 'model' })));
}

const CHANNELS_PER_PAGE = 256;

function waveWidgets() {
  return tab().widgets.filter(widget => widget.kind === 'wave');
}

function currentWave(waves = waveWidgets()) {
  return waves.find(widget => widget.id === model.selected) ??
    waves.find(widget => widget.id === model.waveTarget) ?? waves[0];
}

function renderWaveChannelPicker() {
  const waves = waveWidgets();
  const wave = currentWave(waves);
  model.waveTarget = wave?.id;
  $('#waveChannelToolbar').hidden = !wave;
  const target = $('#waveChannelTarget');
  const entries = waves.map(item => [item.id, widgetTitle(item)]);
  const key = JSON.stringify(entries);
  if (target.dataset.entries !== key) {
    target.dataset.entries = key;
    options(target, entries, wave?.id);
  }
  if (target.value !== wave?.id) target.value = wave?.id ?? '';
  target.title = wave ? widgetTitle(wave) : '';
  const canSelect = !!wave && wave.mode !== 'xy';
  const selected = new Set(wave?.channels ?? []);
  const { channels } = model.data;
  let available = 0;
  let checked = 0;
  for (const [index, channel] of channels.entries()) {
    if (channel.visible === false) continue;
    available++;
    if (selected.has(index)) checked++;
  }
  $('#waveChannelHelp').hidden = wave?.mode !== 'xy';
  $('#waveChannelActions').hidden = !canSelect;
  $('#selectedChannelCount').textContent = checked + '/' + available;
  $('#selectAllChannels').disabled = checked === available;
  $('#clearAllChannels').disabled = !selected.size;
  for (const { index, checkbox } of model.channelRows) {
    const visible = channels[index]?.visible !== false;
    checkbox.hidden = !canSelect;
    checkbox.disabled = !visible;
    checkbox.checked = visible && selected.has(index);
    checkbox.parentElement.title = visible ? checkbox.getAttribute('aria-label') : t('此通道已全局隐藏');
  }
}

function selectWaveChannels(select) {
  const wave = currentWave();
  if (!wave || wave.mode === 'xy') return;
  const channels = select ? model.data.channels.flatMap((channel, index) => channel.visible === false ? [] : [index]) : [];
  setWidget(wave, { channels });
}

function buildChannelRow(channel, index) {
  const row = el('div', 'channel');
  const edit = () => channelSettings(model.data.channels[index], (property, value) => {
    model.state.channelSettings[index] = { ...model.state.channelSettings[index], [property]: value };
    model.data.channels[index][property] = value;
    persist();
    renderChannels(true);
  });
  const picker = el('label', 'channel-check');
  const checkbox = el('input');
  checkbox.type = 'checkbox';
  checkbox.setAttribute('aria-label', model.language === 'en'
    ? 'Show ' + channel.name + ' trace' : '显示 ' + channel.name + ' 波形');
  checkbox.addEventListener('change', () => {
    const wave = currentWave();
    if (!wave || wave.mode === 'xy') return;
    const selection = new Set(wave.channels ?? []);
    if (checkbox.checked) selection.add(index);
    else selection.delete(index);
    setWidget(wave, { channels: [...selection] });
  });
  const swatch = el('span', 'swatch');
  swatch.style.background = channel.visible ? channel.color : 'transparent';
  const value = el('span', 'value');
  picker.append(checkbox, swatch);
  row.append(picker, button(channel.name, edit, 'name', channel.name), value);
  row.addEventListener('contextmenu', event => contextMenu(event, [['通道名', edit]]));
  return { index, row, value, checkbox };
}

function renderChannels(force = false) {
  const { channels, latest } = model.data;
  const totalPages = Math.max(1, Math.ceil(channels.length / CHANNELS_PER_PAGE));
  model.channelPage = Math.min(model.channelPage ?? 0, totalPages - 1);
  if (force || model.channelsRef !== channels || model.renderedChannelPage !== model.channelPage) {
    model.channelsRef = channels;
    const offset = model.channelPage * CHANNELS_PER_PAGE;
    const page = channels.slice(offset, offset + CHANNELS_PER_PAGE);
    const key = JSON.stringify([model.channelPage, channels.length, page.map(channel => [channel.name, channel.color, channel.visible])]);
    if (force || model.channelRowsKey !== key) {
      model.channelRowsKey = key;
      model.renderedChannelPage = model.channelPage;
      const root = $('#channels');
      model.channelRows = page.map((channel, index) => buildChannelRow(channel, offset + index));
      root.replaceChildren(...model.channelRows.map(item => item.row));
      if (!channels.length) root.append(el('div', 'hint', t('连接设备或运行 Demo 后显示通道。')));
      $('#channelCount').textContent = channels.length + ' CH';
      $('#channelPager').hidden = totalPages <= 1;
      $('#channelPageNumber').textContent = (model.channelPage + 1) + '/' + totalPages;
      $('#previousChannels').disabled = model.channelPage === 0;
      $('#nextChannels').disabled = model.channelPage === totalPages - 1;
      renderInspector();
    }
  }
  for (const { index, value: label } of model.channelRows) {
    const ch = channels[index];
    const value = latest[index] == null ? NaN : latest[index] * ch.scale + ch.offset;
    const digits = bounded(ch.decimals, 0, 15, 3);
    const display = Number.isFinite(value) ? value.toFixed(digits) : '—';
    if (label.textContent !== display) label.textContent = display;
  }
}

function renderTimeline() {
  const { start, end, count } = model.data;
  const span = viewSpan();
  const input = $('#span');
  if (document.activeElement !== input && Number(input.value) !== span) input.value = span;
  const control = $('#timeline');
  control.min = start;
  control.max = Math.max(start, end - span);
  control.value = model.position ?? control.max;
  control.disabled = count <= span;
  $('#freeze').textContent = model.position == null ? '⏸' : '▶';
  $('#freeze').classList.toggle('is-active', model.position != null);
  const left = model.data.series.start;
  const right = model.data.series.end;
  $('#rangeText').textContent = left + '–' + right + ' / ' + count;
}

function formatBytes(bytes = 0) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KiB';
  return (bytes / 1048576).toFixed(2) + ' MiB';
}

function renderStatus() {
  const data = model.data;
  const config = model.state.connection;
  const active = data.status === 'connected';
  const connecting = data.status === 'connecting' || model.connectPending;
  $('#status').dataset.state = connecting ? 'connecting' : data.status;
  $('#statusText').textContent = t(connecting ? '连接中' : active ? '在线' : '离线');
  $('#connect').textContent = t(connecting ? '连接中' : active ? '断开' : '连接');
  $('#connect').disabled = connecting;
  $('#startDemo').disabled = active || connecting;
  $('#transport').disabled = active || connecting;
  $('#engine').disabled = active || connecting;
  $('#saveData').disabled = !data.captured;
  $('#clearSamples').disabled = !data.captured && !data.stats.images;
  $('#captureState').textContent = t(data.capturing ? '自动采集中' : '已采集') + ' ' + (data.captured ?? 0).toLocaleString();
  $('#stopSend').disabled = !data.sending;
  $('#send').disabled = !active || config.kind === 'demo';
  $('#sendFile').disabled = $('#send').disabled;
  const endpoints = {
    serial: [config.path, config.baudRate + ' baud'].filter(Boolean).join(' · '),
    tcpClient: config.host + ':' + config.port,
    tcpServer: config.host + ':' + config.port,
    udp: config.localPort + ' → ' + config.remoteHost + ':' + config.remotePort,
    demo: config.demoSignal
  };
  $('#endpoint').textContent = (endpoints[config.kind] ?? '') + ' · ' + (ENGINES.find(([key]) => key === config.engine)?.[1] ?? config.engine);
  const error = $('#statusError');
  error.hidden = !data.error;
  error.textContent = error.title = data.error ?? '';
  $('#rxStat').textContent = 'RX ' + formatBytes(data.stats.rx);
  $('#txStat').textContent = 'TX ' + formatBytes(data.stats.tx);
  $('#frameStat').textContent = (data.stats.samples ?? 0) + ' F · ' + (data.stats.images ?? 0) + ' IMG · ' + (data.stats.invalid ?? 0) + ' ERR';
  $('#capacityStat').textContent = (data.count ?? 0).toLocaleString() + ' / ' + (data.capacity ?? model.state.capacity).toLocaleString();
  $('#dtStat').textContent = 'Δt ' + model.state.dtMs + ' ms';
  updatePeers(data, rpc);
}

function renderLog() {
  const { terminal } = model.state;
  const log = $('#logs');
  log.style.setProperty('--log-font', terminal.zoom + 'px');
  $('#rx').classList.toggle('is-active', terminal.rxEcho);
  $('#tx').classList.toggle('is-active', terminal.txEcho);
  $('#hex').classList.toggle('is-active', terminal.hex);
  const filtered = model.logs.filter(item => (item.direction !== 'tx' || terminal.txEcho) && (item.direction !== 'rx' || terminal.rxEcho) &&
    (item.kind !== 'sample' || terminal.showPlotData) && (item.kind !== 'image' || terminal.showImageData));
  const pageSize = bounded(terminal.pageSize, 10, 500, 120);
  const count = Math.max(1, Math.min(terminal.pages || 8, Math.ceil(filtered.length / pageSize)));
  model.page = Math.max(0, Math.min(count - 1, model.page));
  const last = filtered.length - model.page * pageSize;
  const page = filtered.slice(Math.max(0, last - pageSize), last);
  $('#logPage').textContent = (count - model.page) + '/' + count;
  $('#olderLog').disabled = model.page >= count - 1;
  $('#newerLog').disabled = !model.page;
  const rows = page.map(item => {
    const row = el('div', 'log-row ' + item.direction);
    row.append(el('span', 'log-kind', item.direction.toUpperCase()));
    if (terminal.timestamps) row.append(el('span', 'log-time', new Date(item.time).toLocaleTimeString('en-GB', {
      hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3
    })));
    if (item.peer) row.append(el('span', 'log-time', item.peer));
    const bytes = el('code');
    const isHex = terminal.hex && item.direction !== 'sys';
    bytes.append(isHex ? (item.hex.match(/../g) ?? []).join(' ').toUpperCase() : logContent(item.text, terminal.ansi));
    if (item.length * 2 > item.hex.length) bytes.append(' … (' + item.length + ' B)');
    row.append(bytes);
    return row;
  });
  log.replaceChildren(...(rows.length ? rows : [el('div', 'hint', t('等待收发数据。可在终端设置中显示数值帧和图片帧。'))]));
  if (!model.page) log.scrollTop = log.scrollHeight;
}

function renderAll() {
  renderShell();
  renderTabs();
  renderSidebar();
  renderBoard();
  renderInspector();
  renderChannels(true);
  renderTimeline();
  renderLog();
}

function connectionChanged() {
  persist();
  renderStatus();
  renderPorts();
}

function connectionDialog() {
  modal('连接设置', connectionForm(model.state, model.data, connectionChanged, rpc, connectionDialog));
  updatePeers(model.data, rpc);
  renderPorts();
  void refreshPorts();
}

function renderPorts() {
  const connection = model.state.connection;
  const serial = connection.kind === 'serial';
  const active = model.data.status !== 'idle' || model.connectPending;
  const baud = $('#serialBaud');
  baud.hidden = !serial;
  baud.disabled = active;
  if (document.activeElement !== baud) baud.value = connection.baudRate;
  const ports = model.ports;
  const choices = ports.map(port => [
    port.path, port.friendlyName || [port.path, port.manufacturer].filter(Boolean).join(' · ')
  ]);
  const selected = selectSerialPort(ports, connection)?.path ?? '';
  for (const node of $$('[data-serial-ports]')) {
    node.hidden = !serial;
    if (!serial) continue;
    node.disabled = active || !ports.length;
    const key = JSON.stringify([choices, selected]);
    if (node.dataset.portKey !== key) {
      node.dataset.portKey = key;
      options(node, [['', ports.length ? '选择串口设备' : '未检测到串口'], ...choices], selected);
    }
  }
}

async function refreshPorts() {
  if (model.state.connection.kind !== 'serial' || model.scanningPorts) return;
  model.scanningPorts = true;
  try {
    const ports = await rpc('ports');
    if (!Array.isArray(ports)) return;
    model.ports = ports.sort((a, b) => a.path.localeCompare(b.path, undefined, { numeric: true }));
    const connection = model.state.connection;
    if (connection.kind === 'serial' && model.data.status === 'idle' && !model.connectPending) {
      const port = selectSerialPort(model.ports, connection);
      if (port && (port.path !== connection.path || serialPortId(port) !== connection.portId)) {
        Object.assign(connection, { path: port.path, portId: serialPortId(port) });
        persist();
      }
    }
    renderPorts();
  } finally {
    model.scanningPorts = false;
  }
}

function sendCommand(commandId, params) {
  if (!commandId) return;
  void rpc('command', { commandId, params: Array.isArray(params) ? params : Object.values(params ?? {}) });
}

function sendText() {
  const text = $('#sendText').value;
  if (!text) return;
  model.history = [text, ...model.history.filter(entry => entry !== text)].slice(0, 40);
  void rpc('send', { text });
}

function toggleView(key) {
  setViewVisible(key, !viewVisible(key));
  persist();
  renderShell();
  if (key === 'sidebar') renderSidebar();
}

async function clickAction(name) {
  if (name === 'saveTimed') {
    await rpc('saveCsv', { time: true });
  } else if (name === 'loadArchive') {
    await archive('load');
  } else if (name === 'appearance') {
    appearanceSettings(model.state, () => { persist(); renderAll(); }, rpc);
  } else if (name === 'plugins') {
    pluginDialog(model.state, model.engines, model.trusted, () => { persist(); renderBoard(); renderSidebar(); });
  } else if (name === 'screenshot') {
    try {
      const backgroundColor = getComputedStyle(document.body).getPropertyValue('--bg').trim();
      const png = await toPng($('#board'), { backgroundColor, pixelRatio: Math.min(2, devicePixelRatio) });
      const saved = await rpc(name, { png });
      if (saved) notify('截图已保存');
    } catch (error) { notify(error.message, 'error'); }
  } else {
    await rpc(name);
  }
}

function applyTick(message) {
  const { channels, latest, images, series, analysisSeries, log, clear, ...status } = message;
  const data = model.data;
  Object.assign(data, status);
  if (channels) data.channels = channels;
  if (latest) data.latest = latest;
  if (images) data.images = images;
  if (series) { data.series = series; data.analysisSeries = analysisSeries; }
  renderStatus();
  renderTimeline();
  if (latest || channels) renderChannels();
  else if (images && model.selected) renderInspector();
  if (series || images || latest) for (const { controller } of model.cards.values()) controller.update(data);
  if (clear) model.logs = [];
  if (log?.length) {
    const max = bounded(model.state.terminal.pages, 1, 16, 8) * bounded(model.state.terminal.pageSize, 10, 500, 120);
    model.logs = [...model.logs, ...log].slice(-max);
  }
  if (clear || log?.length) renderLog();
}

window.addEventListener('message', event => {
  if (event.source !== window.parent || event.source === window) return;
  if (event.data?.type === 'signalsTheme') {
    document.body.classList.toggle('vscode-light', event.data.light === true);
    for (const [key, value] of Object.entries(event.data.variables || {})) document.body.style.setProperty(key, value);
    applyAppearance(model.state);
    return;
  }
  const message = event.data?.type === 'signalMessage' ? event.data.payload : undefined;
  if (!message || typeof message.type !== 'string') return;
  if (message.type === 'result') {
    if (message.error) notify(message.error, 'error');
    model.request.get(message.id)?.(message.result);
    model.request.delete(message.id);
  } else if (message.type === 'init' || message.type === 'state') {
    model.state = normalizeState(message.state);
    if (message.type === 'init') {
      model.language = message.language;
      model.trusted = message.trusted;
      model.engines = message.engines;
    }
    renderAll();
    viewport();
    void refreshPorts();
  } else if (message.type === 'language') {
    model.language = message.language;
    for (const node of $$('[data-serial-ports]')) delete node.dataset.portKey;
    if ($('#dialog').open) $('#dialog').close();
    renderAll();
  } else if (message.type === 'tick') {
    try {
      applyTick(message);
    } finally {
      requestAnimationFrame(() => tell('frameAck', { frameId: message.frameId }));
    }
  } else if (message.type === 'resetViewport') {
    model.position = null;
    model.zoomSpan = null;
    renderTimeline();
    viewport();
  } else if (message.type === 'notice') {
    notify(message.text, message.level);
  }
});

for (const node of $$('[data-action]')) node.addEventListener('click', () => void clickAction(node.dataset.action));
for (const node of $$('[data-archive]')) node.addEventListener('click', () => void archive(node.dataset.archive));
for (const node of $$('[data-view]')) node.addEventListener('click', () => toggleView(node.dataset.view));
for (const node of $$('[data-sidebar]')) node.addEventListener('click', () => {
  const kind = node.dataset.sidebar;
  setViewVisible('sidebar', !(viewVisible('sidebar') && model.sidebar === kind));
  model.sidebar = kind;
  persist();
  renderShell();
  renderSidebar();
});
compact.addEventListener('change', () => { renderShell(); renderSidebar(); });
document.addEventListener('click', event => {
  for (const menu of $$('details.menu[open]')) if (!event.target.closest('summary') || !menu.contains(event.target)) menu.open = false;
  if (!event.target.closest('#contextMenu, #tabOptions, #history, .instrument-head button')) $('#contextMenu').hidden = true;
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    $('#contextMenu').hidden = true;
    if (model.full) {
      const item = tab().widgets.find(widget => widget.id === model.full);
      if (item) fullWidget(item);
    }
  }
});
$('#closeDialog').onclick = () => $('#dialog').close();
$('#connectionSettings').onclick = connectionDialog;
$('#transport').onchange = event => {
  model.state.connection.kind = event.target.value;
  connectionChanged();
  renderShell();
  renderSidebar();
  void refreshPorts();
};
document.addEventListener('change', event => {
  if (!event.target.matches('[data-serial-ports]')) return;
  const port = model.ports.find(item => item.path === event.target.value);
  if (!port) { renderPorts(); return; }
  Object.assign(model.state.connection, { path: port.path, portId: serialPortId(port) });
  connectionChanged();
  renderPorts();
});
document.addEventListener('signal-dock-refresh-ports', () => void refreshPorts());
$('#serialBaud').onchange = event => {
  const input = event.currentTarget;
  if (!input.checkValidity() || !Number.isInteger(input.valueAsNumber)) {
    input.value = model.state.connection.baudRate;
    return;
  }
  model.state.connection.baudRate = input.valueAsNumber;
  connectionChanged();
};
setInterval(() => {
  if (!document.hidden) void refreshPorts();
}, 1800);
$('#engine').onchange = event => { model.state.connection.engine = event.target.value; connectionChanged(); renderSidebar(); };
$('#connect').onclick = async () => {
  if (model.data.status === 'connected') { await rpc('disconnect'); return; }
  if (model.state.connection.kind === 'demo') {
    model.state.dtMs = model.state.connection.demoMs;
    persist();
  }
  model.connectPending = true;
  renderStatus();
  renderPorts();
  await rpc('connect');
  model.connectPending = false;
  renderStatus();
  renderPorts();
};
$('#saveData').onclick = async () => {
  const saved = await rpc('saveCsv', { time: true });
  if (saved) notify(t('数据已保存'));
};
$('#toggleTerminal').onclick = () => toggleView('terminal');
$('#toggleInspector').onclick = () => toggleView('inspector');
$('#waveChannelTarget').onchange = event => selectWidget(waveWidgets().find(item => item.id === event.target.value));
$('#selectAllChannels').onclick = () => selectWaveChannels(true);
$('#clearAllChannels').onclick = () => selectWaveChannels(false);
for (const [buttonId, delta] of [['previousChannels', -1], ['nextChannels', 1]]) {
  $('#' + buttonId).onclick = () => {
    model.channelPage += delta;
    renderChannels();
    $('#channels').scrollTop = 0;
  };
}
$('#addTab').onclick = () => editText('新建标签', t('监测') + ' ' + (model.state.tabs.length + 1), title => {
  const item = { id: crypto.randomUUID(), title, widgets: [] };
  model.state.tabs.push(item);
  persist();
  switchTab(item.id);
});
$('#tabOptions').onclick = event => tabMenu(event);
$('#lockLayout').onclick = () => {
  tab().locked = !tab().locked;
  persist();
  renderShell();
  renderSidebar();
};
$('#board').addEventListener('dragover', event => {
  if (!tab().locked && event.dataTransfer.types.includes('application/x-signal-dock-instrument')) event.preventDefault();
});
$('#board').addEventListener('drop', event => {
  const kind = event.dataTransfer.getData('application/x-signal-dock-instrument');
  if (!kind) return;
  event.preventDefault();
  addWidget(kind, event);
});
$('#board').addEventListener('click', event => { if (event.target === $('#board')) selectWidget(null); });
$('#timeline').oninput = event => {
  model.position = event.target.value === event.target.max ? null : Number(event.target.value);
  viewport();
};
$('#freeze').onclick = () => {
  model.position = model.position == null ? Number($('#timeline').value) : null;
  viewport();
  renderTimeline();
};
$('#span').onchange = event => {
  const span = bounded(event.target.valueAsNumber, 2, 2000000, 500);
  const align = model.state.view.align || 1;
  model.state.view.span = bounded(Math.round(span / align) * align, 2, 2000000, 500);
  model.zoomSpan = null;
  event.target.value = model.state.view.span;
  persist();
  viewport();
};
$('#sampleOptions').onclick = () => sampleSettings(model.state, () => { persist(); renderShell(); }, () => {
  $('#dialog').close();
  model.position = null;
  viewport();
  tell('clearSamples');
});
for (const [id, key] of [['rx', 'rxEcho'], ['tx', 'txEcho'], ['hex', 'hex']]) {
  $('#' + id).onclick = () => { model.state.terminal[key] = !model.state.terminal[key]; persist(); renderLog(); };
}
$('#clearLog').onclick = () => tell('clearLog');
$('#olderLog').onclick = () => { model.page++; renderLog(); };
$('#newerLog').onclick = () => { model.page--; renderLog(); };
$('#copyLog').onclick = () => void rpc('copyLog', { text: $('#logs').innerText });
$('#terminalOptions').onclick = () => terminalSettings(model.state, () => { persist(); renderLog(); });
$('#sendMode').onchange = event => { model.state.terminal.sendHex = event.target.value === 'hex'; persist(); };
$('#send').onclick = sendText;
$('#stopSend').onclick = () => tell('cancelSend');
$('#sendFile').onclick = () => void rpc('sendFile');
$('#sendText').onkeydown = event => {
  if (event.key !== 'Enter' || event.isComposing || event.shiftKey) return;
  const send = model.state.terminal.enterToSend ? !(event.ctrlKey || event.metaKey) : event.ctrlKey || event.metaKey;
  if (send) { event.preventDefault(); if (!$('#send').disabled) sendText(); }
};
$('#history').onclick = event => {
  if (!model.history.length) { notify('暂无发送记录'); return; }
  contextMenu(event, model.history.slice(0, 12).map(value => [value.slice(0, 48), () => { $('#sendText').value = value; $('#sendText').focus(); }]));
};
$('#terminalGrip').onpointerdown = event => {
  const grip = event.currentTarget;
  const y = event.clientY;
  const height = model.state.view.terminalHeight;
  grip.setPointerCapture(event.pointerId);
  const move = current => {
    model.state.view.terminalHeight = bounded(height + y - current.clientY, 87, innerHeight * 0.72, height);
    document.documentElement.style.setProperty('--terminal-height', model.state.view.terminalHeight + 'px');
  };
  grip.addEventListener('pointermove', move);
  grip.addEventListener('lostpointercapture', () => { grip.removeEventListener('pointermove', move); persist(); }, { once: true });
};
new MutationObserver(() => applyAppearance(model.state)).observe(document.body, { attributeFilter: ['class'] });
tell('ready');
