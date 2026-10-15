'use strict';
const { spawn } = require('node:child_process');
const path = require('node:path');

function terminateProcessTree(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null || !Number.isInteger(child.pid)) return Promise.resolve();
  if (process.platform !== 'win32') { try { child.kill('SIGKILL'); } catch {} return Promise.resolve(); }
  return new Promise(resolve => {
    const taskkill = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'taskkill.exe');
    const killer = spawn(taskkill, ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, shell: false, stdio: 'ignore' });
    const timeout = setTimeout(() => { try { child.kill(); } catch {} resolve(); }, 2500); timeout.unref();
    const done = () => { clearTimeout(timeout); resolve(); };
    killer.once('error', done); killer.once('close', done);
  });
}
module.exports = { terminateProcessTree };
