'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createRequire } = require('node:module');
const { EventEmitter } = require('node:events');
const { PassThrough, Writable } = require('node:stream');
const { previewSummary } = require('../view-state');
const extensionFile = path.resolve(__dirname, '..', 'extension.js');
const extensionRequire = createRequire(extensionFile);

function controller(confirm = false) {
  const calls = [], dialogs = [];
  const vscode = { workspace: { textDocuments: [] }, window: {
    showWarningMessage: async (...args) => { dialogs.push(args); return confirm ? '确认写入' : undefined; },
    showOpenDialog: async () => [{ fsPath: 'C:\\offline\\ARM_CM4F' }]
  } };
  const context = { module: { exports: {} }, Buffer, setTimeout, clearTimeout, require: name => name === 'vscode' ? vscode : name === './development-tools' ? { ACTIONS: new Set() } : extensionRequire(name) };
  vm.runInNewContext(fs.readFileSync(extensionFile, 'utf8') + '\nmodule.exports.Workbench = Workbench;', context);
  const workbench = Object.create(context.module.exports.Workbench.prototype);
  const state = { options: { direction: 'MdkToCMake', target: 'firmware', sourceProject: 'MDK/board.uvprojx' }, conversion: { issues: [{ blocking: true }] } };
  Object.defineProperty(workbench, 'state', { value: state });
  Object.assign(workbench, { folder: { uri: { fsPath: 'C:\\project' } }, epoch: 1, t: source => source,
    assertSession() {}, assertPreview() {}, render() {}, tools: () => ({}), inspect: async () => {},
    call: async (command, payload) => { calls.push({ command, payload }); return command === 'inspectConversion' ? { targets: ['other', 'firmware'], sourceProjects: ['MDK/other.uvprojx', 'MDK/board.uvprojx'] } : { changes: [{ path: 'C:\\project\\port.c', before: null, after: 'YQ==' }], label: 'repair', repairPlan: { request: { target: 'firmware' }, changes: [], inputs: [], notes: [], summary: 'original' } }; }
  });
  return { workbench, state, calls, dialogs, vscode };
}

test('repair preview summaries exclude full plan and file contents', () => {
  const result = previewSummary({ kind: 'freeRtosRepair', summary: 'review', notes: ['version'], changes: [{ path: 'port.c', before: null, after: 'c2VjcmV0' }], repairPlan: { inputs: [{ content: 'private' }], changes: [{ after: 'c2VjcmV0' }] } });
  assert.equal(result.repairPlan, undefined);
  assert.equal(result.changes[0].after, undefined);
  assert.equal(result.changes[0].afterSize, 6);
  assert.deepEqual(result.notes, ['version']);
});

test('download preview is read only and available while conversion has blockers', async () => {
  const { workbench, state, calls, dialogs } = controller();
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].command, 'previewFreeRtosRepair');
  assert.equal(calls[0].payload.sourceProject, 'MDK/board.uvprojx');
  assert.equal(calls[0].payload.target, 'firmware');
  assert.equal(state.preview.kind, 'freeRtosRepair');
  assert.equal(dialogs.length, 0);
  assert.equal(workbench.needsSave('previewFreeRtosRepair'), false);
  assert.equal(workbench.needsSave('applyFreeRtosRepair'), true);
});

test('local import uses the same preview endpoint and requires explicit source and target', async () => {
  const { workbench, state, calls } = controller();
  await workbench.execute('pickFreeRtosPort', {}, 1);
  assert.equal(calls[0].command, 'previewFreeRtosRepair');
  assert.equal(calls[0].payload.localDirectory, 'C:\\offline\\ARM_CM4F');
  state.options.target = '';
  await assert.rejects(workbench.execute('previewFreeRtosRepair', {}, 1), /MDK/);
  assert.equal(calls.length, 1);
});

test('repair apply requires native confirmation and sends the original plan intact', async () => {
  const { workbench, state, calls } = controller(true);
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  const plan = state.preview.repairPlan;
  state.preview.label = 'localized';
  await workbench.applyPreview(1);
  const apply = calls.find(item => item.command === 'applyFreeRtosRepair');
  assert.ok(apply);
  assert.equal(apply.payload.plan, plan);
  assert.equal(state.preview, undefined);
  assert.ok(calls.some(item => item.command === 'inspectConversion'));
  assert.equal(state.options.target, 'firmware');
  assert.equal(state.options.sourceProject, 'MDK/board.uvprojx');
  assert.match(state.status, /重新转换/);
});

test('cancelled repair confirmation never applies files', async () => {
  const { workbench, calls } = controller(false);
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  assert.equal(await workbench.applyPreview(1), false);
  assert.equal(calls.some(item => item.command === 'applyFreeRtosRepair'), false);
});

test('inspection failures after applying repair report that the files were already saved', async () => {
  const { workbench, state, calls } = controller(true);
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  workbench.inspect = async () => { throw new Error('Inspection failed'); };
  await assert.rejects(workbench.applyPreview(1), /FreeRTOS 补全已完成，但重新检查失败/);
  assert.equal(state.preview, undefined);
  assert.equal(state.conversion, undefined);
  assert.equal(calls.filter(item => item.command === 'applyFreeRtosRepair').length, 1);
});

test('blocked write errors include the recovery action', async () => {
  const { workbench, state } = controller();
  workbench.execute = async () => { state.preview = { issues: [{ blocking: true, title: 'Missing GNU port', action: 'Download and preview FreeRTOS repair' }] }; };
  await assert.rejects(workbench.writeChanges('previewConversion', 1), /Download and preview FreeRTOS repair/);
});

test('repair inputs with unsaved edits participate in write protection', async () => {
  const { workbench, state, vscode } = controller();
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  state.preview.repairPlan.inputs = [{ path: 'C:\\shared\\FreeRTOS.h', sha256: 'hash' }];
  const document = file => ({ isDirty: true, uri: { scheme: 'file', fsPath: file } });
  const input = document('C:\\shared\\FreeRTOS.h'), inside = document('C:\\project\\main.c'), unrelated = document('C:\\elsewhere\\notes.txt');
  vscode.workspace.textDocuments = [input, inside, unrelated];
  assert.deepEqual(workbench.dirtyWorkspaceDocuments(), [input, inside]);
});

test('a save during confirmation invalidates repair before its backend apply', async () => {
  const { workbench, state } = controller();
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  workbench.saveDirtyDocuments = async () => { state.preview = undefined; return true; };
  workbench.diskNotice = () => {};
  await assert.rejects(Object.getPrototypeOf(workbench).call.call(workbench, 'applyFreeRtosRepair', { plan: state.preview.repairPlan }, 1), /重新生成当前改动/);
});

test('conversion repair buttons remain available with blockers and require an explicit selection', () => {
  const source = fs.readFileSync(path.resolve(__dirname, '..', 'media', 'workbench.js'), 'utf8');
  const conversion = source.slice(source.indexOf('  function conversion(state) {'), source.indexOf('  function tools(state) {'));
  for (const [direction, target, project, busy, root, expected] of [
    ['MdkToCMake', 'firmware', 'board.uvprojx', false, 'C:\\project', false],
    ['MdkToCMake', '', 'board.uvprojx', false, 'C:\\project', true],
    ['MdkToCMake', 'firmware', '', false, 'C:\\project', true],
    ['MdkToCMake', 'firmware', 'board.uvprojx', true, 'C:\\project', true],
    ['MdkToCMake', 'firmware', 'board.uvprojx', false, '', true],
    ['CMakeToMdk', 'firmware', 'board.uvprojx', false, 'C:\\project', undefined]
  ]) {
    const buttons = [];
    const render = vm.runInNewContext(`(${conversion})`, { current: { root, busy },
      t: value => value, section: () => ({}), field() {}, configurationField() {}, text() {}, issues() {}, append() {}, element() {}, actions() {}, detailPanel() {}, syncControls() {},
      button: (text, action) => { const result = { text, action, disabled: busy }; buttons.push(result); return result; },
      lock: (button, disabled) => { button.disabled ||= disabled; return button; }
    });
    render({ options: { direction, target, sourceProject: project }, conversion: { issues: [{ blocking: true }] } });
    const repairs = buttons.filter(button => ['previewFreeRtosRepair', 'pickFreeRtosPort'].includes(button.action));
    if (expected === undefined) assert.equal(repairs.length, 0);
    else { assert.equal(repairs.length, 2); assert.ok(repairs.every(button => button.disabled === expected)); }
  }
});

test('repair confirmation is available when unrelated conversion fields are invalid', () => {
  const source = fs.readFileSync(path.resolve(__dirname, '..', 'media', 'workbench.js'), 'utf8');
  const helper = source.slice(source.indexOf('  const button ='), source.indexOf('  const lock ='));
  let clicked, posted;
  const context = { current: { busy: false, state: { preview: { kind: 'freeRtosRepair' } } },
    element: () => ({ dataset: {}, addEventListener: (event, callback) => { clicked = callback; } }),
    send: action => { posted = action; },
    content: { querySelectorAll: () => [{ checkValidity: () => false, reportValidity() {}, focus() {} }] }
  };
  vm.runInNewContext(helper + '\nglobalThis.makeButton = button;', context);
  context.makeButton('确认写入…', 'apply'); clicked();
  assert.equal(posted, 'apply');
});

function bridgeHarness() {
  const messages = [], children = []; let kills = 0;
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(path.resolve(__dirname, '..', 'bridge.js'), 'utf8'), {
    module, setTimeout, clearTimeout, require(name) {
      if (name === './process-tree') return { terminateProcessTree: async () => { kills++; } };
      if (name === 'child_process') return { spawn: () => {
        const child = new EventEmitter();
        child.stdout = new PassThrough(); child.stderr = new PassThrough();
        child.stdin = new Writable({ write(chunk, encoding, done) { messages.push(JSON.parse(chunk.toString())); done(); } });
        children.push(child); return child;
      } };
      return require(name);
    }
  });
  const bridge = new module.exports.HostBridge('backend.exe', { append() {}, appendLine() {} });
  return { bridge, messages, children, get kills() { return kills; }, finish(index, result) {
    const child = children[index], id = messages.find(message => message.id && message.command === result.command)?.id;
    child.stdout.write(JSON.stringify({ id, type: 'result', data: result.data }) + '\n');
    child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
  } };
}

test('HostBridge sends both repair commands and protects an active apply from cancellation', async () => {
  const harness = bridgeHarness();
  let cancellation, subscribed = 0;
  const token = { isCancellationRequested: false, onCancellationRequested(callback) { cancellation = callback; subscribed++; return { dispose() {} }; } };
  const preview = harness.bridge.request('previewFreeRtosRepair', process.cwd(), { target: 'firmware' }, token);
  assert.equal(harness.messages[0]?.command, 'previewFreeRtosRepair');
  assert.equal(subscribed, 1);
  harness.finish(0, { command: 'previewFreeRtosRepair', data: { summary: 'ready' } });
  assert.equal((await preview).summary, 'ready');
  const apply = harness.bridge.request('applyFreeRtosRepair', process.cwd(), { plan: { changes: ['two files'] } }, token);
  assert.equal(harness.messages[1]?.command, 'applyFreeRtosRepair');
  assert.equal(subscribed, 1, 'The apply must not subscribe to cancellation after starting');
  token.isCancellationRequested = true;
  harness.finish(1, { command: 'applyFreeRtosRepair', data: { changedFiles: 2 } });
  assert.equal((await apply).changedFiles, 2);
  assert.equal(harness.kills, 0);
  assert.equal(harness.messages.some(message => message.cancel), false);
  assert.equal(typeof cancellation, 'function', 'Preview retains cancellation support');
  await assert.rejects(harness.bridge.request('applyFreeRtosRepair', process.cwd(), {}, token), error => error.cancelled);
  assert.equal(harness.children.length, 2, 'Cancelled applies do not start a process');
});

test('HostBridge keeps repair previews cancellable', async () => {
  const harness = bridgeHarness(); let cancel;
  const preview = harness.bridge.request('previewFreeRtosRepair', process.cwd(), {}, {
    isCancellationRequested: false, onCancellationRequested(callback) { cancel = callback; return { dispose() {} }; }
  });
  cancel();
  assert.equal(harness.messages.some(message => message.cancel), true);
  harness.finish(0, { command: 'previewFreeRtosRepair', data: { summary: 'not accepted after cancellation' } });
  await assert.rejects(preview, error => error.cancelled === true);
});

test('controller sends atomic repair apply without a token and ignores save-time cancel requests', async () => {
  const { workbench, state } = controller();
  let cancelled = 0, receivedToken = 'not called';
  workbench.cancelSource = { token: { isCancellationRequested: false }, cancel() { cancelled++; this.token.isCancellationRequested = true; } };
  workbench.saveDirtyDocuments = async () => false; workbench.diskNotice = () => {}; workbench.translations = {};
  workbench.bridge = { request: async (command, root, payload, token) => { receivedToken = token; workbench.cancelOperation(); assert.match(state.status, /正在保存/); return { changedFiles: 2 }; } };
  const result = await Object.getPrototypeOf(workbench).call.call(workbench, 'applyFreeRtosRepair', { plan: {} }, 1);
  assert.equal(receivedToken, undefined);
  assert.equal(cancelled, 0);
  assert.equal(result.changedFiles, 2);
  workbench.cancelOperation(); assert.equal(cancelled, 1, 'Cancellation resumes after the write');
});

test('repair completion rechecks plan-level blockers without retaining a conversion write preview', async () => {
  const { workbench, state, calls } = controller(true);
  const originalCall = workbench.call;
  const blocker = { blocking: true, key: 'group', title: 'Unsupported library group', action: 'Remove the MDK library' };
  workbench.call = async (command, payload, ...args) => {
    if (command !== 'previewConversion') return originalCall(command, payload, ...args);
    calls.push({ command, payload });
    return { changes: [{ after: 'private conversion content' }], issues: [blocker], canApply: false };
  };
  await workbench.execute('previewFreeRtosRepair', {}, 1);
  await workbench.applyPreview(1);
  const checked = calls.find(item => item.command === 'previewConversion');
  assert.ok(checked);
  assert.equal(checked.payload.sourceProject, 'MDK/board.uvprojx');
  assert.equal(checked.payload.target, 'firmware');
  assert.ok(state.conversion.planIssues.some(issue => issue === blocker));
  assert.equal(state.preview, undefined);
  assert.equal(JSON.stringify(state.conversion).includes('private conversion content'), false);
  assert.match(state.status, /FreeRTOS 补全已完成/);
  const source = fs.readFileSync(path.resolve(__dirname, '..', 'media', 'workbench.js'), 'utf8');
  const conversion = source.slice(source.indexOf('  function conversion(state) {'), source.indexOf('  function tools(state) {'));
  const displayed = [];
  vm.runInNewContext(`(${conversion})`, { current: { root: 'C:\\project' }, t: value => value, section: () => ({}),
    field() {}, configurationField() {}, text() {}, issues: (panel, values) => { displayed.push(...values || []); },
    append() {}, element() {}, actions() {}, detailPanel() {}, syncControls() {}, button: () => ({}), lock: button => button })(state);
  assert.ok(displayed.includes(blocker), 'Plan-level blockers are rendered on the conversion page');
});
