'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const vscode = require('vscode');
const { confirmWrite } = require('./native-confirm.cjs');

async function waitReady(api, root) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    const snapshot = api.getSnapshot();
    if (snapshot.ready && snapshot.root.toLowerCase() === root.toLowerCase()) return snapshot;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('工程工作台尚未完成正常检测：' + JSON.stringify(api.getSnapshot()));
}

async function run() {
  const root = process.env.STM32_SMOKE_ROOT;
  const secondary = process.env.STM32_SMOKE_SECONDARY;
  const originalTasks = fs.readFileSync(path.join(root, '.vscode', 'tasks.json'));
  const extension = vscode.extensions.getExtension('myfsillyba.stm32-project-helper');
  assert.ok(extension, '本地扩展已加载');
  const api = await extension.activate();
  let snapshot = await waitReady(api, root);
  assert.equal(snapshot.panelOpen, true);
  assert.equal(snapshot.trusted, true);
  assert.deepEqual(snapshot.navigation, ['工程配置', '添加源文件', '加入文件', '工程转换', '工具管理', '修改记录']);
  assert.equal(snapshot.state.inspection.project.chip, 'STM32H723VGT6');
  const fullFlow = !process.env.STM32_SMOKE_DIRECT_ONLY;
  if (fullFlow) {
  const commands = await vscode.commands.getCommands(true);
  for (const command of ['stm32ProjectHelper.open', 'stm32ProjectHelper.selectWorkspace', 'stm32ProjectHelper.refresh']) assert.ok(commands.includes(command));

  for (const page of ['configure', 'sources', 'import', 'conversion', 'tools', 'history']) {
    await api.openPage(page);
    assert.equal(api.getSnapshot().page, page);
    assert.equal(api.getSnapshot().panelOpen, true);
  }
  assert.ok(api.getSnapshot().state.toolInventory);
  assert.ok(Array.isArray(api.getSnapshot().state.history.records));
  await api.openPage('configure');
  const selectedTool = api.request('pickTool', { key: 'ninja' });
  const pickerDeadline = Date.now() + 30000;
  while (api.getSnapshot().state.status !== '请选择 Ninja 版本。' && Date.now() < pickerDeadline) await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(api.getSnapshot().state.status, '请选择 Ninja 版本。');
  assert.ok(commands.includes('workbench.action.acceptSelectedQuickOpenItem'));
  await new Promise(resolve => setTimeout(resolve, 250));
  await vscode.commands.executeCommand('workbench.action.acceptSelectedQuickOpenItem');
  const selected = await selectedTool;
  assert.ok(selected?.tools.ninja, '原生工具选择通过桥接与后端验证');
  assert.equal(vscode.workspace.getConfiguration('stm32ProjectHelper', vscode.Uri.file(root)).get('tools.ninja'), selected.tools.ninja);
  assert.equal(api.getSnapshot().state.inspection.tools.ninja, selected.tools.ninja);
  assert.equal(vscode.workspace.getConfiguration('stm32ProjectHelper', vscode.Uri.file(secondary)).get('tools.ninja'), '');
  await api.request('previewTasks');
  snapshot = api.getSnapshot();
  assert.ok(snapshot.state.preview.changes.length > 0);
  await api.request('diff');
  assert.ok(vscode.workspace.textDocuments.some(document => document.uri.scheme === 'stm32-preview'));
  assert.deepEqual(fs.readFileSync(path.join(root, '.vscode', 'tasks.json')), originalTasks);
  assert.equal(fs.existsSync(path.join(root, 'build')), false);

  const secondDocument = await vscode.workspace.openTextDocument(vscode.Uri.file(path.join(secondary, 'main.c')));
  await vscode.window.showTextDocument(secondDocument);
  await waitReady(api, secondary);
  await api.request('previewTasks');
  snapshot = api.getSnapshot();
  assert.ok(snapshot.state.preview.changes.every(change => path.resolve(change.path).toLowerCase().startsWith(secondary.toLowerCase() + path.sep)));
  const firstDocument = await vscode.workspace.openTextDocument(vscode.Uri.file(path.join(root, 'main.c')));
  await vscode.window.showTextDocument(firstDocument);
  await waitReady(api, root);
  await api.openPage('configure');
  }
  const beforeWriteTabs = vscode.window.tabGroups.all.flatMap(group => group.tabs).length;
  const writing = api.request('writeTasks');
  const writeDeadline = Date.now() + 90000;
  while (!api.getSnapshot().state.status.startsWith('等待确认写入') && Date.now() < writeDeadline) {
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  assert.ok(api.getSnapshot().state.status.startsWith('等待确认写入'), api.getSnapshot().state.status);
  const [written] = await Promise.all([writing, confirmWrite()]);
  assert.equal(written, true, '直接写入按钮通过原生确认完成保存');
  assert.equal(vscode.window.tabGroups.all.flatMap(group => group.tabs).length, beforeWriteTabs, '写入过程不会自动打开差异页');
  const savedTasks = fs.readFileSync(path.join(root, '.vscode', 'tasks.json'), 'utf8');
  assert.ok(savedTasks.includes('user-task') && savedTasks.includes('// User task remains unchanged.'));
  assert.ok(savedTasks.includes('一键编译') && savedTasks.includes('一键烧录(DAPLINK)'));
  assert.deepEqual(fs.readFileSync(path.join(secondary, '.vscode', 'tasks.json')), originalTasks);
  assert.equal(fs.existsSync(path.join(root, 'build')), false);
  snapshot = api.getSnapshot();
  const runner = path.join(root, '.vscode', fs.readdirSync(path.join(root, '.vscode')).find(file => /^stm32-daplink.*\.cmake$/.test(file)));
  const cmake = snapshot.state.inspection.tools.cmake;
  execFileSync(cmake, ['-DACTION=configure', '-P', runner], { cwd: root, windowsHide: true, timeout: 60000 });
  execFileSync(cmake, ['--build', snapshot.state.inspection.project.buildDirectory], { cwd: root, windowsHide: true, timeout: 60000 });
  const firmwarePath = path.join(snapshot.state.inspection.project.buildDirectory, 'current_firmware.elf');
  const firmware = fs.readFileSync(firmwarePath);
  assert.equal(firmware.readUInt16LE(18), 40, '生成 ARM 固件');
  assert.ok(fs.existsSync(path.join(snapshot.state.inspection.project.buildDirectory, 'tasks-first.txt')));
  await api.openPage('history');
  assert.ok(api.getSnapshot().state.history.records.some(record => record.label.includes('VS Code')));
  await api.openPage('configure');
  await vscode.commands.executeCommand('workbench.view.extension.stm32ProjectHelper');
  await new Promise(resolve => setTimeout(resolve, 1200));
  const capture = `Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ExtensionWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rectangle);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr context, uint flags);
}
'@
[void][ExtensionWindowCapture]::SetProcessDPIAware()
$instance = Get-CimInstance Win32_Process -Filter "Name = 'Code.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($env:STM32_SMOKE_PROFILE) -and $_.CommandLine -notmatch '--type=' } | Select-Object -First 1
if (-not $instance) { throw '未找到隔离检查窗口。' }
$windowProcess = Get-Process -Id $instance.ProcessId
$rectangle = New-Object ExtensionWindowCapture+RECT
[void][ExtensionWindowCapture]::GetWindowRect($windowProcess.MainWindowHandle, [ref]$rectangle)
$bitmap = New-Object System.Drawing.Bitmap(($rectangle.Right - $rectangle.Left), ($rectangle.Bottom - $rectangle.Top))
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$context = $graphics.GetHdc()
try { if (-not [ExtensionWindowCapture]::PrintWindow($windowProcess.MainWindowHandle, $context, 2)) { throw '窗口截图未完成。' } }
finally { $graphics.ReleaseHdc($context); $graphics.Dispose() }
try { $bitmap.Save($env:STM32_SMOKE_SCREENSHOT, [System.Drawing.Imaging.ImageFormat]::Png) }
finally { $bitmap.Dispose() }`;
  execFileSync('powershell.exe', ['-NoProfile', '-Command', capture], { windowsHide: true, timeout: 15000 });
  const report = { success: true, version: extension.packageJSON.version, vscode: vscode.version,
    currentWorkspace: root, pages: snapshot.navigation, autoOpen: true, nativeDiff: fullFlow || undefined,
    previewIsReadOnly: fullFlow || undefined, multiRootEditorSwitch: fullFlow || undefined, nativeToolSelection: fullFlow || undefined, workspaceToolPathSaved: fullFlow || undefined,
    directWrite: true, nativeWriteConfirmation: true, writeOpensNoDiffTabs: true, tasksWrittenBeforeBuild: true, firmwarePath, hardwareOperations: 0 };
  fs.writeFileSync(process.env.STM32_SMOKE_REPORT, JSON.stringify(report, null, 2));
  console.log('本地 VS Code 激活、六个页面、原生差异和当前工作区切换检查通过。');
}
module.exports = { run };
