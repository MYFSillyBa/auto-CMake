'use strict';
const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { SerialSession } = require('./serial-session');
const { UsageStatistics } = require('./usage-statistics');
const { createFaultReport } = require('./fault-report');
const { fingerprintProject, projectDelta, RELEVANT } = require('./cubemx-sync');

const ACTIONS = new Set(['writeClangd', 'openClangdExtension', 'prepareCompileDatabase', 'writeDebug', 'startDebug', 'inspectSync', 'previewSync', 'writeSync', 'probeCheck', 'analyzeFirmware', 'pickMapFile', 'hardFault', 'rtos', 'listSerial', 'connectSerial', 'disconnectSerial', 'sendSerial', 'exportSerial', 'exportReport', 'configureTelemetry', 'toggleTelemetry']);
const BLOCK = /^# BEGIN CMakeDapLink managed folders\r?\n[\s\S]*?^# END CMakeDapLink managed folders/gm;
function decodeRegisters(value) {
  const text = String(value || '').trim(); if (text.length > 32768) throw new Error('寄存器输入过大。');
  const values = text.startsWith('{') ? JSON.parse(text) : Object.fromEntries([...text.matchAll(/\b([A-Za-z][A-Za-z0-9]*)\s*[:=]\s*(0x[0-9a-fA-F]+|\d+)\b/g)].map(match => [match[1].toUpperCase(), match[2]]));
  if (!values || Array.isArray(values) || typeof values !== 'object' || !Object.keys(values).length) throw new Error('请输入寄存器 JSON 或 CFSR=0x00000000 形式的值。');
  return Object.fromEntries(Object.entries(values).map(([key, value]) => [key.toUpperCase(), String(value)]));
}
class DevelopmentTools {
  constructor(workbench) {
    this.workbench = workbench; this.baselines = new Map(); this.pendingFiles = new Set();
    this.statistics = new UsageStatistics(workbench.context, () => vscode.env.isTelemetryEnabled === true);
    this.serial = new SerialSession(serial => { this.workbench.state.serial = serial; void this.workbench.panel?.webview.postMessage({ type: 'serialData', serial }); });
    this.watcher = vscode.workspace.createFileSystemWatcher('**/*');
    for (const event of [this.watcher.onDidCreate, this.watcher.onDidChange, this.watcher.onDidDelete]) event(uri => this.changed(uri));
    workbench.context.subscriptions.push(this.watcher);
    if (vscode.env.onDidChangeTelemetryEnabled) workbench.context.subscriptions.push(vscode.env.onDidChangeTelemetryEnabled(allowed => { if (!allowed) for (const controller of this.statistics.inflight) controller.abort(); this.workbench.render(); }));
  }
  t(source, ...args) { return this.workbench.t(source, ...args); }
  options() { const w = this.workbench; return { ...w.options(), compilationDatabaseDirectory: w.state.options.compilationDatabaseDirectory || undefined, debugName: w.state.options.debugName || undefined, gdbEndpoint: w.state.options.gdbEndpoint || 'localhost:3333', mapFilePath: w.state.options.mapFilePath || undefined }; }
  changed(uri) {
    const w = this.workbench, root = w.folder?.uri.fsPath; if (!root || uri.scheme !== 'file' || !RELEVANT.test(uri.fsPath)) return;
    const relative = path.relative(root, uri.fsPath); if (relative.startsWith('..') || path.isAbsolute(relative) || relative.split(/[\\/]/).some(part => ['.git', '.vscode', 'build', 'bin', 'obj', 'node_modules'].includes(part.toLowerCase()))) return;
    this.pendingFiles.add(relative); clearTimeout(this.syncTimer);
    this.syncTimer = setTimeout(() => this.flushChanges(), 800);
  }
  async flushChanges() {
    const w = this.workbench; if (w.busy) { this.syncTimer = setTimeout(() => this.flushChanges(), 500); return; }
    try { await this.inspectSync(); w.render(); } catch (error) { w.output.appendLine(this.t(error.message)); }
    this.pendingFiles.clear();
  }
  readBlocks(root) { try { return fs.readFileSync(path.join(root, 'CMakeLists.txt'), 'utf8').match(BLOCK) || []; } catch { return []; } }
  blockKey(root) { return 'cubemx.managedBlocks.' + crypto.createHash('sha256').update(root.toLowerCase()).digest('hex'); }
  async rememberBlocks() {
    const w = this.workbench, root = w.folder?.uri.fsPath, baseline = this.baselines.get(root); if (!baseline) return;
    const blocks = this.readBlocks(root);
    if (blocks.length) { baseline.blocks = blocks; await w.context.workspaceState.update(this.blockKey(root), blocks); }
  }
  async baseline() {
    const w = this.workbench, root = w.folder?.uri.fsPath; if (!root || this.baselines.has(root)) return;
    const files = await fingerprintProject(root); if (w.folder?.uri.fsPath !== root) return;
    const current = this.readBlocks(root), saved = w.context.workspaceState.get(this.blockKey(root), []);
    const blocks = current.length ? current : Array.isArray(saved) ? saved.filter(block => typeof block === 'string' && block.length <= 4 * 1024 * 1024).slice(0, 8) : [];
    this.baselines.set(root, { files, blocks });
    if (current.length) await w.context.workspaceState.update(this.blockKey(root), current);
    const missing = !current.length && blocks.length > 0;
    w.state.sync = { changed: missing, files: missing ? ['CMakeLists.txt'] : [], notes: [this.t('已记录当前会话的 CubeMX 配置基线。')] };
    if (missing) { w.state.syncNotice = this.t('工程生成配置已变化，可在开发配置页面预览 CubeMX 同步。'); return; }
    w.state.syncNotice = undefined;
  }
  async inspectSync() {
    const w = this.workbench, root = w.folder.uri.fsPath, epoch = w.epoch;
    const after = await fingerprintProject(root); w.assertSession(epoch);
    const previous = this.baselines.get(root);
    if (!previous) this.baselines.set(root, { files: after, blocks: this.readBlocks(root) });
    const files = previous ? projectDelta(previous.files, after) : [];
    if (previous?.blocks?.length && !this.readBlocks(root).length && !files.includes('CMakeLists.txt')) files.push('CMakeLists.txt');
    w.state.sync = { changed: files.length > 0, files, notes: [this.t(files.length ? '检测到工程配置变化，请预览同步差异后确认。' : '当前配置与会话基线一致。')] };
    w.state.syncNotice = files.length ? this.t('工程生成配置已变化，可在开发配置页面预览 CubeMX 同步。') : undefined;
    return w.state.sync;
  }
  async syncPreview(epoch, progress) {
    const w = this.workbench, root = w.folder.uri.fsPath; await this.inspectSync();
    let preview, inspection;
    try { inspection = await w.call('inspectConversion', { direction: 'MdkToCMake' }, epoch, progress); }
    catch (error) { if (error.cancelled || w.cancelSource?.token.isCancellationRequested) throw error; w.output.appendLine(error.message); }
    if (inspection?.sourceProjects?.length) {
      const projects = inspection.sourceProjects;
      const sourceProject = projects.length === 1 ? projects[0] : await vscode.window.showQuickPick(projects, { title: this.t('选择 CubeMX 的 MDK 工程作为同步来源') });
      w.assertSession(epoch); if (!sourceProject) return;
      let target = inspection.targets?.[0];
      if (inspection.targets?.length > 1) target = await vscode.window.showQuickPick(inspection.targets, { title: this.t('选择要同步的构建目标') });
      w.assertSession(epoch); if (!target) return;
      preview = await w.call('previewConversion', { direction: 'MdkToCMake', sourceProject, target, configuration: w.state.options.configuration }, epoch, progress);
    } else {
      const file = path.join(root, 'CMakeLists.txt'); if (!fs.existsSync(file)) throw new Error(this.t('工程没有可同步的 CMakeLists.txt 或 MDK 工程。'));
      preview = { changes: [], label: '同步 CubeMX 工程配置', summary: this.t('保留 CubeMX 生成配置，并恢复本工具管理的源文件块。'), notes: [this.t('没有 MDK 来源时，不重新生成外设代码或猜测 CMake 编译参数。')] };
    }
    const baseline = this.baselines.get(root), currentBlocks = this.readBlocks(root), remembered = currentBlocks.length ? currentBlocks : baseline?.blocks || [];
    const cmakeFile = path.join(root, 'CMakeLists.txt');
    let change = preview.changes.find(item => path.resolve(item.path).toLowerCase() === cmakeFile.toLowerCase());
    const before = fs.existsSync(cmakeFile) ? fs.readFileSync(cmakeFile) : null;
    let content = change?.after ? Buffer.from(change.after, 'base64').toString('utf8') : before?.toString('utf8') || '';
    const additions = remembered.filter(block => !content.includes(block));
    if (additions.length) {
      content += '\n' + additions.join('\n') + '\n';
      if (!change) { change = { path: cmakeFile, before: before?.toString('base64') || null }; preview.changes.push(change); }
      change.after = Buffer.from(content).toString('base64');
    }
    w.preview(preview, 'sync'); return preview;
  }
  async confirmHardware(epoch, source) {
    const w = this.workbench, label = this.t('确认连接硬件');
    const answer = await vscode.window.showWarningMessage(this.t(source), { modal: true, detail: this.t('操作可能短暂停止或复位芯片，不会写入固件。请避免与其他调试会话同时使用。') }, label);
    w.assertSession(epoch); return answer === label;
  }
  async probeCheck(epoch, progress, confirmed = false) {
    const w = this.workbench;
    if (!confirmed && !await this.confirmHardware(epoch, '将连接所选下载器并检测芯片？')) return;
    w.state.probeCheck = undefined;
    const result = await w.call('probeCheck', { ...this.options(), explicitHardware: true }, epoch, progress); w.state.probeCheck = result;
    if (result.output) w.output.appendLine(result.output); return result;
  }
  async execute(action, message, epoch, progress) {
    const w = this.workbench, state = w.state;
    switch (action) {
      case 'writeClangd': case 'writeDebug': {
        let preview = await w.call(action === 'writeClangd' ? 'previewClangd' : 'previewDebug', this.options(), epoch, progress);
        if (action === 'writeClangd' && preview.compileDatabaseFound === false) {
          const active = state.options.buildDirectory || state.inspection?.project?.buildDirectory;
          if (active && path.resolve(w.folder.uri.fsPath, active).toLowerCase() !== path.dirname(preview.compileDatabasePath).toLowerCase())
            throw new Error(this.t('指定目录没有编译数据库，请选择已有数据库或清空目录以使用当前构建目录。'));
          const generated = await this.execute('prepareCompileDatabase', {}, epoch, progress);
          if (!generated?.success) return;
          preview = await w.call('previewClangd', this.options(), epoch, progress);
          if (!preview.compileDatabaseFound) throw new Error(this.t('编译数据库生成失败，请查看输出。'));
        }
        w.preview(preview, action); if (preview.changes?.length) return w.applyPreview(epoch, progress);
        state.status = this.t('当前配置已是最新，没有需要写入的文件。'); return;
      }
      case 'openClangdExtension': return vscode.commands.executeCommand('workbench.extensions.search', '@id:llvm-vs-code-extensions.vscode-clangd');
      case 'prepareCompileDatabase': {
        const answer = await vscode.window.showWarningMessage(this.t('将运行 CMake 配置以生成编译数据库，不编译或连接硬件。'), { modal: true }, this.t('确认配置'));
        w.assertSession(epoch); if (answer !== this.t('确认配置')) return;
        const result = await w.call('prepareCompileDatabase', { ...this.options(), explicitConfigure: true }, epoch, progress); state.compileDatabase = result;
        if (result.output) w.output.appendLine(result.output); state.status = this.t(result.success ? '编译数据库已生成。' : '编译数据库生成失败，请查看输出。'); return result;
      }
      case 'startDebug': {
        if (!vscode.extensions.getExtension('marus25.cortex-debug')) {
          await vscode.commands.executeCommand('workbench.extensions.search', '@id:marus25.cortex-debug'); throw new Error(this.t('请安装 Cortex-Debug 扩展，再启动调试。'));
        }
        const preview = await w.call('previewDebug', this.options(), epoch, progress); w.preview(preview, 'writeDebug');
        if (preview.changes?.length && !await w.applyPreview(epoch, progress)) return;
        if (!await this.confirmHardware(epoch, '将启动 GDB 调试并连接硬件，可能暂停或复位目标。')) return;
        const configuration = preview.configuration;
        if (!configuration) throw new Error(this.t('请在运行和调试面板选择已生成的 STM32 调试配置。'));
        const started = await vscode.debug.startDebugging(w.folder.workspaceFolder || w.folder, configuration);
        state.status = this.t(started ? '调试会话已启动。' : '调试会话未启动，请查看调试控制台。'); return;
      }
      case 'inspectSync': return this.inspectSync();
      case 'previewSync': return this.syncPreview(epoch, progress);
      case 'writeSync': { const preview = await this.syncPreview(epoch, progress); if (!preview) return; if (preview.changes?.length && !await w.applyPreview(epoch, progress)) return; const root = w.folder.uri.fsPath; this.baselines.delete(root); await this.baseline(); return; }
      case 'probeCheck': return this.probeCheck(epoch, progress);
      case 'pickMapFile': { const selected = await vscode.window.showOpenDialog({ canSelectMany: false, filters: { MAP: ['map'] }, defaultUri: w.folder.uri }); w.assertSession(epoch); if (selected?.length) { if (!selected[0].fsPath.toLowerCase().startsWith(w.folder.uri.fsPath.toLowerCase() + path.sep)) throw new Error(this.t('请选择当前工程内的 MAP 文件。')); state.options.mapFilePath = selected[0].fsPath; } return; }
      case 'analyzeFirmware': state.advancedAnalysis = await w.call('analyzeFirmware', this.options(), epoch, progress); return state.advancedAnalysis;
      case 'hardFault': state.hardFault = await w.call('hardFault', { ...this.options(), registers: decodeRegisters(state.options.hardFaultRegisters) }, epoch, progress); return state.hardFault;
      case 'rtos': {
        const endpoint = /^(localhost|127\.0\.0\.1|\[::1\]):([0-9]{1,5})$/i.exec(state.options.gdbEndpoint || 'localhost:3333');
        if (!endpoint || Number(endpoint[2]) < 1 || Number(endpoint[2]) > 65535) throw new Error(this.t('GDB 地址仅接受 localhost、127.0.0.1 或 [::1] 与有效端口。'));
        if (!await this.confirmHardware(epoch, '将连接现有 GDB Server 并暂停目标，以读取 FreeRTOS 任务快照？')) return;
        state.rtos = undefined; state.rtos = await w.call('rtos', { ...this.options(), gdbHost: endpoint[1], gdbPort: Number(endpoint[2]), explicitHardware: true }, epoch, progress); return state.rtos;
      }
      case 'listSerial': state.serial = await this.serial.list(); return;
      case 'connectSerial': state.serial = await this.serial.connect(state.options.serialPort || '', Number(state.options.serialBaud || 115200), state.options.serialMode || 'ASCII'); return { success: true };
      case 'disconnectSerial': state.serial = await this.serial.disconnect(); return;
      case 'sendSerial': if (!this.serial.state.connected) throw new Error(this.t('请先连接串口。')); state.serial = await this.serial.send(state.options.serialText || '', state.options.serialMode || 'ASCII', state.options.serialNewline || 'none'); return { success: true };
      case 'exportSerial': return this.saveText(this.serial.export(), 'serial-log.txt', epoch);
      case 'exportReport': {
        const text = createFaultReport({ version: w.context.extension.packageJSON.version, language: w.language, configuration: state.options.configuration, probe: state.options.probeType,
          toolVersions: state.inspection?.versions?.map(item => ({ tool: item.key, version: item.version })), buildSuccess: state.buildResult?.success, flashSuccess: state.flashResult?.success, problems: state.buildResult?.problems || state.verification?.problems }, w.output.text, w.folder.uri.fsPath, process.env.USERPROFILE);
        const document = await vscode.workspace.openTextDocument({ language: 'markdown', content: text }); await vscode.window.showTextDocument(document, { preview: true });
        const answer = await vscode.window.showInformationMessage(this.t('故障报告已脱敏并打开预览。请检查内容，确认后保存；不会自动上传。'), { modal: true }, this.t('保存报告'));
        w.assertSession(epoch); if (answer === this.t('保存报告')) { const savedPath = await this.saveText(document.getText(), 'stm32-diagnostic-report.md', epoch); state.report = { path: savedPath, redacted: true }; return savedPath ? { success: true } : undefined; } return;
      }
      case 'configureTelemetry': {
        await this.statistics.enable(false); await this.statistics.configure(state.options.telemetryHost || 'https://us.i.posthog.com', this.telemetryKeyDraft || ''); this.telemetryKeyDraft = undefined;
        void w.panel?.webview.postMessage({ type: 'clearSensitiveDrafts' }); state.telemetry = this.statistics.snapshot(); state.status = this.t('PostHog 项目已保存，统计保持关闭。'); return;
      }
      case 'toggleTelemetry': {
        const enabled = message.enabled === true;
        if (enabled) { const answer = await vscode.window.showWarningMessage(this.t('同意向配置的 PostHog 项目发送匿名功能统计？'), { modal: true, detail: this.t('仅发送随机匿名标识、版本、语言、功能名称与成功状态。网络服务仍可看到来源 IP；不发送工程路径、源码、日志或芯片序列号。VS Code 总统计开关关闭时不发送。') }, this.t('同意并启用')); w.assertSession(epoch); if (answer !== this.t('同意并启用')) return; }
        await this.statistics.enable(enabled); state.telemetry = this.statistics.snapshot(); return;
      }
    }
  }
  async saveText(text, filename, epoch) { const w = this.workbench; const uri = await vscode.window.showSaveDialog({ defaultUri: vscode.Uri.file(path.join(w.folder.uri.fsPath, filename)) }); w.assertSession(epoch); if (uri) { if (fs.existsSync(uri.fsPath)) { const answer = await vscode.window.showWarningMessage(this.t('目标文件已存在，确认覆盖？'), { modal: true }, this.t('确认覆盖')); w.assertSession(epoch); if (answer !== this.t('确认覆盖')) return; } await vscode.workspace.fs.writeFile(uri, Buffer.from(text, 'utf8')); return uri.fsPath; } }
  dispose() { clearTimeout(this.syncTimer); this.serial.dispose(); this.statistics.dispose(); this.baselines.clear(); this.telemetryKeyDraft = undefined; }
}
module.exports = { DevelopmentTools, ACTIONS, decodeRegisters };
