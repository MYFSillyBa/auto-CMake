'use strict';
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const path = require('node:path');
const { terminateProcessTree } = require('./process-tree');

// Runs in Windows PowerShell's .NET Framework; no script file or native Node addon is installed.
const SERIAL_PROGRAM = `using System; using System.IO.Ports; using System.Threading; using System.Threading.Tasks; using System.Web.Script.Serialization;
public static class Stm32SerialSession {
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
 static void Emit(string type, object data) { Console.WriteLine(Json.Serialize(new { type, data })); Console.Out.Flush(); }
 public static void Run() {
  Console.InputEncoding = new System.Text.UTF8Encoding(false); Console.OutputEncoding = new System.Text.UTF8Encoding(false);
  SerialPort port = null; Task<string> input = Task.Run(() => Console.ReadLine());
  try { while (true) {
   if (input.IsCompleted) {
    string line = input.GetAwaiter().GetResult(); if (line == null) break; string[] parts = line.Split('\\t');
    try {
     if (parts[0] == "LIST") { var names = SerialPort.GetPortNames(); Array.Sort(names); Emit("ports", names); }
     else if (parts[0] == "OPEN") {
      if (port != null) { port.Close(); port.Dispose(); port = null; }
      port = new SerialPort(parts[1], int.Parse(parts[2]), Parity.None, 8, StopBits.One);
      port.ReadTimeout = 200; port.WriteTimeout = 1000; port.DtrEnable = false; port.RtsEnable = false; port.Open(); Emit("opened", new { name = parts[1], baud = port.BaudRate });
     } else if (parts[0] == "SEND") {
      if (port == null || !port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
      byte[] bytes = Convert.FromBase64String(parts[1]); if (bytes.Length > 65536) throw new ArgumentException("Serial payload exceeds 64 KiB.");
      port.Write(bytes, 0, bytes.Length); Emit("sent", parts[1]);
     } else if (parts[0] == "CLOSE") { if (port != null) { port.Close(); port.Dispose(); port = null; } Emit("closed", true); }
    } catch (Exception e) { Emit("error", e.Message); }
    input = Task.Run(() => Console.ReadLine());
   }
   if (port != null && port.IsOpen) {
    try { int count = Math.Min(port.BytesToRead, 4096); if (count > 0) { byte[] bytes = new byte[count]; count = port.Read(bytes, 0, count); Emit("data", Convert.ToBase64String(bytes, 0, count)); } }
    catch (Exception e) { Emit("error", e.Message); port.Dispose(); port = null; Emit("closed", true); }
   }
   Thread.Sleep(15);
  } } finally { if (port != null) port.Dispose(); }
 }
}`;

function serialBytes(text, mode = 'ASCII', newline = 'none') {
  if (typeof text !== 'string' || text.length > 131072) throw new Error('串口发送内容过大。');
  let data;
  if (mode === 'HEX') {
    const hex = text.replace(/\s+/g, '');
    if (!hex || hex.length % 2 || !/^[0-9a-f]+$/i.test(hex)) throw new Error('HEX 需要完整的十六进制字节，例如 01 A0 FF。');
    data = Buffer.from(hex, 'hex');
  } else {
    if (mode !== 'ASCII' || /[^\x00-\x7f]/.test(text)) throw new Error('ASCII 模式仅接受 ASCII 字符。');
    data = Buffer.from(text, 'ascii');
  }
  if (!['none', 'LF', 'CRLF'].includes(newline)) throw new Error('请选择有效的串口换行方式。');
  data = Buffer.concat([data, Buffer.from(newline === 'LF' ? '\n' : newline === 'CRLF' ? '\r\n' : '')]);
  if (data.length > 65536) throw new Error('串口发送内容过大。');
  return data;
}
function displayBytes(bytes, mode) {
  return mode === 'HEX' ? bytes.toString('hex').match(/.{2}/g)?.join(' ').toUpperCase() || ''
    : [...bytes].map(byte => byte >= 32 && byte <= 126 || [9, 10, 13].includes(byte) ? String.fromCharCode(byte) : '\\x' + byte.toString(16).padStart(2, '0')).join('');
}
class SerialSession {
  constructor(onChange) { this.onChange = onChange; this.state = { ports: [], connected: false, lines: [], receivedBytes: 0, sentBytes: 0 }; this.waiter = undefined; this.sequence = 0; this.lastSent = 0; }
  start() {
    if (this.child) return;
    if (process.platform !== 'win32') throw new Error('串口助手仅支持本地 Windows。');
    const script = "Add-Type -TypeDefinition @'\n" + SERIAL_PROGRAM + "\n'@ -ReferencedAssemblies 'System.dll','System.Core.dll','System.Web.Extensions.dll'; [Stm32SerialSession]::Run()";
    const executable = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const child = spawn(executable, ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child = child; let stderr = '';
    const reader = readline.createInterface({ input: child.stdout });
    child.stderr.on('data', bytes => { stderr = (stderr + bytes).slice(-8192); });
    child.stdin.on('error', error => this.fail(error));
    child.on('error', error => this.fail(error));
    reader.on('line', line => { try { this.receive(JSON.parse(line)); } catch (error) { this.fail(error); } });
    child.on('close', () => { reader.close(); if (this.child !== child) return; this.child = undefined; this.state.connected = false; if (this.waiter) this.fail(new Error(stderr || '串口后台已退出。')); this.onChange(this.snapshot()); });
  }
  receive(message) {
    if (message.type === 'ports') this.state.ports = message.data.map(name => ({ name, description: name }));
    if (message.type === 'opened') { this.state.connected = true; this.state.port = message.data.name; this.state.baud = message.data.baud; this.state.error = undefined; }
    if (message.type === 'closed') this.state.connected = false;
    if (message.type === 'data' || message.type === 'sent') {
      const bytes = Buffer.from(message.data, 'base64');
      this.state[message.type === 'data' ? 'receivedBytes' : 'sentBytes'] += bytes.length;
      this.state.lines.push({ id: ++this.sequence, time: new Date().toISOString(), direction: message.type === 'data' ? 'RX' : 'TX', text: displayBytes(bytes, this.mode) });
      while (this.state.lines.length > 1500 || this.state.lines.reduce((sum, line) => sum + line.text.length, 0) > 1024 * 1024) this.state.lines.shift();
    }
    if (message.type === 'error') this.fail(new Error(message.data));
    else if (this.waiter?.type === message.type) { const waiter = this.waiter; this.waiter = undefined; clearTimeout(waiter.timer); waiter.resolve(this.snapshot()); }
    if (!this.notifyTimer) this.notifyTimer = setTimeout(() => { this.notifyTimer = undefined; this.onChange(this.snapshot(true)); }, 100);
  }
  fail(error) { this.state.error = error.message; if (this.waiter) { const waiter = this.waiter; this.waiter = undefined; clearTimeout(waiter.timer); waiter.reject(error); } this.onChange(this.snapshot()); }
  command(command, type) {
    this.start(); if (this.waiter) return Promise.reject(new Error('串口操作尚未完成。'));
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.waiter = undefined; this.dispose(); reject(new Error('串口操作超时。')); }, 10000);
      this.waiter = { type, resolve, reject, timer }; this.child.stdin.write(command + '\n', error => { if (error) this.fail(error); });
    });
  }
  list() { return this.command('LIST', 'ports'); }
  connect(port, baud, mode) { if (!/^COM[1-9]\d{0,3}$/i.test(port) || !Number.isInteger(baud) || baud < 50 || baud > 4000000) throw new Error('请选择有效的 COM 端口和波特率。'); this.mode = mode === 'HEX' ? 'HEX' : 'ASCII'; return this.command(`OPEN\t${port.toUpperCase()}\t${baud}`, 'opened'); }
  send(text, mode, newline) { this.mode = mode; return this.command('SEND\t' + serialBytes(text, mode, newline).toString('base64'), 'sent'); }
  disconnect() { return this.child ? this.command('CLOSE', 'closed') : Promise.resolve(this.snapshot()); }
  snapshot(delta = false) { const lines = delta ? this.state.lines.filter(line => line.id > this.lastSent) : this.state.lines.slice(-250); if (delta) this.lastSent = this.sequence; return { ...this.state, lines, delta }; }
  export() { return this.state.lines.map(line => `${line.time}\t${line.direction}\t${line.text}`).join('\r\n'); }
  dispose() { clearTimeout(this.notifyTimer); const child = this.child; this.child = undefined; this.state.connected = false; if (this.waiter) this.fail(new Error('串口连接已关闭。')); if (child) { child.stdin.end(); void terminateProcessTree(child); } }
}
module.exports = { SerialSession, serialBytes, displayBytes, SERIAL_PROGRAM };
