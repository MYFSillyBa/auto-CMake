'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { createProject } = require('../test/fixtures.cjs');

async function main() {
  const extensionRoot = path.resolve(__dirname, '..');
  const vscodeExecutablePath = process.env.VSCODE_EXECUTABLE_PATH || 'D:\\Microsoft VS Code\\Code.exe';
  if (!fs.existsSync(vscodeExecutablePath)) throw new Error('请通过 VSCODE_EXECUTABLE_PATH 指定已安装的 VS Code 可执行文件。');
  const temporary = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'stm32-vscode-smoke-')));
  const primary = path.join(temporary, 'primary');
  const secondary = path.join(temporary, 'secondary');
  createProject(primary); createProject(secondary);
  const workspace = path.join(temporary, 'probe.code-workspace');
  const settingsDirectory = path.join(temporary, 'profile', 'User'); fs.mkdirSync(settingsDirectory, { recursive: true });
  fs.writeFileSync(path.join(settingsDirectory, 'settings.json'), JSON.stringify({ 'stm32ProjectHelper.language': process.env.STM32_SMOKE_LANGUAGE || 'zh-CN' }));
  fs.writeFileSync(workspace, JSON.stringify({ folders: [{ path: primary }, { path: secondary }] }));
  const driver = path.join(temporary, 'driver'); fs.mkdirSync(driver);
  const done = path.join(temporary, 'driver-result.json');
  fs.writeFileSync(path.join(driver, 'package.json'), JSON.stringify({ name: 'stm32-local-flow-check', publisher: 'local-checks', version: '1.0.0',
    engines: { vscode: '^1.95.0' }, activationEvents: ['*'], extensionDependencies: ['myfsillyba.stm32-project-helper'], main: './driver.cjs' }));
  fs.writeFileSync(path.join(driver, 'driver.cjs'), `const vscode = require('vscode');
const fs = require('node:fs');
exports.activate = function () { setTimeout(async () => {
  let result;
  try { await require(${JSON.stringify(path.join(extensionRoot, 'test', 'vscode-smoke.cjs'))}).run(); result = { success: true }; }
  catch (error) { result = { success: false, message: error.stack || error.message }; console.error(error); }
  fs.writeFileSync(${JSON.stringify(done)}, JSON.stringify(result, null, 2));
  await vscode.commands.executeCommand('workbench.action.closeWindow');
}, 500); };
`);
  console.log('VS Code 正常流程检查目录：' + temporary);
  // Extension test mode suppresses native confirmation dialogs. Use an isolated
  // ordinary development window and a separate temporary driver extension.
  const child = spawn(vscodeExecutablePath, [workspace, '--new-window', '--user-data-dir=' + path.join(temporary, 'profile'),
    '--extensions-dir=' + path.join(temporary, 'extensions'),
    '--extensionDevelopmentPath=' + (process.env.STM32_EXTENSION_ROOT || extensionRoot), '--extensionDevelopmentPath=' + driver,
    '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes', '--disable-updates', '--disable-gpu',
    '--force-renderer-accessibility', '--disable-extension=github.copilot-chat'], {
    cwd: temporary, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, STM32_SMOKE_ROOT: primary, STM32_SMOKE_SECONDARY: secondary, STM32_SMOKE_REPORT: path.join(temporary, 'verification.json'),
      STM32_SMOKE_PROFILE: path.join(temporary, 'profile'), STM32_SMOKE_SCREENSHOT: path.join(temporary, 'workbench.png') }
  });
  let log = '';
  const collect = data => { log = (log + data.toString()).slice(-1024 * 1024); };
  child.stdout.on('data', collect); child.stderr.on('data', collect);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('VS Code 正常流程检查超时。')); }, 240000);
    child.on('error', error => { clearTimeout(timer); reject(error); });
    child.on('close', code => { clearTimeout(timer); code === 0 ? resolve() : reject(new Error('隔离窗口退出代码：' + code + '\n' + log.slice(-8000))); });
  });
  fs.writeFileSync(path.join(temporary, 'window.log'), log);
  const result = fs.existsSync(done) ? JSON.parse(fs.readFileSync(done, 'utf8')) : { success: false, message: '检查驱动未完成。\n' + log.slice(-8000) };
  if (!result.success) throw new Error(result.message);
  console.log(fs.readFileSync(path.join(temporary, 'verification.json'), 'utf8'));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
