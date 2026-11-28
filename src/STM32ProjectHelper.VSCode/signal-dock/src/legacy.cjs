const path = require('node:path');

const WIDGETS = {
  wavechart: 'wave',
  bound_bt: 'button',
  extrabuttondelay: 'delayButton',
  extrabuttontoggle: 'toggle',
  slider: 'slider',
  extradial: 'dial',
  materialdial: 'materialDial',
  materialjoystick: 'joystick',
  light: 'light',
  gauge: 'gauge',
  circular_gauge: 'circularGauge',
  cube: 'cube',
  image: 'image'
};

function context(node) {
  return node?.ctx ?? node ?? {};
}

function properties(node) {
  const value = context(node);
  return value['.'] ?? value;
}

function number(value) {
  if (value === undefined || value === null || value === '') return undefined;
  const n = Number(value);
  return Number.isFinite(n) ? n : undefined;
}

function defined(obj) {
  return Object.fromEntries(Object.entries(obj).filter(([, value]) => value !== undefined));
}

function channelIndex(value) {
  if (typeof value === 'string') value = value.trim().replace(/^[iI](?=\d+$)/, '');
  const n = number(value);
  return Number.isInteger(n) && n >= 0 ? n : undefined;
}

function indices(value) {
  if (Array.isArray(value)) return value.map(channelIndex).filter(index => index !== undefined);
  const index = channelIndex(value);
  return index === undefined ? [] : [index];
}

function commandId(position) {
  return 'legacy-command-' + position.join('.');
}

function escapedText(text) {
  const control = { n: '\n', r: '\r', t: '\t', '0': '\0', a: '\x07', b: '\b', f: '\f', v: '\v', '\\': '\\' };
  return text.replace(/\\(?:x([0-9a-f]{2})|u([0-9a-f]{4})|([nrt0abfv\\]))/gi, (_, hex, unicode, literal) =>
    literal ? control[literal.toLowerCase()] : String.fromCharCode(parseInt(hex ?? unicode, 16)));
}

function migrateCommands(root) {
  function command(source, position) {
    const hex = Boolean(source.hex_on);
    const text = String(source.cmd_hex ?? source.cmd ?? '');
    const item = {
      id: commandId(position),
      name: String(source.name ?? 'Command'),
      note: String(source.intro ?? ''),
      text: hex ? text : escapedText(text),
      hex,
      eol: 'none',
      count: source.loop_on ? (number(source.loop_count) ?? 1) : 1,
      delayMs: number(source.loop_ms) ?? 100,
      hidden: Boolean(source.is_hide ?? source.hidden ?? (source.visible === false))
    };
    if (source.is_group || Array.isArray(source.subCmds)) {
      item.children = (source.subCmds ?? []).map((child, index) => command(child, [...position, index]));
    }
    return item;
  }

  const commands = Array.isArray(root) ? root :
    Array.isArray(root.subCmds) && (!root.name || root.name === 'group') ? root.subCmds : [root];
  return commands.map((source, index) => command(source, [index]));
}

function menuChannel(node) {
  const value = properties(node);
  const list = indices(value.indecies ?? value.indices ?? value.index ?? value.ch_index ?? value.channel ?? value);
  return list.length ? list.at(-1) : undefined;
}

function menuCommand(node) {
  const value = properties(node);
  const position = value.bind_cmd_index ?? value.cmd_index ?? value.indecies ?? value.indices ?? value.index ?? value;
  const parts = typeof position === 'string' ? position.split(/[.,/]/) : position;
  const parsed = indices(parts);
  return parsed.length && (!Array.isArray(parts) || parts.length === parsed.length) ? commandId(parsed) : undefined;
}

function widgetBox(root, reference, kind, index) {
  const base = properties(root.ctx ?? root);
  const resize = properties(context(root.ctx).resize_border);
  if (resize.full_screen) return { x: 0, y: 0, w: 100, h: 100 };
  const target = resize.status?.target ?? {};
  const width = number(base.width ?? target.width);
  const height = number(base.height ?? target.height);
  const clamp = (value, maximum) => Math.max(0, Math.min(value, maximum));
  const w = width > 0 && reference.width > 0 ? clamp(100 * width / reference.width, 100) : kind === 'wave' ? 100 : 25;
  const h = height > 0 && reference.height > 0 ? clamp(100 * height / reference.height, 100) : kind === 'wave' ? 60 : 25;
  const pixelX = number(base.x ?? target.x);
  const pixelY = number(base.y ?? target.y);
  const x = pixelX !== undefined && reference.width > 0 ? clamp(100 * pixelX / reference.width, 100 - w) : (index % 4) * (100 - w) / 3;
  const y = pixelY !== undefined && reference.height > 0 ? clamp(100 * pixelY / reference.height, 100 - h) : (Math.floor(index / 4) % 4) * (100 - h) / 3;
  return { x, y, w, h };
}

function waveOptions(saved, root) {
  const series = properties(saved.rbw);
  const xy = root.xy_mode === true;
  const xAxis = properties(xy ? saved.axis_x_wave2 : saved.axis_x_wave);
  const yAxis = properties(saved.axis_y_wave);
  const span = number(xAxis.right_index) - number(xAxis.left_index);
  let mode = xy ? 'xy' : 'time';
  if (saved.histog_check?.checked) mode = 'histogram';
  else if (saved.freq_check?.checked) mode = 'spectrum';
  else if (saved.bar_check?.checked) mode = 'bar';
  return defined({
    channels: indices(series.lines),
    mode,
    history: mode === 'time' && span > 0 ? Math.round(span) + 1 : undefined,
    xChannel: xy ? menuChannel(saved.x_ch_menu) : undefined,
    lineWidth: number(series.line_width),
    axisAuto: saved.auto_bt_menu?.locked !== undefined ? !saved.auto_bt_menu.locked : undefined,
    yMin: number(yAxis.bottom_value),
    yMax: number(yAxis.top_value),
    xMin: number(xAxis.min_value),
    xMax: number(xAxis.max_value),
    xUnit: xAxis.unit_text,
    hideXAxis: saved.hide_axis_x_check?.checked,
    hideYAxis: saved.hide_axis_y_check?.checked,
    hideBorder: saved.hide_border_check?.checked,
    bins: number(series.histog_gap_num)
  });
}

function commandArgument(event) {
  const value = Array.isArray(event?.float_values) ? event.float_values[0] : event?.float_value;
  return number(value);
}

function migrateWidget(source, tabId, index, reference, warnings) {
  const saved = context(source);
  const name = path.win32.basename(String(source.path ?? source.name ?? '')).toLowerCase().replace(/\.qml$/, '');
  const kind = WIDGETS[name];
  if (!Object.hasOwn(WIDGETS, name)) {
    warnings.push('Unsupported legacy widget: ' + (source.path ?? source.name ?? '(unnamed)'));
    return null;
  }

  const root = saved['.'] ?? saved;
  const label = properties(saved.name_menu);
  const channel = menuChannel(saved.ch_menu);
  const widget = {
    id: tabId + '-widget-' + index,
    kind,
    title: String(root.title || label.name || label.text || label.custom_name || source.name || ''),
    channels: channel === undefined ? [] : [channel],
    commandId: menuCommand(saved.cmd_menu) ?? '',
    ...widgetBox(root, reference, kind, index)
  };
  if (!widget.title) delete widget.title;
  if (kind === 'wave') return { ...widget, ...waveOptions(saved, root) };

  if (['button', 'delayButton', 'toggle'].includes(kind)) {
    const events = properties(saved.argument_menu).model_ctx ?? [];
    const pressed = commandArgument(events[0]) ?? 1;
    const released = commandArgument(events[1]) ?? 0;
    if (kind === 'toggle') Object.assign(widget, { onValue: pressed, offValue: released });
    else Object.assign(widget, { value: pressed, offValue: released });
    if (kind === 'button') Object.assign(widget, { trigger: 'press', sendOnRelease: true });
    if (kind === 'delayButton' && root.bound) widget.sendOnRelease = true;
  }

  if (kind === 'cube') {
    const bindings = [saved.x_menu, saved.y_menu, saved.z_menu].map(menuChannel);
    if (root.quaternion_mode) bindings.push(menuChannel(saved.scalar_menu));
    if (bindings.some(value => value !== undefined)) widget.channels = bindings.map(value => value ?? -1);
    widget.rotation = root.quaternion_mode ? 'quaternion' : 'euler';
    if (root.angle_or_radian !== undefined) widget.angleUnit = root.angle_or_radian ? 'deg' : 'rad';
    if (root.is_show_world_axis !== undefined) widget.showAxes = root.is_show_world_axis;
    if (saved.file_menu?.selected_file) warnings.push('The legacy Cube model must be reselected: ' + saved.file_menu.selected_file);
  }

  if (kind === 'image') {
    const imageId = channelIndex(root.img_index);
    if (imageId !== undefined) widget.imageId = imageId;
    if (root.effect_file) {
      const filename = path.win32.basename(root.effect_file).toLowerCase();
      if (filename.includes('blackandwhite')) {
        widget.filter = 'threshold';
        const threshold = number(root.parameters?.[0]);
        if (threshold !== undefined) widget.threshold = Math.round(threshold * 255);
      } else if (filename.includes('sobel')) {
        widget.filter = 'sobel';
      } else if (!filename.includes('passthrough')) {
        warnings.push('Unsupported legacy image effect: ' + root.effect_file);
      }
    }
  }

  if (kind === 'light' && root.three_color_mode) warnings.push('Legacy three-color light was imported with two states.');

  const appearance = properties(saved.theme);
  const slider = properties(saved.slider_inside);
  return { ...widget, ...defined({
    min: number(root.from ?? root.bottom_value),
    max: number(root.to ?? root.top_value),
    step: number(root.step_size),
    value: number(slider.target_value),
    decimals: number(root.decimal),
    delayMs: number(root.delay),
    threshold: number(root.threshold ?? root.threshold_model_ctx?.[0]),
    invert: root.reverse_logic,
    feedback: ['toggle', 'slider', 'dial', 'materialDial'].includes(kind) ? channel !== undefined : undefined,
    spring: kind === 'joystick' ? root.boundEnabled : undefined,
    color: label.color ?? appearance.lineColor,
    onColor: root.light_color,
    hideBorder: root.is_hide_border,
    hideName: root.is_hide_name
  }) };
}

function migrateLayout(type, root, warnings) {
  const windows = type === 'tab' ? [{ tabs: [root] }] :
    type === 'tabview' ? [root] : (Array.isArray(root) ? root : root.windows ?? []);
  if (windows.length > 1) warnings.push('Tabs from separate legacy windows were merged into one workspace.');
  return windows.flatMap((window, windowIndex) => (window.tabs ?? []).map((tab, tabIndex) => {
    const id = 'legacy-tab-' + windowIndex + '-' + tabIndex;
    const widgetList = tab.widgets ?? [];
    const reference = {
      width: number(tab.width ?? window.width),
      height: number(tab.height ?? window.height)
    };
    if (!reference.width || !reference.height) {
      const boxes = widgetList.map(source => {
        const root = properties(source);
        return properties(root.ctx ?? root);
      });
      reference.width ||= Math.max(0, ...boxes.map(box => (number(box.x) ?? 0) + (number(box.width) ?? 0)));
      reference.height ||= Math.max(0, ...boxes.map(box => (number(box.y) ?? 0) + (number(box.height) ?? 0)));
    }
    return {
      id,
      title: String(tab.name ?? tab.title ?? 'Legacy layout'),
      widgets: widgetList.map((widget, index) => migrateWidget(widget, id, index, reference, warnings)).filter(Boolean)
    };
  }));
}

function migrateConnection(pal) {
  const kind = ['serial', 'udp', 'tcpClient', 'tcpServer', 'demo'][number(pal.link_type_combo)];
  const engine = ['firewater', 'justfloat', 'rawdata'].find(name => name === String(pal.protocol_combo).toLowerCase());
  const serial = properties(pal.serial);
  const udp = properties(pal.udp);
  const client = properties(pal.tcp_client);
  const server = properties(pal.tcp_server);
  const demo = properties(pal.demo);
  const signals = demo.send_picture ? 'jpg' : demo.send_csv_list ? 'csv' : 'sine';
  const flow = String(serial.flow_control ?? '').toLowerCase();
  const endpoint = kind === 'tcpServer' ? server : client;
  return defined({
    kind,
    engine,
    path: serial.port,
    baudRate: number(serial.baud),
    flowControl: flow ? flow.startsWith('hard') ? 'hardware' : flow.startsWith('soft') ? 'software' : 'none' : undefined,
    parity: serial.parity?.toLowerCase(),
    dataBits: number(serial.databits),
    stopBits: number(serial.stopbits),
    dtr: serial.dtr,
    rts: serial.rts,
    brk: serial.break_enabled,
    localPort: number(udp.local_port),
    remoteHost: udp.remote_ip,
    remotePort: number(udp.remote_port),
    host: endpoint.server_ip || undefined,
    port: number(endpoint.listen_port ?? endpoint.server_port),
    banner: client.banner || undefined,
    demoMs: number(signals === 'jpg' ? demo.picture_timeout : signals === 'csv' ? demo.csv_timeout : demo.timeout),
    demoSignal: pal.demo ? signals : undefined,
    demoDir: signals === 'jpg' ? demo.picture_dir : signals === 'csv' ? demo.csv_dir : undefined
  });
}

function migrateSettings(root) {
  const view = context(root.wave_view);
  const chart = properties(view.color_bar);
  const terminals = context(view.tx_rx_zone);
  const terminal = properties(view.tx_rx_zone);
  const panel = properties(view.settingsPanel);
  const channels = Array.isArray(panel.settings_ctx) ? panel.settings_ctx : [];
  const themeName = properties(root.theme_funcs).theme_name;
  const datahub = properties(root.datahub_global);
  const textBuffer = properties(root.text_buffer);
  return defined({
    dtMs: number(chart.delta_t),
    capacity: number(chart['buffer_max_size_input.text'] ?? context(view.color_bar).buffer_max_size_input?.text ?? chart.size),
    fps: number(datahub.fps),
    channelSettings: Object.fromEntries(channels.map((channel, index) => [index, defined({
      name: channel.name,
      color: channel.color,
      scale: number(channel.scale),
      xOffset: number(channel.xoffset),
      offset: number(channel.yoffset),
      visible: channel.is_draw,
      decimals: number(channel.decimal) >= 0 ? number(channel.decimal) : number(datahub.global_decimal)
    })])),
    connection: migrateConnection(context(context(view.left_panel).pal)),
    terminal: defined({
      hex: terminal.hex_on,
      sendHex: terminal.send_hex_on,
      txEcho: terminals.tx_show_echo?.checked,
      rxEcho: terminals.rx_show_echo?.checked,
      showPlotData: terminals.rx_show_plot_data?.checked,
      showImageData: terminals.rx_show_image_data?.checked,
      timestamps: terminals.t_string_on_check?.checked,
      encoding: textBuffer.encoder,
      groupMs: number(datahub.timeout_ms),
      pages: number(textBuffer.list_size),
      eol: ['none', 'lf', 'crlf', 'cr', 'lfcr'][terminals.append_combo?.currentIndex],
      enterToSend: terminal.enter_to_send,
      count: terminal.loop_on ? number(terminal.loop_count) : 1,
      delayMs: number(terminal.loop_ms)
    }),
    theme: themeName === undefined ? undefined : themeName || 'auto'
  });
}

function migrateLegacy(doc) {
  if (doc?.vnumber !== 100) throw new Error('Unsupported VOFA+ file version: ' + doc?.vnumber);
  if (!doc.ctx || typeof doc.ctx !== 'object') throw new Error('VOFA+ file is missing its context.');
  const warnings = [];
  switch (doc.type) {
    case 'cmds': return { kind: 'commands', value: migrateCommands(doc.ctx), warnings };
    case 'tab':
    case 'tabview':
    case 'tabviews': return { kind: 'layout', value: migrateLayout(doc.type, doc.ctx, warnings), warnings };
    case 'config': return { kind: 'settings', value: migrateSettings(doc.ctx), warnings };
    default: throw new Error('Unsupported VOFA+ file type: ' + doc.type);
  }
}

module.exports = { migrateLegacy };
