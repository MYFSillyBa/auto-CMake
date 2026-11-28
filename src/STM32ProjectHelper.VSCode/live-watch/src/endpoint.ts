import { execFile } from 'node:child_process';
import net from 'node:net';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const localHosts = new Set(['localhost', '127.0.0.1', '::1']);
export type Endpoint = { host: string; port: number };

export function portNumber(value: unknown): number | undefined {
    const n = typeof value === 'number' ? value : typeof value === 'string' && /^\d{1,5}$/.test(value) ? Number(value) : NaN;
    return Number.isInteger(n) && n >= 1 && n <= 65535 ? n : undefined;
}

export function portFromArgs(args: unknown): number | undefined {
    const values = Array.isArray(args) ? args : [];
    let port: number | undefined;
    for (const value of values) {
        if (typeof value !== 'string') continue;
        for (const match of value.matchAll(/(?:^|[;\s])tcl(?:_port|\s+port)\s+["']?(\d{1,5}|disabled)\b/gi)) {
            port = portNumber(match[1]);
        }
    }
    return port;
}

function processIds(text: string): Set<string> {
    return new Set([...text.matchAll(/^"openocd(?:\.exe)?","(\d+)"/gim)].map(match => match[1]));
}

export function localOpenOcdPorts(processes: string, rows: string): Endpoint[] {
    const pids = processIds(processes);
    const endpoints = new Map<string, Endpoint>();
    for (const line of rows.split(/\r?\n/)) {
        const match = /^\s*TCP\s+(\S+):(\d+)\s+(?:0\.0\.0\.0:0|\[::\]:0)\s+\S+\s+(\d+)\s*$/i.exec(line);
        if (!match || !pids.has(match[3])) continue;
        const bind = match[1].replace(/^\[|\]$/g, '');
        if (!localHosts.has(bind) && bind !== '0.0.0.0' && bind !== '::') continue;
        const host = bind.includes(':') ? '::1' : '127.0.0.1';
        const port = portNumber(match[2]);
        if (port) endpoints.set(host + ':' + port, { host, port });
    }
    return [...endpoints.values()].sort((a, b) => Number(b.port === 6666) - Number(a.port === 6666) || a.port - b.port);
}

export async function windowsOpenOcdPorts(): Promise<Endpoint[]> {
    const options = { windowsHide: true, timeout: 4000, maxBuffer: 1024 * 1024 };
    const [processes, sockets] = await Promise.all([
        exec('tasklist.exe', ['/fi', 'IMAGENAME eq openocd.exe', '/fo', 'csv', '/nh'], options),
        exec('netstat.exe', ['-ano'], options),
    ]);
    return localOpenOcdPorts(processes.stdout, sockets.stdout);
}

export function isTclServer(endpoint: Endpoint, timeout = 350): Promise<boolean> {
    return new Promise(resolve => {
        if (!localHosts.has(endpoint.host)) { resolve(false); return; }
        const socket = net.createConnection(endpoint);
        let data = '';
        let done = false;
        const finish = (value: boolean) => {
            if (done) return;
            done = true;
            socket.destroy();
            resolve(value);
        };
        socket.setEncoding('utf8');
        socket.setTimeout(timeout);
        socket.on('connect', () => socket.write('capture {version}\x1a'));
        socket.on('data', chunk => {
            data += chunk;
            const end = data.indexOf('\x1a');
            if (end !== -1) finish(/Open On-Chip Debugger|\bOpenOCD\b/i.test(data.slice(0, end)));
            else if (data.length > 8192) finish(false);
        });
        socket.on('error', () => finish(false));
        socket.on('timeout', () => finish(false));
        socket.on('close', () => finish(false));
    });
}
