import { $, el, t, form, modal, button, contextMenu } from './ui.mjs';

const num = (key, label, min, max, step = 1) => ({ key, label, type: 'number', min, max, step });
const select = (key, label, options) => ({ key, label, type: 'select', options });
const check = (key, label) => ({ key, label, type: 'checkbox' });
const line = (key, label) => ({ key, label, type: 'text' });
const kv = values => values.map(value => [value, String(value)]);

export const TRANSPORTS = [['demo', 'Demo'], ['serial', '串口'], ['tcpClient', 'TCP Client'], ['tcpServer', 'TCP Server'], ['udp', 'UDP']];
export const ENGINES = [['justfloat', 'JustFloat'], ['firewater', 'FireWater'], ['rawdata', 'RawData']];
export const ENDINGS = [['none', '无'], ['lf', 'LF'], ['crlf', 'CRLF'], ['cr', 'CR'], ['lfcr', 'LFCR']];
const ENCODINGS = kv(['utf8', 'ascii', 'latin1', 'gbk', 'gb18030', 'big5', 'utf16le', 'shift_jis', 'windows1252']);

export function connectionForm(state, data, update, rpc, rerender) {
  const config = state.connection;
  let specs;
  if (config.kind === 'serial') {
    specs = [
      num('baudRate', '波特率', 1, 12_000_000),
      select('dataBits', '数据位', kv([5, 6, 7, 8])), select('parity', '校验', kv(['none', 'even', 'odd', 'mark', 'space'])),
      select('stopBits', '停止位', kv([1, 1.5, 2])),
      select('flowControl', '流控', [['none', '无'], ['hardware', '硬件'], ['software', '软件']]),
      check('dtr', 'DTR'), check('rts', 'RTS'), check('brk', 'Break')
    ];
  } else if (config.kind === 'tcpClient') {
    specs = [line('host', '服务器地址'), num('port', '端口', 1, 65535), line('banner', '连接后发送')];
  } else if (config.kind === 'tcpServer') {
    specs = [line('host', '监听地址'), num('port', '监听端口', 1, 65535)];
  } else if (config.kind === 'udp') {
    specs = [line('localHost', '本地地址'), num('localPort', '本地端口', 1, 65535),
      line('remoteHost', '远端地址'), num('remotePort', '远端端口', 1, 65535)];
  } else {
    specs = [
      select('demoSignal', '数据源', kv(['sine', 'square', 'triangle', 'sawtooth', 'noise', 'csv', 'jpg'])),
      num('demoMs', '采样周期（ms）', 1, 60000), num('demoChannels', '通道数', 1, 256)
    ];
  }
  if (config.engine === 'justfloat' || config.engine === 'firewater') specs.push(num('expectedChannels', '期望通道数（0 自动）', 0, 4096));
  const wrap = el('div');
  const contents = form(specs, config, (key, value) => {
    config[key] = value;
    update();
    if (key === 'demoSignal') rerender();
    if (data.status === 'connected' && ['dtr', 'rts', 'brk'].includes(key)) void rpc('signals', { signals: { [key]: value } });
  });
  wrap.append(contents);
  if (config.kind === 'serial') {
    const tools = el('div', 'command-tools');
    const ports = el('select');
    ports.setAttribute('aria-label', '串口设备');
    ports.dataset.serialPorts = '';
    tools.append(ports, button('刷新', () => document.dispatchEvent(new Event('signal-dock-refresh-ports'))));
    wrap.prepend(tools);
  }
  if (config.kind === 'demo' && ['csv', 'jpg'].includes(config.demoSignal)) {
    const row = el('div', 'command-tools');
    row.append(button('文件夹', async () => {
      const path = await rpc('demoDir');
      if (path) { config.demoDir = path; update(); rerender(); }
    }), el('small', 'muted', config.demoDir));
    wrap.append(row);
  }
  const peers = el('div', 'command-tools peer-list');
  peers.hidden = true;
  wrap.append(peers);
  return wrap;
}

export function updatePeers(data, rpc) {
  const entries = (data.peers ?? []).map(peer => [peer.id, peer.label]);
  const key = JSON.stringify([entries, data.peer]);
  for (const container of document.querySelectorAll('.peer-list')) {
    container.hidden = !entries.length;
    if (!entries.length || container.dataset.key === key) continue;
    container.dataset.key = key;
    const list = el('select');
    list.setAttribute('aria-label', 'Active peer');
    list.replaceChildren(...entries.map(([id, label]) => new Option(label, id)));
    list.value = data.peer ?? entries[0][0];
    list.onchange = () => void rpc('peer', { peer: list.value });
    container.replaceChildren(el('span', 'muted', 'Peers ' + entries.length), list);
  }
}

export function terminalSettings(state, save) {
  modal('终端设置', form([
    select('encoding', '编码', ENCODINGS), select('eol', '换行', ENDINGS),
    check('enterToSend', 'Enter 发送'), check('timestamps', '时间戳'), check('ansi', 'ANSI 颜色'),
    check('rxEcho', '显示 RX'), check('txEcho', '显示 TX'),
    check('showPlotData', '显示数据帧'), check('showImageData', '显示图片帧'),
    num('groupMs', '接收合并（ms）', 0, 2000), num('pageSize', '每页行数', 10, 500),
    num('pages', '保留页数', 1, 16), num('zoom', '文本字号', 8, 30),
    num('count', '发送次数', -999999, 999999), num('delayMs', '发送间隔（ms）', 0, 3600000)
  ], state.terminal, (key, value) => { state.terminal[key] = value; save(); }));
}

export function sampleSettings(state, save, clear) {
  modal('采样与时间轴', form([
    num('capacity', '缓冲容量 /ch', 1, 2000000), num('dtMs', '名义 Δt（ms）', 0.000001, 3600000, 'any'),
    num('align', '可视点对齐', 1, 10000)
  ], { ...state, align: state.view.align }, (key, value) => {
    if (key === 'align') state.view.align = value;
    else state[key] = value;
    save();
  }), [button('清空采样', clear, 'danger')]);
}

export function channelSettings(channel, save) {
  modal('通道名', form([
    line('name', '通道名'), { key: 'color', label: '颜色', type: 'color' }, check('visible', '可见'),
    num('scale', '显示增益', -1e20, 1e20, 'any'), num('offset', 'Y 偏置', -1e20, 1e20, 'any'),
    num('xOffset', 'X 偏置', -1e20, 1e20, 'any'), num('decimals', '小数位', 0, 15)
  ], channel, save));
}

export function editText(title, value, save) {
  const node = el('div', 'form-grid');
  const input = el('input');
  input.value = value;
  input.setAttribute('aria-label', t('名称'));
  node.append(input);
  const accept = () => {
    const name = input.value.trim();
    if (!name) return;
    $('#dialog').close();
    save(name);
  };
  input.addEventListener('keydown', event => { if (event.key === 'Enter') accept(); });
  modal(title, node, [button('保存', accept, 'primary')]);
  input.select();
}

export function flatCommands(nodes, depth = 0) {
  return nodes.flatMap(node => [{ node, depth, parent: nodes }, ...(node.children ? flatCommands(node.children, depth + 1) : [])]);
}

function editCommand(node, save) {
  const draft = { ...node };
  const specs = [line('name', '名称'), { key: 'note', label: '注释', type: 'textarea' }, check('hidden', '组发送时跳过')];
  if (!node.children) specs.push(
    check('hex', 'HEX'), { key: 'text', label: '文本', type: 'textarea' }, select('eol', '换行', ENDINGS),
    num('count', '发送次数', -999999, 999999), num('delayMs', '发送间隔（ms）', 0, 3600000)
  );
  const contents = form(specs, draft, (key, value) => { draft[key] = value; });
  const accept = () => {
    if (!draft.name?.trim()) return;
    Object.assign(node, draft);
    $('#dialog').close();
    save();
  };
  modal(node.children ? '编辑命令组' : '编辑命令', contents, [button('保存', accept, 'primary')]);
}

export function commandTree(state, save, send, archive) {
  const tree = el('div');
  const bar = el('div', 'command-tools');
  const add = (group, target = state.commands) => {
    const command = { id: crypto.randomUUID(), name: t(group ? '命令组' : '命令'), text: '', note: '', hex: false, eol: 'none', count: 1, delayMs: 100 };
    if (group) command.children = [];
    editCommand(command, () => { target.push(command); save(); });
  };
  bar.append(button('添加命令', () => add(false)), button('添加组', () => add(true)),
    button('↧', () => archive('load'), '', '载入配置 / VOFA+ 1.3'),
    button('↥', () => archive('commands'), '', '导出命令树'));
  tree.append(bar);
  const nodes = flatCommands(state.commands);
  for (const { node, depth, parent } of nodes) {
    const row = el('div', 'command-row');
    row.style.setProperty('--depth', depth);
    row.dataset.hidden = !!node.hidden;
    row.draggable = true;
    row.title = node.note || node.name;
    const remove = () => { parent.splice(parent.indexOf(node), 1); save(); };
    const move = delta => {
      const index = parent.indexOf(node);
      if (index + delta < 0 || index + delta >= parent.length) return;
      parent.splice(index, 1);
      parent.splice(index + delta, 0, node);
      save();
    };
    const run = () => {
      const body = el('div', 'form-grid');
      const input = el('input');
      input.placeholder = '1.25, 2, 3';
      input.setAttribute('aria-label', 'Parameters');
      body.append(el('span', 'muted', t('参数用逗号分隔，最多 10 个。Hex 字节参数用 0x 前缀。')), input);
      modal(node.name, body, [button('发送', () => {
        const params = input.value.trim() ? input.value.split(',').map(value => {
          const item = value.trim();
          return /^0x/i.test(item) ? item.slice(2) : Number(item);
        }) : [];
        $('#dialog').close();
        send(node.id, params);
      }, 'primary')]);
    };
    const hasParams = current => current.children
      ? current.children.some(hasParams) : current.hex ? current.text.includes('%%') : /%(?!%)[.\d+\-]*[df]/.test(current.text);
    row.append(button((node.children ? '▾ ' : '') + node.name, () => editCommand(node, save), 'name'),
      button('▷', () => hasParams(node) ? run() : send(node.id, []), '', '发送'));
    row.addEventListener('contextmenu', event => contextMenu(event, [
      ['编辑', () => editCommand(node, save)], ['带参数发送', run],
      ...(node.children ? [['添加命令', () => add(false, node.children)], ['添加组', () => add(true, node.children)]] : []),
      ['上移', () => move(-1)], ['下移', () => move(1)], ['删除', remove]
    ]));
    row.addEventListener('dragstart', event => event.dataTransfer.setData('application/x-signal-dock-command', node.id));
    row.addEventListener('dragover', event => { if (event.dataTransfer.types.includes('application/x-signal-dock-command')) event.preventDefault(); });
    row.addEventListener('drop', event => {
      const source = nodes.find(item => item.node.id === event.dataTransfer.getData('application/x-signal-dock-command'));
      if (!source || source.node === node || flatCommands(source.node.children ?? []).some(item => item.node === node)) return;
      event.preventDefault();
      source.parent.splice(source.parent.indexOf(source.node), 1);
      if (node.children) node.children.push(source.node);
      else parent.splice(parent.indexOf(node), 0, source.node);
      save();
    });
    tree.append(row);
  }
  if (!nodes.length) tree.append(el('div', 'hint', t('创建命令后可绑定到按钮、滑条、摇杆或状态灯。')));
  return tree;
}

const THEME_OPTIONS = [['auto', '跟随系统'], ['dark', '深色'], ['light', '浅色'], ['vscode', 'VS Code'],
  ['blue', 'Blue Iron'], ['eye', 'Eye Care'], ['violet', 'Violet']];
const THEME_COLORS = ['bg', 'surface', 'panel', 'raised', 'ink', 'muted', 'accent', 'edge', 'grid'];

export function appearanceSettings(state, save, rpc) {
  const open = () => appearanceSettings(state, save, rpc);
  const theme = state.themes.find(item => item.id === state.appearance.theme);
  const specs = [
    select('theme', '主题', [...THEME_OPTIONS, ...state.themes.map(item => [item.id, item.name])]),
    num('scale', '界面缩放', 0.75, 1.75, 0.05), line('font', '界面字体'),
    num('fps', '刷新上限（0 随屏幕）', 0, 240),
    { key: 'background', label: '背景图', type: 'file' }
  ];
  const root = form(specs, { ...state.appearance, fps: state.fps }, (key, value) => {
    if (key === 'fps') state.fps = value;
    else state.appearance[key] = value;
    save();
    if (['theme', 'background'].includes(key)) open();
  }, () => rpc('loadAsset', { asset: 'background' }));
  if (theme) root.append(form(THEME_COLORS.map(key => ({ key, type: 'color', label: key })), theme.colors, (key, value) => {
    theme.colors[key] = value;
    save();
  }));
  const clone = () => editText('克隆当前主题', '自定义主题', name => {
    const css = getComputedStyle(document.body);
    const colors = Object.fromEntries(THEME_COLORS.map(key => [key, css.getPropertyValue('--' + key).trim()]));
    const item = { id: crypto.randomUUID(), name, colors, base: document.body.dataset.theme };
    state.themes.push(item);
    state.appearance.theme = item.id;
    save();
    open();
  });
  modal('外观与性能', root, [
    button('克隆当前主题', clone),
    ...(theme ? [button('导出主题', () => rpc('saveArchive', { kind: 'theme' }))] : []),
    button('复位外观', () => {
      state.appearance = { theme: 'auto', scale: 1, font: '', background: '' };
      state.fps = 0;
      save();
      open();
    })
  ]);
}

export function applyAppearance(state) {
  const { appearance } = state;
  const body = document.body;
  const theme = state.themes.find(item => item.id === appearance.theme);
  body.dataset.theme = appearance.theme === 'auto'
    ? body.classList.contains('vscode-light') ? 'light' : 'dark'
    : theme?.base ?? appearance.theme;
  body.style.setProperty('--scale', appearance.scale);
  body.style.setProperty('--workbench-font', appearance.font || '');
  for (const key of THEME_COLORS) body.style.setProperty('--' + key, theme?.colors?.[key] ?? '');
  const asset = appearance.background;
  const match = /\.(png|jpe?g|webp)$/i.exec(asset?.name ?? '');
  const data = typeof asset?.data === 'string' && /^[a-zA-Z0-9+/=]+$/.test(asset.data) ? asset.data : '';
  body.style.setProperty('--board-image', match && data ? 'url(data:image/' + (match[1].toLowerCase().startsWith('jp') ? 'jpeg' : match[1]) + ';base64,' + data + ')' : '');
  body.style.setProperty('--board-size', match && data ? 'cover' : '');
}
