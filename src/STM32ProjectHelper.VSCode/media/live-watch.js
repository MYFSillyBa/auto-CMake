'use strict';
(() => {
  const vscode = acquireVsCodeApi();
  const $ = id => document.getElementById(id);
  const send = (type, details = {}) => vscode.postMessage({ type, ...details });
  const labels = {
    zh: {
      name: '名称Name', value: '数值Value', address: '地址Address', type: '类型Type',
      connected: '已连接', connecting: '正在连接', disconnected: '未连接', connect: '连接', disconnect: '断开',
      pause: '暂停刷新', resume: '继续刷新', options: '选项', submit: '添加',
      refreshRate: '刷新率', unlimited: '无上限',
      placeholder: '<输入变量名>', inputLabel: '变量名',
      maximizeSidebar: '占满侧栏', restoreSidebar: '恢复工程导航',
      missing: '等待 ELF 固件', expand: '展开成员', collapse: '折叠成员', edit: '写入数值',
      remove: '移除变量', auto: '自动', hex: '十六进制', decimal: '十进制', float: '浮点数', binary: '二进制',
      choose: '选择要使用的 Tcl 端口', unknown: '符号查找失败', notfound: '找不到符号',
      missingElf: '未找到 ELF 固件。请通过「选项」选择文件。',
      manyElf: '找到多个 ELF 固件。请选择当前调试使用的文件。',
      missingGdb: '无法运行 ARM GDB。请检查工具链路径。',
      duplicate: '该变量已在观察列表中。',
      invalid: '输入的数值与变量类型不匹配。',
      noWrite: '无法写入当前变量，请先确认地址和实时读数。',
      failedRead: '读取目标内存失败', failedWrite: '写入目标内存失败', failedConnect: '连接 OpenOCD 失败',
      pointerOffline: '指针成员需要连接目标芯片后才能求值。',
      pointerInvalid: '指针指向可访问 RAM 之外。请核对固件和指针初始化状态。',
      pointerUnreadable: '无法读取指针或解析其基址和类型。',
    },
    en: {
      name: 'Name', value: 'Value', address: 'Address', type: 'Type', connected: 'Connected', connecting: 'Connecting',
      disconnected: 'Waiting for connection', connect: 'Connect', disconnect: 'Disconnect', pause: 'Pause refresh',
      resume: 'Resume refresh', options: 'Options', submit: 'Add', placeholder: '<Enter variable name>',
      refreshRate: 'Refresh rate', unlimited: 'Unlimited',
      inputLabel: 'Variable name', maximizeSidebar: 'Fill sidebar', restoreSidebar: 'Restore project navigation',
      missing: 'Waiting for ELF firmware',
      expand: 'Expand members', collapse: 'Collapse members', edit: 'Write value', remove: 'Remove variable',
      auto: 'Auto', hex: 'Hexadecimal', decimal: 'Decimal', float: 'Float', binary: 'Binary',
    },
  };
  const rows = new Map();
  const formats = ['auto', 'hex', 'decimal', 'float', 'binary'];
  const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
  let t = labels.zh, editing, selection, connected = false, language = 'zh-CN';
  const menu = $('menu'), table = $('rows');
  function errorText(error) {
    if (!error || language === 'en') return error || '';
    if (/multiple OpenOCD Tcl servers/i.test(error)) return t.choose;
    if (/multiple ELF firmware/i.test(error)) return t.manyElf;
    if (/(?:No|Selected) ELF firmware|No ELF file/i.test(error)) return t.missingElf;
    if (/already being watched/i.test(error)) return t.duplicate;
    if (/Symbol not found|No symbol/i.test(error)) return t.notfound;
    if (/needs active target connection/i.test(error)) return t.pointerOffline;
    if (/non-RAM value/i.test(error)) return t.pointerInvalid;
    if (/pointer (?:base|type|value)|not a raw pointer/i.test(error)) return t.pointerUnreadable;
    if (/Cannot write/i.test(error)) return t.noWrite;
    if (/^Invalid value:/i.test(error)) return t.invalid;
    if (/Write failed:/i.test(error)) return t.failedWrite;
    if (/Memory read failed|failed to read memory/i.test(error)) return t.failedRead;
    if (/connect failed|connection (?:closed|timeout)/i.test(error)) return t.failedConnect;
    if (/GDB process|GDB executable|spawn|ENOENT|not recognized/i.test(error)) return t.missingGdb;
    if (/^Ignored invalid/i.test(error)) return '部分内存范围设置无效。请使用形如 0x20000000-0x20020000 的区间。';
    return t.unknown;
  }
  function select(row) {
    selection?.node.removeAttribute('data-selected');
    selection = row;
    if (row) row.node.dataset.selected = 'true';
  }
  function canWrite(row) {
    return connected && !row.data.hasChildren && !!row.data.value && !row.data.error && !!row.data.address;
  }
  function edit(row) {
    if (!canWrite(row) || editing) return;
    const input = document.createElement('input');
    input.setAttribute('aria-label', t.edit);
    input.value = row.data.value;
    input.maxLength = 120;
    const close = () => {
      if (editing !== row) return;
      editing = undefined;
      update(row, false);
    };
    input.onblur = close;
    input.onkeydown = event => {
      if (event.key !== 'Enter' && event.key !== 'Escape') return;
      event.preventDefault();
      if (event.key === 'Enter' && input.value.trim()) send('write', { id: row.data.id, value: input.value.trim() });
      close();
      row.node.focus();
    };
    editing = row;
    row.value.replaceChildren(input);
    input.focus(); input.select();
  }
  function showMenu(row, x, y) {
    menu.replaceChildren();
    const entries = formats.map(value => [t[value], () => send('format', { id: row.data.id, value })]);
    if (canWrite(row)) entries.push([t.edit, () => edit(row)]);
    if (!row.data.depth) entries.push([t.remove, () => send('remove', { id: row.data.id })]);
    for (const [label, run] of entries) {
      const button = document.createElement('button');
      button.textContent = label;
      button.onclick = () => { menu.hidden = true; run(); };
      menu.append(button);
    }
    menu.hidden = false;
    menu.style.left = Math.max(0, Math.min(x, innerWidth - menu.offsetWidth - 4)) + 'px';
    menu.style.top = Math.max(0, Math.min(y, innerHeight - menu.offsetHeight - 4)) + 'px';
    menu.querySelector('button')?.focus();
  }
  function makeRow(data) {
    const node = document.createElement('tr');
    node.tabIndex = 0;
    const row = { data, node, name: node.insertCell(), value: node.insertCell(), address: node.insertCell(), type: node.insertCell() };
    row.disclosure = document.createElement('button');
    row.disclosure.className = 'disclosure';
    row.nameText = document.createElement('span');
    row.name.append(row.disclosure, row.nameText);
    row.disclosure.onclick = () => send('expand', { id: row.data.id });
    row.value.ondblclick = () => edit(row);
    node.onclick = () => select(row);
    node.onfocus = () => select(row);
    node.oncontextmenu = event => { event.preventDefault(); select(row); showMenu(row, event.clientX, event.clientY); };
    node.onkeydown = event => {
      if (event.target !== node) return;
      if (event.key === 'Delete' && !row.data.depth) send('remove', { id: row.data.id });
      else if (event.key === 'F2') edit(row);
      else if (event.key === 'ContextMenu' || event.shiftKey && event.key === 'F10') {
        event.preventDefault(); const box = node.getBoundingClientRect(); showMenu(row, box.left, box.bottom);
      } else if (event.key === 'Enter' && row.data.hasChildren) send('expand', { id: row.data.id });
    };
    return row;
  }
  function set(node, value) { if (node.textContent !== value) node.textContent = value; }
  function update(row, flash) {
    const data = row.data;
    row.name.style.paddingInlineStart = Math.min(12, data.depth) * 9 + 4 + 'px';
    row.disclosure.disabled = !data.hasChildren;
    row.disclosure.textContent = data.hasChildren ? data.expanded ? '▾' : '▸' : '';
    row.disclosure.setAttribute('aria-label', data.expanded ? t.collapse : t.expand);
    row.disclosure.setAttribute('aria-expanded', String(data.expanded));
    set(row.nameText, data.name);
    row.name.title = data.name;
    if (editing !== row) {
      const value = data.error ? errorText(data.error) : data.value || '—';
      if (flash && !data.error && !reduceMotion.matches &&
          row.value.textContent && row.value.textContent !== value && row.animation?.playState !== 'running') {
        row.animation = row.value.animate([{ backgroundColor: 'var(--change)' }, { backgroundColor: 'transparent' }], { duration: 330 });
      }
      set(row.value, value);
      row.value.title = value;
    }
    set(row.address, data.address || '—');
    set(row.type, data.typeName || '—');
    row.address.title = data.address;
    row.type.title = data.typeName;
  }
  window.addEventListener('message', event => {
    const state = event.data;
    if (state?.type !== 'frame') return;
    const languageChanged = language !== state.language;
    language = state.language;
    t = language === 'en' ? labels.en : labels.zh;
    document.documentElement.lang = language;
    for (const key of ['name', 'value', 'address', 'type']) set($('label-' + key), t[key]);
    $('name').placeholder = t.placeholder;
    $('name').setAttribute('aria-label', t.inputLabel);
    const sidebarButton = $('sidebar-size');
    const sidebarLabel = state.sidebarExpanded ? t.restoreSidebar : t.maximizeSidebar;
    sidebarButton.hidden = false;
    set(sidebarButton, state.sidebarExpanded ? '⤡' : '⤢');
    sidebarButton.title = sidebarLabel;
    sidebarButton.setAttribute('aria-label', sidebarLabel);
    sidebarButton.setAttribute('aria-pressed', String(state.sidebarExpanded));
    document.body.dataset.sidebarExpanded = String(state.sidebarExpanded);
    connected = state.state === 'connected';
    $('status').dataset.state = state.state;
    set($('state'), t[state.state]);
    set($('endpoint'), state.endpoint);
    set($('connect'), connected ? t.disconnect : t.connect);
    $('connect').disabled = state.state === 'connecting';
    set($('pause'), state.paused ? t.resume : t.pause);
    $('pause').setAttribute('aria-pressed', String(state.paused));
    const rate = $('poll-rate');
    set($('poll-unlimited'), t.unlimited);
    rate.setAttribute('aria-label', t.refreshRate);
    rate.title = state.pollInterval ? `${t.refreshRate} · ${state.pollInterval} ms` : `${t.refreshRate} · ${t.unlimited}`;
    if (rate.value !== String(state.pollInterval)) rate.value = String(state.pollInterval);
    set($('options-text'), t.options);
    $('options').setAttribute('aria-label', t.options);
    $('options').title = t.options;
    set($('submit'), t.submit);
    const firmware = state.firmware?.split(/[\\/]/).pop();
    set($('firmware'), firmware ? 'ELF · ' + firmware : t.missing);
    $('firmware').title = state.firmware || t.missing;
    $('notice').hidden = !state.notice;
    set($('notice'), errorText(state.notice));
    for (const data of state.changed) {
      const existing = rows.get(data.id);
      const row = existing || makeRow(data);
      row.data = data;
      update(row, !!existing);
      rows.set(data.id, row);
    }
    if (state.order) {
      const ordered = new Set(state.order);
      for (const [id, row] of rows) if (!ordered.has(id)) {
        if (selection === row) select(undefined);
        if (editing === row) editing = undefined;
        rows.delete(id);
      }
      table.replaceChildren(...state.order.map(id => rows.get(id).node));
    }
    if (languageChanged) for (const row of rows.values()) update(row, false);
    requestAnimationFrame(() => send('ack', { id: state.id }));
  });
  $('add').onsubmit = event => {
    event.preventDefault();
    const input = $('name'), name = input.value.trim();
    if (name) { send('add', { name }); input.value = ''; }
  };
  $('connect').onclick = () => send('connect');
  $('pause').onclick = () => send('pause');
  $('poll-rate').onchange = event => send('pollInterval', { value: Number(event.target.value) });
  $('sidebar-size').onclick = () => send('toggleSidebar');
  $('options').onclick = () => send('options');
  document.addEventListener('pointerdown', event => { if (!menu.contains(event.target)) menu.hidden = true; });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') menu.hidden = true; });
  send('ready');
})();
