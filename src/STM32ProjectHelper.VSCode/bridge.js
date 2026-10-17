'use strict';
const { spawn } = require('child_process');
const readline = require('readline');
const crypto = require('crypto');
const { terminateProcessTree } = require('./process-tree');
const COMMANDS = new Set(['inspect', 'previewTasks', 'applyChanges', 'verify', 'scanSources', 'previewSources', 'previewImport', 'importFiles', 'inspectConversion', 'previewConversion', 'history', 'previewRestore', 'restore', 'tools', 'selectTool', 'repair', 'importTool', 'activateTool', 'build', 'flash', 'memory']);
for (const command of ['previewClangd', 'previewDebug', 'prepareCompileDatabase', 'probeCheck', 'rtos', 'analyzeFirmware', 'hardFault']) COMMANDS.add(command);

class HostBridge {
  constructor(executable, output, { translate = (source, ...args) => String(source ?? '').replace(/\{(\d+)\}/g, (match, index) => Number(index) < args.length ? String(args[Number(index)]) : match), onTranslations = () => {} } = {}) { this.t = translate; this.onTranslations = onTranslations; this.executable = executable; this.output = output; this.active = new Set(); }
  request(command, root, payload, token, progress) {
    if (!COMMANDS.has(command)) return Promise.reject(new Error(this.t('不支持的后端操作。')));
    if (token?.isCancellationRequested) return Promise.reject(Object.assign(new Error(this.t('操作已取消。')), { cancelled: true }));
    return new Promise((resolve, reject) => {
      const id = crypto.randomUUID();
      const child = spawn(this.executable, [], { cwd: root, windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
      this.active.add(child);
      let terminal, failure, cancelTimer, settled = false, cancelling = false;
      const lines = readline.createInterface({ input: child.stdout });
      const cancel = () => {
        if (settled || cancelling) return;
        cancelling = true;
        failure = new Error(this.t('操作已取消。'));
        failure.cancelled = true;
        if (!child.stdin.destroyed) child.stdin.write('{"cancel":true}\n', () => {});
        cancelTimer = setTimeout(() => { void terminateProcessTree(child); }, 2500);
      };
      const subscription = token?.onCancellationRequested(cancel);
      const finish = (error) => {
        if (settled) return;
        settled = true; clearTimeout(cancelTimer); clearTimeout(requestTimer); subscription?.dispose(); lines.close(); this.active.delete(child);
        if (error || failure) reject(failure || error); else if (terminal !== undefined) resolve(terminal); else reject(new Error(this.t('本地后端未返回结果。请查看输出日志。')));
      };
      const timeout = ['repair', 'importTool'].includes(command) ? 45 * 60000 : ['verify', 'build', 'flash'].includes(command) ? 25 * 60000 : command === 'inspectConversion' ? 7 * 60000 : 3 * 60000;
      const requestTimer = setTimeout(() => { failure = Object.assign(new Error(this.t('操作超时，已停止后台进程。')), { timedOut: true }); void terminateProcessTree(child); }, timeout);
      requestTimer.unref();
      child.on('error', error => finish(new Error(this.t('无法启动本地后端：{0}', error.message))));
      child.stdin.on('error', error => { if (!terminal && !failure) failure = error; });
      child.stderr.on('data', data => this.output.append(data.toString()));
      lines.on('line', line => {
        if (!line.trim()) return;
        let envelope;
        try { envelope = JSON.parse(line); } catch { this.output.appendLine(line); return; }
        if (envelope.id !== id) return;
        if (envelope.translations && typeof envelope.translations === 'object' && !Array.isArray(envelope.translations)) this.onTranslations(envelope.translations);
        if (envelope.type === 'progress') {
          const info = envelope.data || {}; this.output.appendLine([info.stage, info.message].filter(Boolean).map(value => this.t(value)).join('：')); progress?.(info);
        } else if (envelope.type === 'result') terminal = envelope.data;
        else if (envelope.type === 'error') {
          const timedOut = failure?.timedOut;
          if (!timedOut) {
            failure = new Error(this.t(envelope.message || '本地后端操作失败。'));
            failure.data = envelope.data;
            if (cancelling || envelope.data?.cancelled === true) failure.cancelled = true;
          }
        }
      });
      child.on('close', code => finish(code && !failure ? new Error(this.t('本地后端退出代码：{0}', code)) : undefined));
      this.output.appendLine('[' + command + '] ' + root);
      child.stdin.write(JSON.stringify({ id, command, root, payload: payload || {} }) + '\n', error => { if (error && !failure) failure = error; });
    });
  }
  dispose() { for (const child of this.active) void terminateProcessTree(child); this.active.clear(); }
}
module.exports = { HostBridge };
