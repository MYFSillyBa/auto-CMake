'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { request } = require('./host-client.cjs');
const { HostBridge } = require('../bridge.js');
const { createProject, createCubeMxProject } = require('./fixtures.cjs');

test('validated local tool choices are used by software checks and generated tasks', { timeout: 180000 }, async () => {
  const base = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-extension-tools-')));
  const root = path.join(base, 'project');
  const options = createProject(root);
  const bridge = new HostBridge(path.resolve(__dirname, '..', 'server', 'STM32ProjectHelper.ExtensionHost.exe'), { append() {}, appendLine() {} });
  try {
    const inspection = await request(root, 'inspect', options);
    assert.ok(inspection.candidates.length >= 4);
    const tools = {};
    for (const [key, wire] of [['CMake', 'cmake'], ['Ninja', 'ninja'], ['Compiler', 'compiler'], ['OpenOcd', 'openOcd']]) {
      const executable = inspection.tools[wire];
      assert.ok(inspection.candidates.some(candidate => candidate.key === key && candidate.executable === executable));
      const selected = await bridge.request('selectTool', root, { key, executable });
      assert.equal(selected.tools[wire], executable);
      assert.equal(Object.values(selected.tools).filter(value => value && value !== selected.tools.scripts).length, 1);
      assert.ok(selected.version);
      tools[wire] = selected.tools[wire];
    }
    const selectedOptions = { ...options, tools };
    const chain = path.join(root, 'cmake', 'gcc-arm-none-eabi.cmake');
    const originalChain = fs.readFileSync(chain);
    const originalPresets = fs.readFileSync(path.join(root, 'CMakePresets.json'));
    const tasks = await request(root, 'previewTasks', selectedOptions);
    await request(root, 'applyChanges', { changes: tasks.changes, label: tasks.label });
    const first = await request(root, 'verify', selectedOptions);
    assert.equal(first.success, true, first.output);
    const cachePath = path.join(root, 'build', 'Debug', 'CMakeCache.txt');
    const originalCache = fs.readFileSync(cachePath, 'utf8');
    const cacheValue = (text, key) => text.match(new RegExp('^' + key + ':[^=]*=(.*)$', 'm'))?.[1].trim();
    const normalized = value => path.resolve(value).toLowerCase();
    assert.equal(normalized(cacheValue(originalCache, 'CMAKE_C_COMPILER')), normalized(tools.compiler));
    assert.equal(normalized(cacheValue(originalCache, 'CMAKE_MAKE_PROGRAM')), normalized(tools.ninja));

    const alternate = path.join(base, 'alternate ninja'); fs.mkdirSync(alternate);
    const ninja = path.join(alternate, 'ninja.exe'); fs.copyFileSync(tools.ninja, ninja);
    const choice = await request(root, 'selectTool', { key: 'Ninja', executable: ninja });
    const updatedOptions = { ...options, tools: { ...tools, ninja: choice.tools.ninja } };
    const updated = await request(root, 'previewTasks', updatedOptions);
    assert.equal(fs.readFileSync(cachePath, 'utf8'), originalCache, '预览保留原缓存');
    await request(root, 'applyChanges', { changes: updated.changes, label: updated.label });
    const second = await request(root, 'verify', updatedOptions);
    assert.equal(second.success, true, second.output);
    assert.match(second.output, /cache-backups/);
    const newCache = fs.readFileSync(cachePath, 'utf8');
    assert.equal(normalized(cacheValue(newCache, 'CMAKE_C_COMPILER')), normalized(tools.compiler));
    assert.equal(normalized(cacheValue(newCache, 'CMAKE_MAKE_PROGRAM')), normalized(ninja));
    const backupRoot = path.join(root, 'build', 'Debug', '.stm32-tools', 'cache-backups');
    const backups = fs.readdirSync(backupRoot).map(folder => path.join(backupRoot, folder, 'CMakeCache.txt'));
    assert.ok(backups.some(file => fs.readFileSync(file, 'utf8') === originalCache));
    const runner = path.join(root, '.vscode', fs.readdirSync(path.join(root, '.vscode')).find(file => /^stm32-daplink.*\.cmake$/.test(file)));
    const { execFileSync } = require('node:child_process');
    execFileSync(tools.cmake, ['-DACTION=configure', '-P', runner], { cwd: root, windowsHide: true, timeout: 60000 });
    execFileSync(tools.cmake, ['--build', path.dirname(cachePath)], { cwd: root, windowsHide: true, timeout: 60000 });
    assert.equal(fs.readdirSync(backupRoot).length, backups.length, '相同工具路径保留增量编译缓存');
    assert.deepEqual(fs.readFileSync(chain), originalChain);
    assert.deepEqual(fs.readFileSync(path.join(root, 'CMakePresets.json')), originalPresets);
    assert.ok(second.firmware.length > 0);
    const desktopSettings = path.join(process.env.LOCALAPPDATA, 'CMakeDapLink', 'tool-paths.json');
    const desktopPaths = fs.existsSync(desktopSettings) ? fs.readFileSync(desktopSettings) : null;
    const repaired = await request(root, 'repair', { ...updatedOptions, installationRoot: path.join(base, 'managed-tools') });
    assert.deepEqual(repaired.installed, [], '已选工具可用时无需重新下载');
    assert.deepEqual(repaired.problems, []);
    for (const key of ['cmake', 'ninja', 'compiler', 'openOcd']) assert.equal(repaired.tools[key], updatedOptions.tools[key], '修复保留工作区的工具选择');
    assert.deepEqual(fs.existsSync(desktopSettings) ? fs.readFileSync(desktopSettings) : null, desktopPaths, '扩展修复不改写桌面工具路径');
  } finally {
    bridge.dispose();
    assert.ok(base.startsWith(fs.realpathSync.native(os.tmpdir()) + path.sep) && path.basename(base).startsWith('stm32-extension-tools-'));
    fs.rmSync(base, { recursive: true, force: true });
  }
});

for (const language of ['zh-CN', 'en']) {
const localizedRequest = request;
const requestInLanguage = (root, command, payload = {}, events = []) => localizedRequest(root, command, { ...payload, language }, events);
test(`${language}: local backend configures firmware, selected sources, import and recovery`, { timeout: 180000 }, async () => {
  const request = requestInLanguage;
  const base = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-extension-flow-')));
  const root = path.join(base, '当前工程');
  const options = createProject(root);
  try {
    const inspection = await request(root, 'inspect', options);
    assert.equal(inspection.project.chip, 'STM32H723VGT6');
    assert.equal(inspection.project.isCMakeProject, true);
    assert.ok(inspection.tools.scripts);
    assert.equal(inspection.versions.filter(x => x.available).length, 4);
    const originalTasks = fs.readFileSync(path.join(root, '.vscode', 'tasks.json'));
    const preview = await request(root, 'previewTasks', options);
    assert.ok(preview.changes.length >= 2);
    assert.deepEqual(fs.readFileSync(path.join(root, '.vscode', 'tasks.json')), originalTasks);
    assert.equal(fs.existsSync(path.join(root, 'build')), false);
    await request(root, 'applyChanges', { changes: preview.changes, label: preview.label });
    assert.equal(fs.existsSync(path.join(root, 'build')), false);
    const written = fs.readFileSync(path.join(root, '.vscode', 'tasks.json'), 'utf8');
    assert.ok(written.includes('user-task') && written.includes('// User task remains unchanged.'));
    assert.ok(written.includes(language === 'en' ? 'One-click Flash (DAPLINK)' : '一键烧录(DAPLINK)'));
    const events = [];
    const verification = await request(root, 'verify', options, events);
    assert.equal(verification.success, true, verification.output);
    assert.equal(path.basename(verification.firmware[0]), 'current_firmware.elf');
    assert.equal(verification.openOcdChecked, true);
    assert.ok(events.some(x => x.type === 'progress'));
    assert.ok(fs.existsSync(path.join(root, 'build', 'Debug', 'tasks-first.txt')));

    const folder = path.join(root, 'module');
    const scanned = await request(root, 'scanSources', { folder, target: 'extension_probe' });
    assert.ok(scanned.candidateFiles.includes('module/unused.c'));
    const selectedFiles = ['module/motor.c', 'module/include/motor.h'];
    const sources = await request(root, 'previewSources', { folder, target: 'extension_probe', selectedFiles });
    await request(root, 'applyChanges', { changes: sources.changes, label: sources.label });
    const cmake = fs.readFileSync(path.join(root, 'CMakeLists.txt'), 'utf8');
    assert.ok(cmake.includes('module/motor.c') && cmake.includes('module/include/motor.h'));
    assert.equal(cmake.includes('module/unused.c'), false);
    const sourceBuild = await request(root, 'verify', { ...options, checkOpenOcd: false });
    assert.equal(sourceBuild.success, true, sourceBuild.output);

    const incoming = path.join(base, 'incoming'); fs.mkdirSync(incoming);
    fs.writeFileSync(path.join(incoming, 'extra.h'), '#pragma once\n');
    const importPreview = await request(root, 'previewImport', { folderName: 'extras', sourceFiles: [path.join(incoming, 'extra.h')] });
    const imported = await request(root, 'importFiles', { plan: importPreview.plan, confirmedExistingFolder: false });
    assert.equal(imported.files.length, 1);
    const existingPreview = await request(root, 'previewImport', { folderName: 'extras', sourceFiles: [path.join(incoming, 'extra.h')] });
    assert.equal(existingPreview.plan.folderExisted, true);
    const appended = await request(root, 'importFiles', { plan: existingPreview.plan, confirmedExistingFolder: true });
    assert.equal(path.basename(appended.files[0]), 'extra (1).h');
    assert.equal(fs.readFileSync(imported.files[0], 'utf8'), '#pragma once\n');

    const history = await request(root, 'history');
    const taskRecord = history.records.find(x => x.label === preview.label);
    assert.ok(taskRecord);
    const restorePreview = await request(root, 'previewRestore', { id: taskRecord.id });
    assert.ok(restorePreview.changes.length >= 2);
    assert.equal(restorePreview.conflicts.length, 0);
    await request(root, 'restore', { id: taskRecord.id, changes: restorePreview.changes, overwriteChangedFiles: false });
    assert.deepEqual(fs.readFileSync(path.join(root, '.vscode', 'tasks.json')), originalTasks);
    assert.equal(fs.existsSync(path.join(root, '.vscode', 'stm32-daplink.cmake')), false);
    const tools = await request(root, 'tools');
    assert.ok(tools.installations.some(x => x.key === 'CMake' && x.isActive));
    assert.equal(fs.readdirSync(path.join(root, '.vscode')).some(x => x.endsWith('.ps1')), false);
  } finally {
    const resolved = path.resolve(base);
    assert.ok(resolved.startsWith(fs.realpathSync.native(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('stm32-extension-flow-'));
    fs.rmSync(resolved, { recursive: true, force: true });
  }
});

test(`${language}: CubeMX-shaped MDK and CMake conversion works through the extension protocol`, { timeout: 180000 }, async () => {
  const request = requestInLanguage;
  const base = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-extension-conversion-')));
  const root = path.join(base, 'project');
  createCubeMxProject(root);
  try {
    const initial = await request(root, 'inspectConversion', { direction: 'MdkToCMake' });
    assert.ok(initial.targets.includes('ConversionDemo'));
    const original = fs.readFileSync(initial.sourceProjects[0]);
    const forward = await request(root, 'previewConversion', { direction: 'MdkToCMake', target: 'ConversionDemo', sourceProject: initial.sourceProjects[0], configuration: 'Debug' });
    assert.equal(forward.canApply, true, JSON.stringify(forward.issues));
    assert.equal(fs.existsSync(path.join(root, 'CMakeLists.txt')), false);
    await request(root, 'applyChanges', { changes: forward.changes, label: forward.label });
    const options = { configurePreset: 'Debug', buildPreset: 'Debug', configuration: 'Debug', targetScript: 'target/stm32f1x.cfg' };
    const tasks = await request(root, 'previewTasks', options);
    await request(root, 'applyChanges', { changes: tasks.changes, label: tasks.label });
    const verified = await request(root, 'verify', { ...options, checkOpenOcd: false });
    assert.equal(verified.success, true, verified.output);
    assert.ok(verified.firmware.length);
    assert.ok(fs.readFileSync(initial.sourceProjects[0]).equals(original), '转换为 CMake 时保留原 MDK 文件');
    const reverse = await request(root, 'inspectConversion', { direction: 'CMakeToMdk', commonOptions: options });
    assert.ok(reverse.targets.includes('ConversionDemo'));
    const back = await request(root, 'previewConversion', { direction: 'CMakeToMdk', target: 'ConversionDemo', buildDirectory: reverse.buildDirectory, configuration: 'Debug' });
    assert.equal(back.canApply, true, JSON.stringify(back.issues));
    const reverseWrite = await request(root, 'applyChanges', { changes: back.changes, label: back.label });
    assert.ok(fs.existsSync(back.outputProject));
    const recovery = await request(root, 'previewRestore', { id: reverseWrite.record.id });
    await request(root, 'restore', { id: reverseWrite.record.id, changes: recovery.changes, overwriteChangedFiles: false });
    assert.ok(fs.readFileSync(initial.sourceProjects[0]).equals(original), '确认替换的 MDK 文件可恢复为原始内容');
  } finally {
    const resolved = path.resolve(base);
    assert.ok(resolved.startsWith(fs.realpathSync.native(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('stm32-extension-conversion-'));
    fs.rmSync(resolved, { recursive: true, force: true });
  }
});
}

test('a moved project backs up stale build cache and recompiles without modifying source files', { timeout: 180000 }, async () => {
  const base = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-extension-relocation-')));
  const original = path.join(base, 'original');
  const current = path.join(base, 'moved');
  const options = createProject(original);
  try {
    const initial = await request(original, 'verify', { ...options, checkOpenOcd: false });
    assert.equal(initial.success, true, initial.output);
    const source = fs.readFileSync(path.join(original, 'CMakeLists.txt'));
    fs.renameSync(original, current);
    const repaired = await request(current, 'verify', { ...options, checkOpenOcd: false });
    assert.equal(repaired.success, true, repaired.output);
    assert.ok(repaired.firmware.every(x => x.startsWith(current + path.sep)));
    assert.deepEqual(fs.readFileSync(path.join(current, 'CMakeLists.txt')), source);
    const cache = fs.readFileSync(path.join(current, 'build', 'Debug', 'CMakeCache.txt'), 'utf8');
    assert.ok(cache.includes(current.replaceAll('\\', '/')));
    assert.ok(!cache.includes(original.replaceAll('\\', '/')));
    assert.ok(repaired.output.includes('缓存') || repaired.output.includes('cache'));
    const repeated = await request(current, 'verify', { ...options, checkOpenOcd: false });
    assert.equal(repeated.success, true, repeated.output);
  } finally {
    const resolved = path.resolve(base);
    assert.ok(resolved.startsWith(fs.realpathSync.native(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('stm32-extension-relocation-'));
    fs.rmSync(resolved, { recursive: true, force: true });
  }
});
