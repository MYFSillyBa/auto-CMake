'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createRequire } = require('node:module');

function fixture(mode = 'launch', accept = true, apply = true) {
  const file = path.resolve(__dirname, '..', 'development-tools.js');
  const load = createRequire(file), dialogs = [], starts = [], calls = [];
  const configuration = { request: mode, executable: 'D:\\示例工程\\build\\Profile\\firmware.elf', preLaunchTask: mode === 'launch' ? 'One-click Build' : '' };
  const vscode = {
    extensions: { getExtension: () => ({}) },
    window: { showWarningMessage: async (...args) => { dialogs.push(args); return accept ? args[2] : undefined; } },
    debug: { startDebugging: async (...args) => { starts.push(args); return true; } }
  };
  const context = { module: { exports: {} }, require: name => name === 'vscode' ? vscode : load(name), Buffer, setTimeout, clearTimeout };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), context);
  const workbench = {
    state: { options: mode === 'launch' ? {} : { debugMode: mode } }, folder: {}, options: () => ({}),
    t: (value, ...args) => value.replace(/\{(\d+)\}/g, (_, index) => args[Number(index)]),
    assertSession() {}, preview() {}, applyPreview: async () => apply,
    call: async (command, options) => { calls.push({ command, options }); return { changes: [{}], configuration }; }
  };
  const tools = Object.create(context.module.exports.DevelopmentTools.prototype);
  tools.workbench = workbench;
  return { tools, dialogs, starts, calls, configuration };
}

test('normal debug explicitly confirms programming the selected firmware before starting', async () => {
  const f = fixture();
  await f.tools.execute('startDebug', {}, 1);
  assert.equal(f.calls[0].options.debugMode, 'launch');
  assert.match(f.dialogs[0][0], /下载所选固件/);
  assert.match(f.dialogs[0][1].detail, /写入芯片/);
  assert.ok(f.dialogs[0][1].detail.includes(f.configuration.executable));
  assert.ok(!f.dialogs[0][1].detail.includes('不会写入固件'));
  assert.equal(f.starts.length, 1);
  assert.equal(f.starts[0][1], f.configuration);
});

test('attach-only is sent to the backend and described as no build or download', async () => {
  const f = fixture('attach');
  await f.tools.execute('startDebug', {}, 1);
  assert.equal(f.calls[0].options.debugMode, 'attach');
  assert.match(f.dialogs[0][0], /不编译、不下载固件/);
  assert.match(f.dialogs[0][1].detail, /板上固件与所选 ELF 一致/);
  assert.equal(f.starts.length, 1);
});

test('cancelling the hardware confirmation or configuration write never starts debugging', async () => {
  const cancelHardware = fixture('launch', false);
  await cancelHardware.tools.execute('startDebug', {}, 1);
  assert.equal(cancelHardware.dialogs.length, 1);
  assert.equal(cancelHardware.starts.length, 0);
  const cancelWrite = fixture('launch', true, false);
  await cancelWrite.tools.execute('startDebug', {}, 1);
  assert.equal(cancelWrite.dialogs.length, 0);
  assert.equal(cancelWrite.starts.length, 0);
});

test('an unknown debug request cannot reach the hardware confirmation or debugger', async () => {
  const f = fixture('custom');
  await assert.rejects(f.tools.execute('startDebug', {}, 1), /launch 或 attach/);
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.starts.length, 0);
});
