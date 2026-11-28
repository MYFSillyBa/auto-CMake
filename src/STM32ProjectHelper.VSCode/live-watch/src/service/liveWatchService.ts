import * as vscode from 'vscode';
import { OpenOcdTclClient } from '../transport/openOcdTclClient';
import { GdbSymbolResolver } from '../elf/gdbSymbolResolver';
import { stat } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import {
    WatchNode,
    DisplayFormat,
    createWatchNode,
    flattenTree,
    formatValue,
    parseWriteValue,
    ViewRow,
} from '../model/watchNode';

export type ConnectionState = 'connected' | 'disconnected' | 'connecting';
export const POLL_INTERVALS = [500, 200, 100, 50, 33, 20, 10, 5, 1, 0] as const;
export const DEFAULT_POLL_INTERVAL = 200;

export type ServiceErrorCode =
    | 'connect_failed'
    | 'write_failed'
    | 'read_failed'
    | 'generic';

export interface ServiceEvent {
    type: 'treeUpdate' | 'connectionChange' | 'error' | 'pauseChange' | 'addWatchResult';
    rows?: ViewRow[];
    connectionState?: ConnectionState;
    paused?: boolean;
    message?: string;
    errorCode?: ServiceErrorCode;
    addWatchName?: string;
    addWatchSuccess?: boolean;
    addWatchReqId?: number;
}

interface Environment {
    endpoint: () => Promise<{ host: string; port: number } | undefined>;
    firmware: () => Promise<{ path: string; gdb: string; message?: string }>;
}

/**
 * Central service that coordinates:
 *   - GDB symbol resolution (ELF/DWARF)
 *   - OpenOCD TCL memory reading
 *   - Polling scheduler
 *   - Watch node state management
 */
export class LiveWatchService implements vscode.Disposable {
    private ocd: OpenOcdTclClient;
    private gdb: GdbSymbolResolver;
    private watchList: WatchNode[] = [];
    private cancelPoll: (() => void) | null = null;
    private pollInterval = DEFAULT_POLL_INTERVAL;
    private lastPollStarted = -Infinity;
    private visible = false;
    private autoConnect = true;
    private symbols: Promise<void> = Promise.resolve();
    private revision = 0;
    private connecting = 0;
    private activeGdb = '';
    private activeFirmwareStamp = '';
    private ioCount = 0;
    private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    private reconnectInFlight: boolean = false;
    private paused: boolean = false;
    private polling: boolean = false;
    private connectionState: ConnectionState = 'disconnected';
    private readFailureBackoff = new Map<string, { address: number; failures: number; nextRetryAt: number }>();
    private readonly pointerRetryAt = new WeakMap<WatchNode, number>();
    private listeners: ((event: ServiceEvent) => void)[] = [];
    private context: vscode.ExtensionContext;
    private disposed: boolean = false;

    constructor(context: vscode.ExtensionContext, private readonly environment: Environment) {
        this.context = context;
        const config = vscode.workspace.getConfiguration('stm32ProjectHelper.liveWatch');
        this.configureRamRegions(config, false);
        this.ocd = new OpenOcdTclClient(
            '127.0.0.1', 6666,
        );
        this.gdb = new GdbSymbolResolver(
            config.get('gdbPath', 'arm-none-eabi-gdb'),
            config.get('elfPath', ''),
        );

        // Restore saved watch list
        const saved = context.workspaceState.get<string[]>('stm32ProjectHelper.liveWatch.variables', []);
        // We'll resolve these after connection
        this.savedVariables = saved;

        // Listen for config changes
        this.disposables.push(
            vscode.workspace.onDidChangeConfiguration(e => {
                if (e.affectsConfiguration('stm32ProjectHelper.liveWatch')) {
                    this.applyConfig();
                }
            })
        );

        this.ocd.onClose = () => {
            if (this.connectionState !== 'connected') return;
            this.disconnect();
            this.scheduleReconnect();
        };
    }

    private savedVariables: string[] = [];
    private disposables: vscode.Disposable[] = [];
    private static readonly MIN_VALID_ADDR = 0x10000;
    private static readonly RECONNECT_MIN_MS = 1000;
    private static readonly RECONNECT_MAX_MS = 2000;
    private static readonly MAX_AUTO_POINTER_DEREF_DEPTH = 3;
    private static readonly READ_FAILURE_BASE_BACKOFF_MS = 250;
    private static readonly READ_FAILURE_MAX_BACKOFF_MS = 10_000;
    private static readonly DEFAULT_RAM_REGIONS: Array<{ start: number; end: number }> = [
        // Conservative defaults for common STM32 SRAM windows.
        { start: 0x20000000, end: 0x20020000 },
        { start: 0x10000000, end: 0x10010000 },
        { start: 0x24000000, end: 0x24100000 },
        { start: 0x30000000, end: 0x30080000 },
        { start: 0x38000000, end: 0x38010000 },
    ];
    private elfResolutionIssue: string = '';
    private ramRegions: Array<{ start: number; end: number }> = [...LiveWatchService.DEFAULT_RAM_REGIONS];

    onEvent(listener: (event: ServiceEvent) => void): void {
        this.listeners.push(listener);
    }

    private emit(event: ServiceEvent): void {
        for (const fn of this.listeners) {
            fn(event);
        }
    }

    private emitTree(): void {
        this.emit({ type: 'treeUpdate' });
    }

    private emitConnection(): void {
        this.emit({ type: 'connectionChange', connectionState: this.connectionState });
    }

    // ── Configuration ──────────────────────────────────────────

    private applyConfig(): void {
        const config = vscode.workspace.getConfiguration('stm32ProjectHelper.liveWatch');
        this.configureRamRegions(config, true);
        this.reconnect();
    }

    getElfPath(): string {
        return this.detectedElfPath;
    }

    getEndpoint(): string {
        return this.connectionState === 'connected' ? this.ocd.endpoint : '';
    }

    setVisible(visible: boolean): void {
        this.visible = visible;
        if (visible) {
            this.startPolling();
            this.scheduleReconnect();
        } else this.stopPolling();
    }

    reconnect(): void {
        this.disconnect();
        if (this.autoConnect) void this.tryConnect();
    }

    setAutoConnect(enabled: boolean): void {
        this.autoConnect = enabled;
        this.reconnect();
    }

    // ── Connection ─────────────────────────────────────────────

    async connect(): Promise<void> {
        if (!this.autoConnect || this.disposed || this.connectionState === 'connected' || this.connectionState === 'connecting') return;
        this.stopReconnectLoop();
        const generation = ++this.connecting;
        this.connectionState = 'connecting';

        try {
            const endpoint = await this.environment.endpoint();
            if (generation !== this.connecting || this.disposed) return;
            if (!endpoint) { this.connectionState = 'disconnected'; this.emitConnection(); this.scheduleReconnect(); return; }
            this.emitConnection();
            this.ocd.updateConfig(endpoint.host, endpoint.port);
            await this.ocd.connect();
            if (generation !== this.connecting || this.disposed) return;
            this.connectionState = 'connected';
            this.emitConnection();
            this.stopReconnectLoop();
            await this.refreshSymbols(false);
            if (generation !== this.connecting || this.disposed) return;
            this.startPolling();
        } catch (err: any) {
            if (generation !== this.connecting || this.disposed) return;
            this.ocd.disconnect();
            this.connectionState = 'disconnected';
            this.emitConnection();
            this.scheduleReconnect();
            throw err;
        }
    }

    async tryConnect(silent: boolean = true): Promise<void> {
        try {
            await this.connect();
        } catch (err: any) {
            if (!silent) {
                const message = err instanceof Error ? err.message : String(err);
                this.emit({
                    type: 'error',
                    errorCode: 'connect_failed',
                    message: `OpenOCD connect failed (${this.ocd.endpoint}): ${message}`,
                });
            }
        }
    }

    disconnect(): void {
        this.connecting++;
        this.revision++;
        this.stopReconnectLoop();
        this.stopPolling();
        this.connectionState = 'disconnected';
        this.ocd.disconnect();
        this.readFailureBackoff.clear();
        const clear = (node: WatchNode) => {
            node.hasRawValue = false;
            node.value = '';
            for (const child of node.children) clear(child);
        };
        for (const node of this.watchList) clear(node);
        this.emitConnection();
    }

    getConnectionState(): ConnectionState {
        return this.connectionState;
    }

    // ── Watch management ───────────────────────────────────────

    addWatch(variableName: string): Promise<void> {
        return this.symbolOperation(async () => {
            await this.reloadSymbols(false);
            await this.addWatchWithSymbols(variableName, true);
        });
    }

    private async addWatchWithSymbols(variableName: string, save: boolean): Promise<void> {
        const name = variableName.trim();
        if (!name || name.length > 240 || this.watchList.length >= 128) { return; }

        // Check duplicate
        if (this.watchList.some(n => n.name === name)) {
            this.emit({
                type: 'addWatchResult',
                addWatchName: name,
                addWatchSuccess: false,
                message: `"${name}" is already being watched`,
            });
            return;
        }

        let node: WatchNode;
        let addMessage: string | undefined;

        const elfPath = this.getElfPath();
        if (!elfPath) {
            addMessage = this.elfResolutionIssue ||
                'No ELF firmware found. Choose a firmware file.';
            node = this.makeErrorWatchNode(name, addMessage);
        } else {
            try {
                node = await this.gdb.resolve(name);
                if (node.error) {
                    addMessage = node.error;
                    node = this.makeErrorWatchNode(name, addMessage, node);
                } else {
                    try {
                        await this.hydratePointerDerefLayouts(node);
                        await this.resolveRuntimePointerChainAddress(node);
                    } catch (err: any) {
                        addMessage = err instanceof Error ? err.message : String(err);
                        node = this.makeErrorWatchNode(name, addMessage, node);
                    }
                }
            } catch (err: any) {
                addMessage = `Failed to resolve "${name}": ${err.message}`;
                node = this.makeErrorWatchNode(name, addMessage);
            }
        }

        const setIds = (item: WatchNode, id: string) => {
            item.id = id;
            item.children.forEach((child, index) => setIds(child, `${id}/${index}`));
        };
        setIds(node, JSON.stringify(name));
        this.watchList.push(node);
        this.startPolling();
        this.emitTree();
        this.emit({
            type: 'addWatchResult',
            addWatchName: name,
            addWatchSuccess: true,
            message: addMessage,
        });

        if (save) {
            this.saveWatchList();
        }
    }

    private makeErrorWatchNode(name: string, message: string, source?: WatchNode): WatchNode {
        const node = source ?? createWatchNode(name, name, '???', 0, 0, true);
        node.error = message;
        node.value = '';
        node.rawValue = 0;
        node.hasRawValue = false;
        // Keep parsed tree metadata when available so pointer-chain watches
        // can recover automatically on later polls.
        if (!source) {
            node.address = 0;
            node.size = 0;
            node.children = [];
        }
        node.expanded = true;
        return node;
    }

    /**
     * Resolve the active ELF path from explicit settings or safe auto-detect.
     */
    private async reloadSymbols(force: boolean): Promise<void> {
        const detected = await this.environment.firmware();
        const currentStamp = detected.path ? await stat(detected.path).then(info => `${info.mtimeMs}:${info.size}`, () => '') : '';
        this.elfResolutionIssue = detected.message ?? '';
        const changed = this.detectedElfPath !== detected.path || this.activeGdb !== detected.gdb || this.activeFirmwareStamp !== currentStamp;
        if (this.disposed || !force && !changed && !this.savedVariables.length) return;
        const names = [...new Set([...this.savedVariables, ...this.watchList.map(node => node.name)])];
        this.savedVariables = [];
        this.watchList = [];
        this.revision++;
        this.readFailureBackoff.clear();
        this.detectedElfPath = detected.path;
        this.activeGdb = detected.gdb;
        this.activeFirmwareStamp = currentStamp;
        this.gdb.updateConfig(detected.gdb, detected.path);
        for (const name of names) {
            if (this.disposed) break;
            await this.addWatchWithSymbols(name, false);
        }
        this.emitTree();
    }

    private detectedElfPath: string = '';

    /**
     * For `->` expressions, GDB-in-ELF mode may return only an offset
     * (e.g. 0x2c). Recover the real address by reading the runtime pointer.
     */
    private async resolveRuntimePointerChainAddress(root: WatchNode): Promise<boolean> {
        if (root.pointerBaseExpr === undefined) root.pointerBaseExpr = await this.findPointerBaseExpression(root.expression);
        const pointerExpr = root.pointerBaseExpr;
        if (!pointerExpr) return false;
        if (!this.ocd.isConnected) {
            throw new Error('Pointer-chain expression needs active target connection');
        }

        // Resolve and cache pointer-base metadata once per root expression.
        if (!root.pointerStorageAddress || !root.pointerBaseType) {
            const pointerStorageAddr = await this.gdb.resolveAddress(pointerExpr);
            if (pointerStorageAddr === null || pointerStorageAddr < LiveWatchService.MIN_VALID_ADDR) {
                throw new Error(`Failed to resolve pointer base: ${pointerExpr}`);
            }
            const pointerTypeName = await this.gdb.resolveType(pointerExpr);
            if (!pointerTypeName) {
                throw new Error(`Failed to resolve pointer type: ${pointerExpr}`);
            }
            if (!this.isPointerType(pointerTypeName)) {
                throw new Error(
                    `Left side of "${root.expression}" is not a raw pointer ` +
                    `(${pointerExpr}: ${pointerTypeName}). operator-> proxies are not supported.`
                );
            }
            root.pointerStorageAddress = pointerStorageAddr;
            root.pointerBaseType = pointerTypeName;
        }

        const pointerStorageAddr = root.pointerStorageAddress!;
        const pointerTypeName = root.pointerBaseType!;

        const rootSpanBytes = Math.max(root.size, 4);
        const words = await this.readMemory32WithBackoff(root, pointerStorageAddr, 4);
        if (!words?.length) {
            throw new Error(`Cannot read pointer value: ${pointerExpr}`);
        }
        const pointerValue = words[0] >>> 0;
        if (!this.isLikelyRamRange(pointerValue, rootSpanBytes)) {
            throw new Error(
                `Pointer "${pointerExpr}" resolved via ${this.formatHex(pointerStorageAddr)} ` +
                `to non-RAM value ${this.formatHex(pointerValue)} ` +
                `(ELF: ${this.getElfPath() || '<auto-detect failed>'}). ` +
                'This usually means the running firmware does not match the ELF, ' +
                'or the pointer has not been initialized yet. ' +
                'If your target uses non-default RAM ranges, set stm32ProjectHelper.liveWatch.ramRegions.'
            );
        }
        // Capture relative offsets once, then rebase absolutely each poll.
        if (root.relativeAddress === undefined) {
            this.capturePointerRootRelativeAddresses(root);
        }
        this.rebasePointerRootAddresses(root, pointerValue);
        return true;
    }

    private shiftNodeAddress(node: WatchNode, delta: number): void {
        // Synthetic/static nodes use address=0,size=0 and must not be rebased.
        if ((node.size > 0 || node.isRoot) && !node.pointerDeref) {
            node.address += delta;
        }
        for (const child of node.children) {
            this.shiftNodeAddress(child, delta);
        }
    }

    private isLikelyRamAddress(address: number): boolean {
        for (const region of this.ramRegions) {
            if (address >= region.start && address < region.end) {
                return true;
            }
        }
        return false;
    }

    private isLikelyRamRange(address: number, sizeBytes: number): boolean {
        if (sizeBytes <= 0) {
            return this.isLikelyRamAddress(address);
        }
        const endExclusive = address + sizeBytes;
        if (!Number.isFinite(endExclusive) || endExclusive > 0x1_0000_0000) {
            return false;
        }
        for (const region of this.ramRegions) {
            if (address >= region.start && endExclusive <= region.end) {
                return true;
            }
        }
        return false;
    }

    private isPointerType(typeName: string): boolean {
        return typeName.replace(/\s+/g, ' ').trim().includes('*');
    }

    private formatHex(value: number): string {
        return `0x${(value >>> 0).toString(16)}`;
    }

    private extractTopLevelArrowLhs(expression: string): string | null {
        const accesses = this.listTopLevelMemberAccesses(expression);
        for (let i = accesses.length - 1; i >= 0; i--) {
            if (accesses[i].operator === '->') {
                return accesses[i].lhs;
            }
        }
        return null;
    }

    private findTopLevelArrowIndex(expression: string): number {
        let parenDepth = 0;
        let bracketDepth = 0;
        for (let i = 0; i < expression.length - 1; i++) {
            const ch = expression[i];
            if (ch === '(') {
                parenDepth++;
                continue;
            }
            if (ch === ')') {
                parenDepth = Math.max(0, parenDepth - 1);
                continue;
            }
            if (ch === '[') {
                bracketDepth++;
                continue;
            }
            if (ch === ']') {
                bracketDepth = Math.max(0, bracketDepth - 1);
                continue;
            }
            if (parenDepth === 0 && bracketDepth === 0 && ch === '-' && expression[i + 1] === '>') {
                return i;
            }
        }
        return -1;
    }

    private extractTopLevelPointerMemberAccess(
        expression: string,
    ): { lhs: string; operator: '->' | '.' } | null {
        const accesses = this.listTopLevelMemberAccesses(expression);
        if (accesses.length === 0) { return null; }
        return accesses[accesses.length - 1];
    }

    private listTopLevelMemberAccesses(
        expression: string,
    ): Array<{ lhs: string; operator: '->' | '.' }> {
        let parenDepth = 0;
        let bracketDepth = 0;
        const accesses: Array<{ lhs: string; operator: '->' | '.' }> = [];

        for (let i = 0; i < expression.length; i++) {
            const ch = expression[i];
            if (ch === '(') {
                parenDepth++;
                continue;
            }
            if (ch === ')') {
                parenDepth = Math.max(0, parenDepth - 1);
                continue;
            }
            if (ch === '[') {
                bracketDepth++;
                continue;
            }
            if (ch === ']') {
                bracketDepth = Math.max(0, bracketDepth - 1);
                continue;
            }
            if (parenDepth !== 0 || bracketDepth !== 0) {
                continue;
            }

            if (ch === '-' && expression[i + 1] === '>') {
                const lhs = expression.slice(0, i).trim();
                if (lhs) {
                    accesses.push({ lhs, operator: '->' });
                }
                i++;
                continue;
            }

            if (ch === '.') {
                const lhs = expression.slice(0, i).trim();
                if (lhs) {
                    accesses.push({ lhs, operator: '.' });
                }
            }
        }

        return accesses;
    }

    private async findPointerBaseExpression(expression: string): Promise<string | null> {
        const accesses = this.listTopLevelMemberAccesses(expression);
        if (accesses.length === 0) { return null; }

        // Check from right to left: pick the nearest member-access lhs that is
        // actually a pointer type (e.g. "gimbal.yaw_.driver_" -> "gimbal.yaw_").
        for (let i = accesses.length - 1; i >= 0; i--) {
            const lhs = accesses[i].lhs;
            const typeName = await this.gdb.resolveType(lhs);
            if (!typeName) { continue; }
            if (this.isPointerType(typeName)) {
                return lhs;
            }
        }
        return null;
    }

    private capturePointerRootRelativeAddresses(node: WatchNode): void {
        if (!node.pointerDeref) {
            if (this.isPointerRootOffsetAddress(node.address)) {
                node.relativeAddress = node.address;
            } else if (node.relativeAddress === undefined) {
                node.relativeAddress = undefined;
            }
        }
        for (const child of node.children) {
            if (child.pointerDeref) { continue; }
            this.capturePointerRootRelativeAddresses(child);
        }
    }

    private isPointerRootOffsetAddress(address: number): boolean {
        return address >= 0 && address < LiveWatchService.MIN_VALID_ADDR;
    }

    private rebasePointerRootAddresses(node: WatchNode, baseAddress: number): void {
        if (!node.pointerDeref && node.relativeAddress !== undefined) {
            node.address = baseAddress + node.relativeAddress;
        }
        for (const child of node.children) {
            if (child.pointerDeref) { continue; }
            this.rebasePointerRootAddresses(child, baseAddress);
        }
    }

    private async hydratePointerDerefLayouts(root: WatchNode): Promise<void> {
        await this.hydratePointerDerefLayoutsRecursive(root);
    }

    private async hydratePointerDerefLayoutsRecursive(node: WatchNode): Promise<void> {
        for (const child of node.children) {
            if (child.pointerDeref && child.pointerCompositePending) {
                const depth = child.pointerDerefDepth ?? 1;
                if (depth <= LiveWatchService.MAX_AUTO_POINTER_DEREF_DEPTH) {
                    const resolved = await this.gdb.resolveTypeLayout(child.typeName, child.name, child.expression, depth);
                    if (resolved) {
                        child.typeName = resolved.typeName;
                        child.size = resolved.size;
                        child.children = resolved.children;
                        child.pointerCompositePending = false;
                        child.value = '';
                        child.rawValue = 0;
                        child.hasRawValue = false;
                        child.changed = false;
                        this.captureRelativeAddresses(child);
                    } else {
                        child.pointerCompositePending = false;
                    }
                } else {
                    child.pointerCompositePending = false;
                }
            }
            await this.hydratePointerDerefLayoutsRecursive(child);
        }
    }

    private captureRelativeAddresses(node: WatchNode): void {
        if (this.isStaticSyntheticNode(node)) {
            node.relativeAddress = undefined;
        } else {
            node.relativeAddress = node.address;
        }
        for (const child of node.children) {
            this.captureRelativeAddresses(child);
        }
    }

    private rebasePointerTargetAddresses(
        node: WatchNode,
        baseAddress: number,
        allowPointerDerefNode: boolean = false,
    ): void {
        if ((!node.pointerDeref || allowPointerDerefNode) && node.relativeAddress !== undefined) {
            node.address = baseAddress + node.relativeAddress;
        }
        for (const child of node.children) {
            if (child.pointerDeref) {
                // Nested pointer-deref branches use their own live pointer value.
                continue;
            }
            this.rebasePointerTargetAddresses(child, baseAddress, false);
        }
    }

    private clearPointerTargetValues(node: WatchNode): boolean {
        let changed = false;
        if (!this.isStaticSyntheticNode(node)) {
            const nodeChanged = node.address !== 0 || node.value !== '' || node.hasRawValue;
            if (nodeChanged) {
                changed = true;
            }
            node.address = 0;
            node.value = '';
            node.rawValue = 0;
            node.hasRawValue = false;
            node.changed = nodeChanged;
        } else {
            node.changed = false;
        }

        for (const child of node.children) {
            if (this.clearPointerTargetValues(child)) {
                changed = true;
            }
        }
        return changed;
    }

    private isStaticSyntheticNode(node: WatchNode): boolean {
        return node.size === 0 && node.expression.includes('::') && !node.pointerDeref;
    }

    removeWatch(nodeId: string): void {
        const root = this.watchList.find(node => node.id === nodeId);
        if (root) {
            this.clearReadBackoffForSubtree(root);
            this.watchList = this.watchList.filter(node => node !== root);
            this.emitTree();
            this.saveWatchList();
        }
    }

    clearWatches(): Promise<void> {
        return this.symbolOperation(async () => {
            this.savedVariables = [];
            this.watchList = [];
            this.revision++;
            this.stopPolling();
            this.readFailureBackoff.clear();
            this.saveWatchList();
            this.emitTree();
        });
    }

    clearAll(): void {
        for (const root of this.watchList) {
            this.clearReadBackoffForSubtree(root);
        }
        this.watchList = [];
        this.emitTree();
        this.saveWatchList();
    }

    toggleExpand(nodeId: string): void {
        const node = this.findNodeInList(nodeId);
        if (node && node.children.length > 0) {
            node.expanded = !node.expanded;
            this.emitTree();
        }
    }

    setDisplayFormat(nodeId: string, format: DisplayFormat): void {
        const node = this.findNodeInList(nodeId);
        if (node) {
            node.displayFormat = format;
            // Re-format only when we have a trustworthy raw value.
            if (!node.hasRawValue) {
                const inferred = this.inferRawValueFromDisplay(node);
                if (inferred !== null) {
                    node.rawValue = inferred;
                    node.hasRawValue = true;
                }
            }
            if (node.hasRawValue) {
                node.value = formatValue(node, node.rawValue);
            }
            this.emitTree();
        }
    }

    private inferRawValueFromDisplay(node: WatchNode): number | null {
        const current = (node.value || '').trim();
        if (!current) { return null; }

        // Enum-like display: "Type::Name (181)"
        const inParen = current.match(/\((-?\d+)\)\s*$/);
        if (inParen) {
            const v = parseInt(inParen[1], 10);
            if (!Number.isNaN(v)) {
                return v >>> 0;
            }
        }

        return parseWriteValue(current, node.typeName);
    }

    async writeValue(nodeId: string, newValueStr: string): Promise<void> {
        const node = this.findNodeInList(nodeId);
        if (!node || node.error || node.children.length || ![1, 2, 4].includes(node.size) ||
            !this.isLikelyRamRange(node.address, node.size) || this.connectionState !== 'connected' ||
            this.findRootForId(nodeId)?.error || !node.hasRawValue) {
            this.emit({ type: 'error', message: 'Cannot write: node not found or no address' });
            return;
        }

        const rawValue = parseWriteValue(newValueStr, node.typeName, node.size);
        if (rawValue === null) {
            this.emit({ type: 'error', message: `Invalid value: "${newValueStr}" for type ${node.typeName}` });
            return;
        }

        try {
            if (node.size <= 1) {
                await this.ocd.writeMemory8(node.address, rawValue);
            } else if (node.size <= 2) {
                await this.ocd.writeMemory16(node.address, rawValue);
            } else {
                await this.ocd.writeMemory32(node.address, rawValue);
            }
        } catch (err: any) {
            this.emit({ type: 'error', errorCode: 'write_failed', message: `Write failed: ${err.message}` });
        }
    }

    refreshSymbols(force = true): Promise<void> {
        return this.symbolOperation(() => this.reloadSymbols(force));
    }

    private symbolOperation(run: () => Promise<void>): Promise<void> {
        const operation = this.symbols.then(run);
        this.symbols = operation.catch(() => {});
        return operation;
    }

    // ── Polling ────────────────────────────────────────────────

    private startPolling(): void {
        this.schedulePoll(false);
    }

    getPollInterval(): number {
        return this.pollInterval;
    }

    setPollInterval(interval: unknown): void {
        if (typeof interval !== 'number' || interval === this.pollInterval ||
            !POLL_INTERVALS.some(value => value === interval)) return;
        this.pollInterval = interval;
        this.stopPolling();
        this.startPolling();
    }

    private schedulePoll(idle: boolean): void {
        if (this.cancelPoll || this.polling || !this.visible || this.paused || this.disposed ||
            !this.watchList.length || this.connectionState !== 'connected') return;
        const run = () => { this.cancelPoll = null; void this.pollOnce(); };
        const delay = Math.max(idle ? 120 : 0, this.lastPollStarted + this.pollInterval - performance.now());
        if (delay > 0) { const timer = setTimeout(run, Math.ceil(delay)); this.cancelPoll = () => clearTimeout(timer); }
        else { const timer = setImmediate(run); this.cancelPoll = () => clearImmediate(timer); }
    }

    private stopPolling(): void {
        this.cancelPoll?.();
        this.cancelPoll = null;
    }

    togglePause(): void {
        this.paused = !this.paused;
        if (this.paused) this.stopPolling(); else this.startPolling();
        this.emit({ type: 'pauseChange', paused: this.paused });
    }

    isPaused(): boolean {
        return this.paused;
    }

    private async pollOnce(): Promise<void> {
        if (this.paused || this.polling || this.disposed) { return; }
        if (this.connectionState !== 'connected') { return; }

        this.polling = true;
        this.lastPollStarted = performance.now();
        const generation = this.revision;
        const operations = this.ioCount;
        try {
            let anyChanged = false;
            for (const root of this.watchList) {
                if (generation !== this.revision || !this.visible || this.paused) break;
                const originalAddress = root.address;
                if (root.error) {
                    const recovered = await this.tryRecoverWatch(root);
                    if (!recovered) { continue; }
                    anyChanged = true;
                } else if (!await this.refreshPointerRootAddress(root)) {
                    anyChanged = true;
                    continue;
                }
                const changed = await this.readNodeValues(root);
                if (changed || root.address !== originalAddress) anyChanged = true;
            }
            if (anyChanged && generation === this.revision) {
                this.emitTree();
            }
        } catch (err: any) {
            if (generation !== this.revision) return;
            const message = err instanceof Error ? err.message : String(err);
            const isConnectionIssue =
                /not connected|connection|econnrefused|timeout|closed/i.test(message);

            if (isConnectionIssue) {
                // Connection lost — mark disconnected so user can reconnect
                this.disconnect();
                this.scheduleReconnect();
                return;
            }

            this.emit({ type: 'error', errorCode: 'read_failed', message: `Memory read failed: ${message}` });
        } finally {
            this.polling = false;
            this.schedulePoll(operations === this.ioCount);
        }
    }

    /**
     * Read memory for a node and all its children.
     * For struct nodes, read the entire block at once for efficiency.
     * Returns true if any value changed.
     * Throws on connection errors so pollOnce can detect disconnect.
     */
    private async readNodeValues(node: WatchNode): Promise<boolean> {
        if (node.address === undefined || node.address === null || node.size === 0) { return false; }
        if (node.address <= 0) { return false; }
        if (!this.isLikelyRamRange(node.address, Math.max(node.size, 1))) {
            return false;
        }

        const words = await this.readMemory32WithBackoff(node, node.address, node.size);
        if (!words || words.length === 0) { return false; }

        const changedDirect = this.applyValues(node, words, node.address);
        const changedDeref = await this.updatePointerDerefValues(node);
        return changedDirect || changedDeref;
    }

    private async readMemory32WithBackoff(
        node: WatchNode,
        address: number,
        size: number,
    ): Promise<number[] | null> {
        if (size > 65536 || !this.isLikelyRamRange(address, size)) return null;
        const now = Date.now();
        const prev = this.readFailureBackoff.get(node.id);
        if (prev && prev.address === address && now < prev.nextRetryAt) {
            return null;
        }

        try {
            // `cortex-debug` uses a dedicated live-monitor GDB session for background reads.
            // On single-connection OpenOCD setups that session is rejected, so live polling
            // must stay on the quiet TCL path instead of opening another GDB client.
            this.ioCount++;
            const offset = address % 4;
            const alignedAddress = address - offset;
            const count = Math.ceil((offset + size) / 4);
            let words: number[];
            if (this.isLikelyRamRange(alignedAddress, count * 4)) {
                const aligned = await this.ocd.readMemory32(alignedAddress, count);
                const shift = offset * 8;
                words = shift === 0 ? aligned : aligned.slice(0, Math.ceil(size / 4)).map((word, index) =>
                    ((word >>> shift) | (aligned[index + 1] << (32 - shift))) >>> 0);
            } else {
                const bytes = await this.ocd.readMemory8(address, size);
                words = Array.from({ length: Math.ceil(size / 4) }, (_, word) => {
                    const i = word * 4;
                    return (bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24)) >>> 0;
                });
            }
            this.readFailureBackoff.delete(node.id);
            return words;
        } catch (err: any) {
            if (!this.isMemoryReadFailureError(err)) {
                throw err;
            }
            const failures = prev && prev.address === address ? Math.min(prev.failures + 1, 8) : 1;
            const delay = Math.min(
                LiveWatchService.READ_FAILURE_MAX_BACKOFF_MS,
                LiveWatchService.READ_FAILURE_BASE_BACKOFF_MS * (1 << (failures - 1)),
            );
            this.readFailureBackoff.set(node.id, {
                address,
                failures,
                nextRetryAt: now + delay,
            });
            return null;
        }
    }

    private isMemoryReadFailureError(err: unknown): boolean {
        const message = err instanceof Error ? err.message : String(err);
        return /failed to read memory|incomplete openocd read response|unexpected openocd read response|remote communication error|target disconnected/i.test(message);
    }

    private async tryRecoverWatch(root: WatchNode): Promise<boolean> {
        if (root.pointerBaseExpr === null || (this.pointerRetryAt.get(root) ?? 0) > Date.now() ||
            this.listTopLevelMemberAccesses(root.expression).length === 0) return false;
        try {
            if (!await this.resolveRuntimePointerChainAddress(root)) return false;
            root.error = undefined;
            return true;
        } catch (err: any) {
            root.error = err instanceof Error ? err.message : String(err);
            this.pointerRetryAt.set(root, Date.now() + (root.pointerStorageAddress ? 120 : 1000));
            return false;
        }
    }

    private async refreshPointerRootAddress(root: WatchNode): Promise<boolean> {
        if (root.pointerBaseExpr === null) {
            return true;
        }
        try {
            await this.resolveRuntimePointerChainAddress(root);
            root.error = undefined;
            return true;
        } catch (err: any) {
            root.error = err instanceof Error ? err.message : String(err);
            return false;
        }
    }

    /**
     * Recursively apply read values to a node tree.
     * `words` is the full memory block, `blockBase` is the starting address.
     */
    private applyValues(node: WatchNode, words: number[], blockBase: number): boolean {
        let anyChanged = false;

        if (node.children.length === 0) {
            if (node.size <= 0 || node.address <= 0) {
                node.changed = false;
                node.hasRawValue = false;
                return false;
            }
            // Leaf node — extract value from the word array
            const byteOffset = node.address - blockBase;
            const wordIndex = Math.floor(byteOffset / 4);
            if (wordIndex >= 0 && wordIndex < words.length) {
                const shift = (byteOffset % 4) * 8;
                let rawWord = shift ? ((words[wordIndex] >>> shift) | (words[wordIndex + 1] << (32 - shift))) >>> 0 : words[wordIndex];
                if (node.size === 1) {
                    rawWord &= 0xFF;
                } else if (node.size === 2) {
                    rawWord &= 0xFFFF;
                }

                const prevRaw = node.rawValue;
                node.rawValue = rawWord;
                node.hasRawValue = true;
                const newValue = formatValue(node, rawWord);

                if (node.value !== newValue) {
                    node.changed = true;
                    anyChanged = true;
                } else {
                    node.changed = false;
                }
                node.value = newValue;
            }
        } else {
            // Pointer parent with synthetic deref child should still show its own pointer value.
            const isPointerParent = node.typeName.includes('*');
            if (isPointerParent && node.size > 0 && node.address > 0) {
                const byteOffset = node.address - blockBase;
                const wordIndex = Math.floor(byteOffset / 4);
                if (wordIndex >= 0 && wordIndex < words.length) {
                    const rawWord = words[wordIndex] >>> 0;
                    node.rawValue = rawWord;
                    node.hasRawValue = true;
                    const newValue = formatValue(node, rawWord);
                    if (node.value !== newValue) {
                        node.changed = true;
                        anyChanged = true;
                    } else {
                        node.changed = false;
                    }
                    node.value = newValue;
                }
            } else {
                node.changed = false;
            }

            // Parent node — recurse into regular children
            for (const child of node.children) {
                if (child.pointerDeref) { continue; }
                const childChanged = this.applyValues(child, words, blockBase);
                if (childChanged) { anyChanged = true; }
            }
        }

        return anyChanged;
    }

    private async updatePointerDerefValues(node: WatchNode): Promise<boolean> {
        let anyChanged = false;

        const derefChildren = node.children.filter(c => c.pointerDeref);
        if (derefChildren.length > 0) {
            const pointerValue = node.rawValue >>> 0;
            for (const deref of derefChildren) {
                const changed = await this.readPointerTargetValue(deref, pointerValue);
                if (changed) { anyChanged = true; }
            }
        }

        for (const child of node.children) {
            if (child.pointerDeref) { continue; }
            const changed = await this.updatePointerDerefValues(child);
            if (changed) { anyChanged = true; }
        }

        return anyChanged;
    }

    private async readPointerTargetValue(targetNode: WatchNode, pointerValue: number): Promise<boolean> {
        const prevValue = targetNode.value;
        const prevAddress = targetNode.address;
        const targetSpanBytes = Math.max(targetNode.size, 4);

        if (!this.isLikelyRamRange(pointerValue, targetSpanBytes)) {
            return this.clearPointerTargetValues(targetNode);
        }

        // Composite deref node: rebase full subtree, read one contiguous block,
        // then recurse into nested pointer deref children.
        if (targetNode.children.length > 0) {
            if (targetNode.relativeAddress === undefined) {
                this.captureRelativeAddresses(targetNode);
            }
            this.rebasePointerTargetAddresses(targetNode, pointerValue, true);

            if (targetNode.size <= 0) {
                targetNode.changed = targetNode.address !== prevAddress;
                return targetNode.changed;
            }

            const words = await this.readMemory32WithBackoff(targetNode, pointerValue, targetNode.size);
            if (!words || words.length === 0) {
                return false;
            }

            const changedDirect = this.applyValues(targetNode, words, pointerValue);
            const changedDeref = await this.updatePointerDerefValues(targetNode);
            targetNode.changed = changedDirect || changedDeref || targetNode.address !== prevAddress;
            return targetNode.changed;
        }

        if (targetNode.size <= 0) {
            return this.clearPointerTargetValues(targetNode);
        }

        const words = await this.readMemory32WithBackoff(targetNode, pointerValue, targetNode.size);
        if (!words || words.length === 0) {
            return false;
        }

        let rawWord = words[0] >>> 0;
        if (targetNode.size === 1) {
            rawWord = rawWord & 0xFF;
        } else if (targetNode.size === 2) {
            rawWord = rawWord & 0xFFFF;
        }

        targetNode.address = pointerValue;
        targetNode.rawValue = rawWord;
        targetNode.hasRawValue = true;
        const newValue = formatValue(targetNode, rawWord);
        targetNode.changed = newValue !== prevValue || targetNode.address !== prevAddress || prevValue === '';
        targetNode.value = newValue;
        return targetNode.changed;
    }

    // ── Node lookup helpers ────────────────────────────────────

    private findNodeInList(nodeId: string): WatchNode | null {
        for (const root of this.watchList) {
            const found = this.findNode(root, nodeId);
            if (found) { return found; }
        }
        return null;
    }

    private findNode(node: WatchNode, nodeId: string): WatchNode | null {
        if (node.id === nodeId) { return node; }
        for (const child of node.children) {
            const found = this.findNode(child, nodeId);
            if (found) { return found; }
        }
        return null;
    }

    private findRootForId(nodeId: string): WatchNode | null {
        for (const root of this.watchList) {
            if (root.id === nodeId || this.findNode(root, nodeId) !== null) {
                return root;
            }
        }
        return null;
    }

    private clearReadBackoffForSubtree(node: WatchNode): void {
        this.readFailureBackoff.delete(node.id);
        for (const child of node.children) {
            this.clearReadBackoffForSubtree(child);
        }
    }

    // ── Persistence ────────────────────────────────────────────

    private saveWatchList(): void {
        const names = this.watchList.map(n => n.name);
        void this.context.workspaceState.update('stm32ProjectHelper.liveWatch.variables', names);
    }

    private configureRamRegions(config: vscode.WorkspaceConfiguration, emitWarning: boolean): void {
        const rawEntries = config.get<unknown[]>('ramRegions', []);
        const { regions, invalidEntries } = this.parseRamRegions(Array.isArray(rawEntries) ? rawEntries : []);
        this.ramRegions = regions;

        if (emitWarning && invalidEntries.length > 0) {
            this.emit({
                type: 'error',
                errorCode: 'generic',
                message: `Ignored invalid stm32ProjectHelper.liveWatch.ramRegions entries: ${invalidEntries.join(', ')}. Use "0x20000000-0x20020000".`,
            });
        }
    }

    private parseRamRegions(rawEntries: readonly unknown[]): {
        regions: Array<{ start: number; end: number }>;
        invalidEntries: string[];
    } {
        const regions: Array<{ start: number; end: number }> = [];
        const invalidEntries: string[] = [];

        for (const rawEntry of rawEntries) {
            if (typeof rawEntry !== 'string') {
                invalidEntries.push(String(rawEntry));
                continue;
            }

            const entry = rawEntry.trim();
            if (!entry) { continue; }

            const match = entry.match(/^(0x[0-9a-fA-F]+|\d+)\s*(?:-|:|\.\.)\s*(0x[0-9a-fA-F]+|\d+)$/);
            if (!match) {
                invalidEntries.push(entry);
                continue;
            }

            const start = this.parseAddressLiteral(match[1]);
            const end = this.parseAddressLiteral(match[2]);
            if (start === null || end === null || start >= end) {
                invalidEntries.push(entry);
                continue;
            }

            regions.push({ start, end: end + 1 });
        }

        if (regions.length === 0) {
            return {
                regions: [...LiveWatchService.DEFAULT_RAM_REGIONS],
                invalidEntries,
            };
        }

        regions.sort((a, b) => a.start - b.start);
        return { regions, invalidEntries };
    }

    private parseAddressLiteral(value: string): number | null {
        const parsed = value.startsWith('0x') || value.startsWith('0X')
            ? parseInt(value, 16)
            : parseInt(value, 10);
        return Number.isFinite(parsed) ? (parsed >>> 0) : null;
    }

    // ── Current state ──────────────────────────────────────────

    getRows(): ViewRow[] {
        return flattenTree(this.watchList);
    }

    private scheduleReconnect(): void {
        if (this.disposed || !this.autoConnect) return;
        if (this.connectionState === 'connected' || this.connectionState === 'connecting') { return; }
        if (this.reconnectTimer || this.reconnectInFlight) { return; }

        const span = LiveWatchService.RECONNECT_MAX_MS - LiveWatchService.RECONNECT_MIN_MS + 1;
        const delay = LiveWatchService.RECONNECT_MIN_MS + Math.floor(Math.random() * span);

        this.reconnectTimer = setTimeout(async () => {
            this.reconnectTimer = null;
            if (this.disposed || this.connectionState === 'connected' || this.connectionState === 'connecting') {
                return;
            }

            this.reconnectInFlight = true;
            try {
                await this.tryConnect(true);
            } finally {
                this.reconnectInFlight = false;
                if (!this.disposed && this.connectionState === 'disconnected') {
                    this.scheduleReconnect();
                }
            }
        }, this.visible ? delay : delay * 3);
    }

    private stopReconnectLoop(): void {
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
    }

    // ── Dispose ────────────────────────────────────────────────

    dispose(): void {
        this.disposed = true;
        this.connecting++;
        this.revision++;
        this.stopReconnectLoop();
        this.stopPolling();
        this.ocd.disconnect();
        for (const d of this.disposables) { d.dispose(); }
    }
}
