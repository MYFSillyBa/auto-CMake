import * as net from 'net';

/**
 * TCP client for the OpenOCD TCL interface (default port 6666).
 *
 * Protocol:
 *   Send: "command\x1a"
 *   Recv: "response\x1a"
 *
 * Use plain TCL commands wrapped by `capture` so memory read/write traffic
 * does not flood VSCode Debug Console.
 */
export class OpenOcdTclClient {
    onClose?: () => void;
    private static readonly COMMAND_TIMEOUT_MS = 3000;
    private static captureMarkerSeq = 0;
    private socket: net.Socket | null = null;
    private host: string;
    private port: number;
    private connected: boolean = false;
    private pendingResolve: ((value: string) => void) | null = null;
    private pendingReject: ((reason: Error) => void) | null = null;
    private pendingCommand: string | null = null;
    private pendingTimer: ReturnType<typeof setTimeout> | null = null;
    private recvBuffer: string = '';
    private commandQueue: Array<{
        cmd: string;
        resolve: (value: string) => void;
        reject: (reason: Error) => void;
    }> = [];
    private busy: boolean = false;
    private commandStyle: 'unknown' | 'ocd' | 'capture' = 'unknown';

    constructor(host: string = '127.0.0.1', port: number = 6666) {
        this.host = host;
        this.port = port;
    }

    get isConnected(): boolean {
        return this.connected;
    }

    get endpoint(): string {
        return `${this.host.includes(':') ? '[' + this.host + ']' : this.host}:${this.port}`;
    }

    updateConfig(host: string, port: number): void {
        if (this.host !== host || this.port !== port) {
            this.host = host;
            this.port = port;
            if (this.connected) {
                this.disconnect();
            }
        }
    }

    /**
     * Connect to the OpenOCD TCL server.
     */
    connect(): Promise<void> {
        return new Promise((resolve, reject) => {
            let settled = false;
            const safeResolve = () => {
                if (settled) { return; }
                settled = true;
                resolve();
            };
            const safeReject = (err: Error) => {
                if (settled) { return; }
                settled = true;
                reject(err);
            };

            if (this.connected && this.socket) {
                safeResolve();
                return;
            }
            this.disconnect();

            const socket = new net.Socket();
            socket.setEncoding('utf-8');
            socket.setNoDelay(true);
            socket.setTimeout(3000);

            socket.on('connect', () => {
                if (this.socket !== socket) { socket.destroy(); return; }
                this.connected = true;
                this.commandStyle = 'unknown';
                socket.setTimeout(0);
                safeResolve();
            });

            socket.on('data', (data: string) => {
                if (this.socket !== socket) return;
                this.recvBuffer += data;
                if (this.recvBuffer.length > 1024 * 1024) { this.disconnect(); this.onClose?.(); return; }
                let eom = this.recvBuffer.indexOf('\x1a');
                while (eom !== -1) {
                    const response = this.recvBuffer.substring(0, eom);
                    this.recvBuffer = this.recvBuffer.substring(eom + 1);
                    this.resolvePendingCommand(response);
                    eom = this.recvBuffer.indexOf('\x1a');
                }
            });

            socket.on('error', (err) => {
                if (this.socket !== socket) { safeReject(err); return; }
                this.connected = false;
                if (this.pendingReject) {
                    this.rejectPendingCommand(err);
                }
                safeReject(new Error(`Failed to connect to OpenOCD at ${this.endpoint}: ${err.message}`));
            });

            socket.on('close', () => {
                safeReject(new Error('Connection closed'));
                if (this.socket !== socket) return;
                this.connected = false;
                this.socket = null;
                if (this.pendingReject) {
                    this.rejectPendingCommand(new Error('Connection closed'));
                }
                // Reject all queued commands
                for (const item of this.commandQueue) {
                    item.reject(new Error('Connection closed'));
                }
                this.commandQueue = [];
                this.busy = false;
                this.onClose?.();
            });

            socket.on('timeout', () => {
                socket.destroy();
                safeReject(new Error(`Connection timeout to OpenOCD at ${this.endpoint}`));
            });

            socket.connect(this.port, this.host);
            this.socket = socket;
        });
    }

    disconnect(): void {
        if (this.pendingReject) {
            this.rejectPendingCommand(new Error('Disconnected'));
        }
        if (this.commandQueue.length > 0) {
            for (const item of this.commandQueue) {
                item.reject(new Error('Disconnected'));
            }
        }
        if (this.socket) {
            this.socket.destroy();
            this.socket = null;
        }
        this.connected = false;
        this.recvBuffer = '';
        this.pendingResolve = null;
        this.pendingReject = null;
        this.pendingCommand = null;
        this.clearPendingTimer();
        this.commandQueue = [];
        this.busy = false;
        this.commandStyle = 'unknown';
    }

    /**
     * Execute a TCL command and return the response.
     */
    execute(command: string): Promise<string> {
        return new Promise((resolve, reject) => {
            if (!this.connected || !this.socket) {
                reject(new Error('Not connected'));
                return;
            }
            this.commandQueue.push({ cmd: command, resolve, reject });
            this.drainQueue();
        });
    }

    /**
     * Execute an OpenOCD command in quiet mode.
     */
    private async executeOcd(commandWithoutPrefix: string): Promise<string> {
        if (this.commandStyle === 'ocd') {
            return this.execute(`ocd_${commandWithoutPrefix}`);
        }
        if (this.commandStyle === 'capture') {
            return this.executeCaptureCommand(commandWithoutPrefix);
        }

        // Unknown style: probe once and cache the best available quiet path.
        const ocdResp = await this.execute(`ocd_${commandWithoutPrefix}`);
        if (!this.isInvalidCommandResponse(ocdResp, 'ocd_')) {
            this.commandStyle = 'ocd';
            return ocdResp;
        }

        const captureResp = await this.executeCaptureCommand(commandWithoutPrefix);
        if (!this.isInvalidCommandResponse(captureResp, 'capture')) {
            this.commandStyle = 'capture';
            return captureResp;
        }

        throw new Error(
            `OpenOCD at ${this.endpoint} does not support quiet TCL commands (ocd_* / capture). ` +
            'Refusing plain mdw/mww to avoid flooding Debug Console.'
        );
    }

    /**
     * Execute command on plain-style OpenOCD servers.
     * Wrap in `capture` to avoid flooding debug console with mdw/mdb log lines.
     */
    private executeCaptureCommand(commandWithoutPrefix: string): Promise<string> {
        // Prefix each capture with a unique marker so we can discard any
        // leading noise before the actual command output.
        const marker = `__livewatch_${++OpenOcdTclClient.captureMarkerSeq}__`;
        return this.execute(`capture {echo ${marker}; ${commandWithoutPrefix}}`)
            .then((response) => this.stripCaptureMarker(response, marker, commandWithoutPrefix));
    }

    private stripCaptureMarker(response: string, marker: string, command: string): string {
        const idx = response.indexOf(marker);
        if (idx < 0) {
            throw new Error(
                `Missing capture marker for OpenOCD command "${command}". Raw: ${response.trim()}`
            );
        }

        let payload = response.substring(idx + marker.length);
        payload = payload.replace(/^\r?\n/, '');
        return payload;
    }

    private isInvalidCommandResponse(resp: string, marker?: string): boolean {
        const s = (resp || '').toLowerCase();
        if (marker && s.includes(`invalid command name "${marker.toLowerCase()}`)) {
            return true;
        }
        return (
            s.includes('invalid command name') ||
            s.includes('unknown command') ||
            s.includes('unknown or ambiguous command') ||
            s.includes('wrong # args')
        );
    }

    private drainQueue(): void {
        if (this.busy || this.commandQueue.length === 0) { return; }
        if (!this.socket || !this.connected) { return; }

        const item = this.commandQueue.shift()!;
        this.busy = true;
        this.pendingCommand = item.cmd;
        this.pendingResolve = item.resolve;
        this.pendingReject = item.reject;
        this.pendingTimer = setTimeout(() => {
            const message = this.pendingCommand
                ? `OpenOCD command timed out after ${OpenOcdTclClient.COMMAND_TIMEOUT_MS} ms: ${this.pendingCommand}`
                : `OpenOCD command timed out after ${OpenOcdTclClient.COMMAND_TIMEOUT_MS} ms`;
            this.rejectPendingCommand(new Error(message));
            this.disconnect();
        }, OpenOcdTclClient.COMMAND_TIMEOUT_MS);
        this.socket.write(item.cmd + '\x1a');
    }

    private resolvePendingCommand(response: string): void {
        if (!this.pendingResolve) {
            return;
        }
        const resolve = this.pendingResolve;
        this.pendingResolve = null;
        this.pendingReject = null;
        this.pendingCommand = null;
        this.clearPendingTimer();
        this.busy = false;
        resolve(response);
        this.drainQueue();
    }

    private rejectPendingCommand(err: Error): void {
        if (!this.pendingReject) {
            this.pendingResolve = null;
            this.pendingCommand = null;
            this.clearPendingTimer();
            this.busy = false;
            return;
        }
        const reject = this.pendingReject;
        this.pendingResolve = null;
        this.pendingReject = null;
        this.pendingCommand = null;
        this.clearPendingTimer();
        this.busy = false;
        reject(err);
    }

    private clearPendingTimer(): void {
        if (this.pendingTimer) {
            clearTimeout(this.pendingTimer);
            this.pendingTimer = null;
        }
    }

    // ── Memory read/write helpers ──────────────────────────────

    /**
     * Read `count` 32-bit words starting at `address`.
     * Returns an array of raw 32-bit values.
     */
    async readMemory32(address: number, wordCount: number): Promise<number[]> {
        const addrHex = '0x' + address.toString(16);
        const response = await this.executeOcd(`mdw ${addrHex} ${wordCount}`);
        return this.parseMdwResponse(response, address, wordCount, 4);
    }

    /**
     * Read `count` 16-bit halfwords starting at `address`.
     */
    async readMemory16(address: number, halfwordCount: number): Promise<number[]> {
        const addrHex = '0x' + address.toString(16);
        const response = await this.executeOcd(`mdh ${addrHex} ${halfwordCount}`);
        return this.parseMdwResponse(response, address, halfwordCount, 2);
    }

    /**
     * Read `count` bytes starting at `address`.
     */
    async readMemory8(address: number, byteCount: number): Promise<number[]> {
        const addrHex = '0x' + address.toString(16);
        const response = await this.executeOcd(`mdb ${addrHex} ${byteCount}`);
        return this.parseMdwResponse(response, address, byteCount, 1);
    }

    /**
     * Write a 32-bit word to `address`.
     */
    async writeMemory32(address: number, value: number): Promise<void> {
        const addrHex = '0x' + address.toString(16);
        const valHex = '0x' + (value >>> 0).toString(16);
        await this.writeMemory(`mww ${addrHex} ${valHex}`);
    }

    /**
     * Write a 16-bit halfword to `address`.
     */
    async writeMemory16(address: number, value: number): Promise<void> {
        const addrHex = '0x' + address.toString(16);
        const valHex = '0x' + (value & 0xFFFF).toString(16);
        await this.writeMemory(`mwh ${addrHex} ${valHex}`);
    }

    /**
     * Write a byte to `address`.
     */
    async writeMemory8(address: number, value: number): Promise<void> {
        const addrHex = '0x' + address.toString(16);
        const valHex = '0x' + (value & 0xFF).toString(16);
        await this.writeMemory(`mwb ${addrHex} ${valHex}`);
    }

    private async writeMemory(command: string): Promise<void> {
        const result = (await this.executeOcd(command)).trim();
        if (result) throw new Error(result);
    }

    /**
     * Parse OpenOCD mdw/mdh/mdb response.
     * Format: "0x20001000: 41700000 3e4ccccd 00000000 ..."
     * Can also be multiple lines for large reads.
     */
    private parseMdwResponse(
        response: string,
        requestAddress?: number,
        requestCount?: number,
        unitBytes: number = 4,
    ): number[] {
        if (/invalid command name/i.test(response)) {
            throw new Error(response.trim());
        }

        const valueByAddress = new Map<number, number>();
        const lines = response.trim().split('\n');
        const memLineRegex = /^\s*0x([0-9a-fA-F]+)\s*:\s*(.+)$/;
        let firstLineAddr: number | null = null;

        for (const line of lines) {
            const m = line.match(memLineRegex);
            if (!m) { continue; }

            let lineAddr = parseInt(m[1], 16) >>> 0;
            if (firstLineAddr === null) {
                firstLineAddr = lineAddr;
            }
            const dataStr = m[2].trim();
            if (!dataStr) { continue; }
            const items = dataStr.split(/\s+/);
            for (const item of items) {
                const token = item.replace(/^0x/i, '');
                if (!/^[0-9a-fA-F]+$/.test(token)) {
                    lineAddr = (lineAddr + unitBytes) >>> 0;
                    continue;
                }
                const v = parseInt(token, 16);
                if (!isNaN(v)) {
                    valueByAddress.set(lineAddr, v >>> 0);
                }
                lineAddr = (lineAddr + unitBytes) >>> 0;
            }
        }

        if (requestAddress !== undefined && requestCount !== undefined) {
            if (requestCount <= 0) { return []; }
            const normalizedRequestAddr = requestAddress >>> 0;
            if (firstLineAddr === null || firstLineAddr !== normalizedRequestAddr) {
                throw new Error(
                    `Unexpected OpenOCD read start address ` +
                    `(expected 0x${normalizedRequestAddr.toString(16)}, ` +
                    `got ${firstLineAddr === null ? 'none' : `0x${firstLineAddr.toString(16)}`}). ` +
                    `Raw: ${response.trim()}`
                );
            }
            const ordered: number[] = [];
            for (let i = 0; i < requestCount; i++) {
                const addr = (requestAddress + i * unitBytes) >>> 0;
                const value = valueByAddress.get(addr);
                if (value === undefined) {
                    throw new Error(
                        `Incomplete OpenOCD read response at 0x${addr.toString(16)} ` +
                        `(requested ${requestCount} x ${unitBytes}-byte units from 0x${requestAddress.toString(16)}). ` +
                        `Raw: ${response.trim()}`
                    );
                }
                ordered.push(value);
            }
            return ordered;
        }

        if (valueByAddress.size === 0 && response.trim().length > 0) {
            throw new Error(`Unexpected OpenOCD read response: ${response.trim()}`);
        }
        const entries = Array.from(valueByAddress.entries()).sort((a, b) => a[0] - b[0]);
        return entries.map(([, value]) => value);
    }
}
