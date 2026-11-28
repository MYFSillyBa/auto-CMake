'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const { PassThrough, Writable } = require('node:stream');
const { discoverProjects } = require('../project-discovery');

test('CMake-only STM32 projects activate while ordinary CMake projects are not discovered', async () => {
  const root = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-discovery-regression-')));
  try {
    for (const name of ['board', 'custom/cmake', 'desktop']) fs.mkdirSync(path.join(root, name), { recursive: true });
    fs.writeFileSync(path.join(root, 'board', 'CMakeLists.txt'), 'project(board C)\nadd_compile_definitions(STM32G474xx)\n');
    fs.writeFileSync(path.join(root, 'custom', 'CMakeLists.txt'), 'project(custom C)\nINCLUDE (\n  cmake/board-settings.cmake\n)\n');
    fs.writeFileSync(path.join(root, 'custom', 'cmake', 'board-settings.cmake'), 'add_compile_definitions ( STM32F407xx )\n');
    fs.writeFileSync(path.join(root, 'desktop', 'CMakeLists.txt'), 'project(desktop C)\nadd_executable(desktop main.c) # STM32F407xx\n');
    const candidates = await discoverProjects(root);
    assert.deepEqual(candidates.map(item => item.root), [path.join(root, 'board'), path.join(root, 'custom')]);
    const manifest = require('../package.json');
    assert.ok(manifest.activationEvents.includes('workspaceContains:**/CMakeLists.txt'));
  } finally {
    assert.ok(path.basename(root).startsWith('stm32-discovery-regression-'));
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('refresh follows an edited preset build directory and discards its old ELF selection', async () => {
  const workspace = { getConfiguration: () => ({ get() {} }) };
  const module = { exports: {} };
  const source = fs.readFileSync(path.join(__dirname, '..', 'extension.js'), 'utf8');
  vm.runInNewContext(source + '\nmodule.exports.Workbench = Workbench;', { module, require(name) {
    if (name === 'vscode') return { workspace };
    if (name === 'path') return path;
    return {};
  } });
  const workbench = Object.create(module.exports.Workbench.prototype);
  const root = path.resolve('firmware');
  const oldDirectory = path.join(root, 'build', 'previous');
  const newDirectory = path.join(root, 'build', 'updated');
  const preset = { configurePreset: 'Field', buildDirectory: oldDirectory };
  const state = { inspection: { project: preset }, options: { configurePreset: 'Field', buildDirectory: oldDirectory,
    firmwareElfPath: path.join(oldDirectory, 'firmware.elf') }, buildResult: { success: true } };
  Object.defineProperty(workbench, 'state', { value: state });
  workbench.folder = { uri: { fsPath: root } };
  workbench.tools = () => ({});
  workbench.t = text => text;
  workbench.rememberBuildSelection = async () => {};
  workbench.assertSession = () => {};
  workbench.hasManagedTasks = () => false;
  workbench.call = async (command, options) => {
    assert.equal(command, 'inspect');
    assert.equal(options.buildDirectory, undefined);
    return { project: { ...preset, buildDirectory: newDirectory } };
  };
  await workbench.inspect();
  assert.equal(state.options.buildDirectory, newDirectory);
  assert.equal(state.options.firmwareElfPath, undefined);
  assert.equal(state.buildResult, undefined);
  state.options.buildDirectory = path.join(root, 'custom-directory');
  workbench.call = async (_command, options) => {
    assert.equal(options.buildDirectory, state.options.buildDirectory);
    return { project: { ...preset, buildDirectory: newDirectory } };
  };
  await workbench.inspect();
  assert.equal(state.options.buildDirectory, path.join(root, 'custom-directory'));
});

test('bridge preserves cancellation when the backend acknowledges with an error envelope', async () => {
  const child = new EventEmitter();
  child.stdout = new PassThrough(); child.stderr = new PassThrough();
  child.exitCode = null; child.signalCode = null;
  let id, cancel;
  child.stdin = new Writable({ write(chunk, encoding, done) {
    const message = JSON.parse(chunk.toString());
    if (message.id) id = message.id;
    if (message.cancel) queueMicrotask(() => {
      child.stdout.write(JSON.stringify({ id, type: 'error', message: 'Cancelled', data: { cancelled: true } }) + '\n');
      child.exitCode = 2; child.stdout.end(); child.stderr.end(); child.emit('close', 2);
    });
    done();
  } });
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'bridge.js'), 'utf8'), {
    module, require(name) {
      if (name === 'child_process') return { spawn: () => child };
      if (name === './process-tree') return { terminateProcessTree: async () => {} };
      return require(name);
    }, setTimeout, clearTimeout
  });
  const { HostBridge } = module.exports;
  const bridge = new HostBridge('backend.exe', { append() {}, appendLine() {} });
  const request = bridge.request('inspect', process.cwd(), {}, {
    isCancellationRequested: false,
    onCancellationRequested(handler) { cancel = handler; return { dispose() {} }; }
  });
  cancel();
  await assert.rejects(request, error => error.cancelled === true && error.data.cancelled === true);
  assert.equal(bridge.active.size, 0);
  const cancelled = bridge.request('inspect', process.cwd(), {}, { isCancellationRequested: true });
  await assert.rejects(cancelled, error => error.cancelled === true);
});
