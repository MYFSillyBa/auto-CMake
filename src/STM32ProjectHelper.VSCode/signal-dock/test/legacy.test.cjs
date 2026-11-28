const assert = require('node:assert/strict');
const { test } = require('node:test');
const { migrateLegacy } = require('../src/legacy.cjs');

const doc = (type, ctx) => ({ type, vnumber: 100, ctx });
const ctx = contents => ({ ctx: contents });

test('v100 commands preserve nested groups, notes, repeat counts and templates', () => {
  const result = migrateLegacy(doc('cmds', { is_group: true, subCmds: [
    { name: 'Capture', is_group: true, loop_on: true, loop_count: 2, loop_ms: 120, subCmds: [
      { name: 'Start', intro: 'Device start', hex_on: false, cmd_hex: 'go %.2f\\r\\n' },
      { name: 'Stop', is_hide: true, hex_on: true, cmd_hex: '01 %% ff' }
    ] }
  ] }));
  assert.equal(result.kind, 'commands');
  assert.equal(result.value[0].id, 'legacy-command-0');
  assert.equal(result.value[0].count, 2);
  assert.equal(result.value[0].delayMs, 120);
  const [start, stop] = result.value[0].children;
  assert.equal(start.name, 'Start');
  assert.equal(start.note, 'Device start');
  assert.equal(start.text, 'go %.2f\r\n');
  assert.equal(stop.hex, true);
  assert.equal(stop.text, '01 %% ff');
  assert.equal(stop.hidden, true);
});

test('v100 layout flattens windows and maps wave and plug-in contexts', () => {
  const box = ctx({ '.': { x: 200, y: 50, width: 400, height: 200 } });
  const result = migrateLegacy(doc('tabviews', [{ width: 800, height: 500, tabs: [
    { name: 'Instruments', widgets: [
      { path: 'WaveChart', ctx: {
        '.': box, rbw: ctx({ '.': { lines: [1, 4], line_width: 3 } }),
        freq_check: { checked: true }
      } },
      { path: 'plugins/widgets/ExtraButtonDelay/ExtraButtonDelay.qml', ctx: {
        '.': { ...box, delay: 230, threshold: 0.75 }, ch_menu: ctx({ index: 4 }),
        cmd_menu: ctx({ indices: [0, 1] }), name_menu: ctx({ name: 'Trigger' })
      } },
      { path: 'bound_bt', ctx: {
        '.': box, argument_menu: ctx({ model_ctx: [{ float_value: 22 }, { float_value: 12 }] })
      } },
      { path: 'thirdParty' }
    ] }
  ] }]));
  assert.equal(result.kind, 'layout');
  assert.equal(result.value[0].title, 'Instruments');
  const [wave, button, pressButton] = result.value[0].widgets;
  assert.deepEqual([wave.x, wave.y, wave.w, wave.h], [25, 10, 50, 40]);
  assert.equal(wave.mode, 'spectrum');
  assert.deepEqual(wave.channels, [1, 4]);
  assert.equal(wave.lineWidth, 3);
  assert.equal(button.kind, 'delayButton');
  assert.equal(button.title, 'Trigger');
  assert.deepEqual(button.channels, [4]);
  assert.equal(button.commandId, 'legacy-command-0.1');
  assert.equal(button.delayMs, 230);
  assert.equal(pressButton.trigger, 'press');
  assert.equal(pressButton.sendOnRelease, true);
  assert.equal(pressButton.value, 22);
  assert.equal(pressButton.offValue, 12);
  assert.match(result.warnings.join('\n'), /thirdParty/);
});

test('v100 native settings preserve HI05 link, terminal and channel display settings', () => {
  const result = migrateLegacy(doc('config', {
    datahub_global: { fps: 30, global_decimal: 6, timeout_ms: 12 },
    text_buffer: { encoder: 'GBK', list_size: 18 },
    wave_view: ctx({
      color_bar: ctx({ '.': { delta_t: 10, 'buffer_max_size_input.text': '50000' } }),
      left_panel: ctx({ pal: ctx({
        serial: { port: 'COM20', baud: '100000', flow_control: 'None', parity: 'None', databits: '8', stopbits: '1', break_enabled: true },
        link_type_combo: 0, protocol_combo: 'JustFloat'
      }) }),
      tx_rx_zone: ctx({ '.': { hex_on: true }, append_combo: { currentIndex: 1 }, tx_show_echo: { checked: true } }),
      settingsPanel: ctx({ '.': { settings_ctx: [
        { name: 'roll', color: '#fa0022', scale: 2, xoffset: 3, yoffset: -1, is_draw: false, decimal: 4 },
        { name: 'pitch', decimal: -7 }
      ] } })
    })
  }));
  assert.equal(result.kind, 'settings');
  assert.equal(result.value.dtMs, 10);
  assert.equal(result.value.capacity, 50000);
  assert.equal(result.value.connection.kind, 'serial');
  assert.equal(result.value.connection.engine, 'justfloat');
  assert.equal(result.value.connection.path, 'COM20');
  assert.equal(result.value.connection.baudRate, 100000);
  assert.equal(result.value.connection.brk, true);
  assert.equal(result.value.terminal.encoding, 'GBK');
  assert.equal(result.value.terminal.groupMs, 12);
  assert.equal(result.value.terminal.pages, 18);
  assert.equal(result.value.terminal.hex, true);
  assert.deepEqual(result.value.channelSettings[0], {
    name: 'roll', color: '#fa0022', scale: 2, xOffset: 3, offset: -1, visible: false, decimals: 4
  });
  assert.equal(result.value.channelSettings[1].decimals, 6);
});

test('legacy migration refuses unsupported document versions and types', () => {
  assert.throws(() => migrateLegacy({ ...doc('cmds', {}), vnumber: 101 }), /version/i);
  assert.throws(() => migrateLegacy(doc('unknown', {})), /type/i);
});
