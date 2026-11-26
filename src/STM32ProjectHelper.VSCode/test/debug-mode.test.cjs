'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createRequire } = require('node:module');

function fixture({ mode = 'launch', apply = true, buildSucceeds = true } = {}) {
  const file = path.resolve(__dirname, '..', 'development-tools.js');
  const load = createRequire(file), dialogs = [], starts = [], calls = [];
  const configuration = { request: mode, executable: 'D:\\示例工程\\build\\Profile\\firmware.elf', preLaunchTask: mode === 'launch' ? 'One-click Build' : '' };
  const vscode = {
    extensions: { getExtension: () => ({}) },
    window: { showWarningMessage: async (...args) => { dialogs.push(args); return undefined; } },
    debug: { startDebugging: async (...args) => { starts.push(args); return true; } }
  };
  const context = { module: { exports: {} }, require: name => name === 'vscode' ? vscode : load(name), Buffer, setTimeout, clearTimeout };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), context);
  const workbench = {
    state: { options: mode === 'launch' ? {} : { debugMode: mode } }, folder: { uri: { fsPath: 'D:\\示例工程' } },
    options: () => ({ firmwareElfPath: workbench.state.options.firmwareElfPath }),
    t: (value, ...args) => value.replace(/\{(\d+)\}/g, (_, index) => args[Number(index)]),
    assertSession() {}, preview() {}, applyPreview: async () => apply, rememberBuildSelection: async () => {},
    showBuildResult: result => { if (result.success) workbench.state.options.firmwareElfPath = configuration.executable; },
    call: async (command, options) => {
      calls.push({ command, options });
      if (command === 'build') return { success: buildSucceeds, firmware: [configuration.executable] };
      return { changes: [{ path: 'launch.json' }], configuration };
    }
  };
  const tools = Object.create(context.module.exports.DevelopmentTools.prototype);
  tools.workbench = workbench;
  return { tools, dialogs, starts, calls, configuration };
}

test('first and subsequent debug launches build and use the current ELF without confirmation', async () => {
  const f = fixture();
  await f.tools.execute('startDebug', {}, 1);
  assert.equal(f.calls[0].options.debugMode, 'launch');
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.calls[0].options.ensureFirmware, true);
  assert.equal(f.starts.length, 1);
  assert.equal(f.starts[0][1].executable, f.configuration.executable);
  assert.equal(f.starts[0][1].preLaunchTask, undefined, 'already built through the helper');
  assert.equal(f.configuration.preLaunchTask, 'One-click Build', 'saved configuration still has a F5 build task');
  await f.tools.execute('startDebug', {}, 1);
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.starts.length, 2);
  assert.deepEqual(f.calls.map(call => call.command), ['build', 'previewDebug', 'build', 'previewDebug']);
});

test('attach-only starts without building or asking for confirmation', async () => {
  const f = fixture({ mode: 'attach' });
  await f.tools.execute('startDebug', {}, 1);
  assert.equal(f.calls[0].options.debugMode, 'attach');
  assert.deepEqual(f.calls.map(call => call.command), ['previewDebug']);
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.starts.length, 1);
});

test('an incomplete configuration write stops the debugger', async () => {
  const cancelWrite = fixture({ apply: false });
  await cancelWrite.tools.execute('startDebug', {}, 1);
  assert.equal(cancelWrite.dialogs.length, 0);
  assert.equal(cancelWrite.starts.length, 0);
});

test('failed build does not write debug files or start Cortex-Debug with the previous ELF', async () => {
  const f = fixture({ buildSucceeds: false });
  const result = await f.tools.execute('startDebug', {}, 1);
  assert.equal(result.success, false);
  assert.deepEqual(f.calls.map(call => call.command), ['build']);
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.starts.length, 0);
});

test('an unknown debug request cannot start the debugger', async () => {
  const f = fixture({ mode: 'custom' });
  await assert.rejects(f.tools.execute('startDebug', {}, 1), /launch 或 attach/);
  assert.equal(f.dialogs.length, 0);
  assert.equal(f.starts.length, 0);
});
