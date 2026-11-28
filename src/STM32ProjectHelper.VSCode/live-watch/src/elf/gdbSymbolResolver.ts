import { spawn } from 'node:child_process';
import { WatchNode, createWatchNode } from '../model/watchNode';

type GdbResolveFailureCode =
    | 'symbol_not_found'
    | 'expression_invalid'
    | 'parse_failed'
    | 'gdb_failed';

class GdbResolveFailure extends Error {
    readonly code: GdbResolveFailureCode;

    constructor(code: GdbResolveFailureCode, message: string) {
        super(message);
        this.code = code;
        this.name = 'GdbResolveFailure';
    }
}

interface MiCommandResult {
    cls: string;
    message: string;
    value: string;
    console: string;
    log: string;
    target: string;
    rawResult: string;
}

interface MiChildInfo {
    name: string;
    exp: string;
    typeName: string;
    numChild: number;
    value: string;
}

interface MiNodeInfo {
    typeName: string;
    numChild: number;
    value: string;
    address: number;
    size: number;
    children: MiChildInfo[];
}

/**
 * Resolves variable addresses and struct layouts by invoking
 * `arm-none-eabi-gdb --interpreter=mi2` against an ELF file with DWARF debug info.
 * MI command status allows us to distinguish:
 *   - symbol not found
 *   - invalid expression
 *   - parser/internal failures
 */
export class GdbSymbolResolver {
    private gdbPath: string;
    private elfPath: string;
    private enumValueCache = new Map<string, Record<string, string> | null>();
    private static readonly MAX_AUTO_POINTER_DEREF_DEPTH = 3;
    private static readonly GDB_TIMEOUT_MS = 10000;
    private static readonly GDB_MAX_OUTPUT_BYTES = 1024 * 1024;

    constructor(gdbPath: string, elfPath: string) {
        this.gdbPath = gdbPath;
        this.elfPath = elfPath;
    }

    updateConfig(gdbPath: string, elfPath: string): void {
        this.gdbPath = gdbPath;
        this.elfPath = elfPath;
        this.enumValueCache.clear();
    }

    /**
     * Resolve a variable name to a complete WatchNode tree.
     * Primary path uses MI var-objects (quiet + structured), avoiding text parsing
     * for print/whatis/ptype in normal watch resolution.
     */
    async resolve(variableName: string): Promise<WatchNode> {
        this.assertExpressionIsSafe(variableName);
        try {
            const node = await this.buildNodeViaMi(variableName, variableName, true, 0);
            await this.attachEnumValueNames(node);
            return node;
        } catch (err: any) {
            if (err instanceof GdbResolveFailure) {
                if (err.code === 'symbol_not_found') {
                    const node = createWatchNode(variableName, variableName, '???', 0, 0, true);
                    node.error = 'Symbol not found';
                    return node;
                }
                if (err.code === 'expression_invalid') {
                    const node = createWatchNode(variableName, variableName, '???', 0, 0, true);
                    node.error = err.message;
                    return node;
                }
            }
            throw err;
        }
    }

    /**
     * Resolve only the address (for quick re-resolve after reflash).
     */
    async resolveAddress(variableName: string): Promise<number | null> {
        this.assertExpressionIsSafe(variableName);
        const [res] = await this.runGdbMi([
            this.miEvaluateExpression(`&(${variableName})`),
        ]);
        if (res.cls === 'error') {
            if (this.isSymbolMissingMessage(res.message)) {
                return null;
            }
            throw this.classifyGdbFailure(res.message, variableName);
        }
        const addr = this.parseAddressFromMiValue(res.value);
        if (addr === null) {
            throw new GdbResolveFailure(
                'parse_failed',
                `Failed to parse address from GDB response for "${variableName}"`,
            );
        }
        return addr;
    }

    /**
     * Resolve the compact type name for an expression using `whatis`.
     * Unlike `ptype /o`, this preserves pointer suffixes on top-level expressions.
     */
    async resolveType(variableName: string): Promise<string | null> {
        this.assertExpressionIsSafe(variableName);
        const [res] = await this.runGdbMi([
            this.miConsoleCommand(`whatis (${variableName})`),
        ]);
        if (res.cls === 'error') {
            if (this.isSymbolMissingMessage(res.message)) {
                return null;
            }
            throw this.classifyGdbFailure(res.message, variableName);
        }
        const parsed = this.parseWhatisTypeFromConsole(res.console);
        if (!parsed) {
            throw new GdbResolveFailure(
                'parse_failed',
                `Failed to parse "whatis" output for "${variableName}"`,
            );
        }
        return parsed;
    }

    /**
     * Resolve a composite type layout (class/struct/union) and attach it to
     * the provided expression. Addresses in the returned tree are offsets from 0.
     */
    async resolveTypeLayout(
        typeName: string,
        nodeName: string,
        expression: string,
        pointerDepth: number,
    ): Promise<WatchNode | null> {
        for (const candidate of this.buildCompositeTypeCandidates(typeName)) {
            try {
                const [res] = await this.runGdbMi([
                    this.miConsoleCommand(`ptype /o ${candidate}`),
                ]);
                if (res.cls === 'error') {
                    if (this.isSymbolMissingMessage(res.message) || this.isExpressionInvalidMessage(res.message)) {
                        continue;
                    }
                    throw this.classifyGdbFailure(res.message, candidate);
                }
                const ptypeOutput = this.extractPtypeOutput(res.console);
                if (!ptypeOutput) { continue; }
                const root = this.parsePtypeO(ptypeOutput, nodeName, expression, 0, false, pointerDepth);
                await this.attachEnumValueNames(root);
                await this.attachStaticMemberValues(root);
                return root;
            } catch {
                // Try next candidate spelling.
            }
        }
        return null;
    }

    private async buildNodeViaMi(
        expression: string,
        nodeName: string,
        isRoot: boolean,
        pointerDepth: number,
    ): Promise<WatchNode> {
        const info = await this.queryNodeViaMi(expression);
        const node = createWatchNode(
            nodeName,
            expression,
            info.typeName || 'unknown',
            info.address,
            info.size,
            isRoot,
        );

        // Preserve value for static-like nodes that do not have a readable address.
        if (node.address <= 0 && info.value && info.value !== '{...}' && info.value !== '[...]') {
            node.value = info.value;
        }

        // Keep pointer fields as pointer nodes; defer one-level dereference to runtime
        // so polling logic can rebase by live pointer values.
        const nextPointerDepth = pointerDepth + 1;
        if (this.shouldCreatePointerDerefChild(node.typeName, nextPointerDepth)) {
            const pointeeType = this.normalizePointeeType(node.typeName);
            const isScalarPointee = this.isScalarPointeeType(pointeeType);
            const derefNode = createWatchNode(
                `*${node.name}`,
                this.composePointerDerefExpr(expression),
                pointeeType,
                0,
                isScalarPointee ? this.guessSize(pointeeType) : 0,
                false,
            );
            derefNode.pointerDeref = true;
            derefNode.pointerDerefDepth = nextPointerDepth;
            derefNode.pointerCompositePending = !isScalarPointee;
            derefNode.relativeAddress = 0;
            node.children.push(derefNode);
            return node;
        }

        if (info.numChild <= 0 || info.children.length === 0) {
            return node;
        }

        const childRuntimeInfo = await this.queryChildAddressSizeViaMi(expression, info.children);

        for (const childInfo of info.children) {
            const childExpr = this.composeChildExpr(expression, childInfo.exp);
            const childName = this.displayNameFromMiChildExp(childInfo.exp);
            const runtime = childRuntimeInfo.get(childInfo.exp);
            const childAddr = runtime?.address ?? 0;
            const childSize = runtime?.size ?? this.guessSize(childInfo.typeName);

            if (childInfo.numChild <= 0 || this.isPointerType(childInfo.typeName)) {
                const childNode = createWatchNode(
                    childName,
                    childExpr,
                    childInfo.typeName || 'unknown',
                    childAddr,
                    childSize,
                    false,
                );
                if (childAddr <= 0 && childInfo.value && childInfo.value !== '{...}' && childInfo.value !== '[...]') {
                    childNode.value = childInfo.value;
                }

                const childNextPointerDepth = pointerDepth + 1;
                if (this.shouldCreatePointerDerefChild(childNode.typeName, childNextPointerDepth)) {
                    const pointeeType = this.normalizePointeeType(childNode.typeName);
                    const isScalarPointee = this.isScalarPointeeType(pointeeType);
                    const derefNode = createWatchNode(
                        `*${childNode.name}`,
                        this.composePointerDerefExpr(childExpr),
                        pointeeType,
                        0,
                        isScalarPointee ? this.guessSize(pointeeType) : 0,
                        false,
                    );
                    derefNode.pointerDeref = true;
                    derefNode.pointerDerefDepth = childNextPointerDepth;
                    derefNode.pointerCompositePending = !isScalarPointee;
                    derefNode.relativeAddress = 0;
                    childNode.children.push(derefNode);
                }
                node.children.push(childNode);
                continue;
            }

            try {
                const childNode = await this.buildNodeViaMi(
                    childExpr,
                    childName,
                    false,
                    pointerDepth,
                );
                node.children.push(childNode);
            } catch (err: any) {
                // Keep partial tree; mark problematic child as error node.
                const fallback = createWatchNode(
                    childName,
                    childExpr,
                    childInfo.typeName || 'unknown',
                    0,
                    0,
                    false,
                );
                fallback.error = err instanceof Error ? err.message : String(err);
                node.children.push(fallback);
            }
        }

        // Recompute composite size from child range when available.
        const inferred = this.inferCompositeSize(node);
        if (inferred > 0) {
            node.size = inferred;
        }
        return node;
    }

    private async queryNodeViaMi(expression: string): Promise<MiNodeInfo> {
        const varName = 'v0';
        const [createRes, listRes, addrRes, sizeRes] = await this.runGdbMi([
            `-var-create ${varName} @ ${this.quoteMiString(expression)}`,
            `-var-list-children --all-values ${this.quoteMiString(varName)}`,
            this.miEvaluateExpression(`&(${expression})`),
            this.miEvaluateExpression(`sizeof(${expression})`),
            `-var-delete ${varName}`,
        ]);

        if (createRes.cls === 'error') {
            throw await this.classifyVarCreateFailure(createRes.message, expression);
        }

        const typeName = this.normalizeTypeName(this.getMiField(createRes.rawResult, 'type') ?? 'unknown');
        const numChild = this.parseIntField(this.getMiField(createRes.rawResult, 'numchild')) ?? 0;
        const value = this.getMiField(createRes.rawResult, 'value') ?? '';
        const children = listRes.cls === 'done'
            ? this.parseMiChildren(listRes.rawResult)
            : [];
        const addr = addrRes.cls === 'done'
            ? (this.parseAddressFromMiValue(addrRes.value) ?? 0)
            : 0;
        const parsedSize = sizeRes.cls === 'done'
            ? this.parseIntegerFromMiValue(sizeRes.value)
            : null;
        const size = parsedSize !== null ? Math.max(0, parsedSize) : this.guessSize(typeName);

        return {
            typeName,
            numChild,
            value,
            address: addr,
            size,
            children,
        };
    }

    private parseMiChildren(rawResult: string): MiChildInfo[] {
        const start = rawResult.indexOf('children=[');
        if (start < 0) { return []; }
        const openIdx = rawResult.indexOf('[', start);
        if (openIdx < 0) { return []; }

        const endIdx = this.findMatchingBracket(rawResult, openIdx, '[', ']');
        if (endIdx < 0) { return []; }
        const body = rawResult.slice(openIdx + 1, endIdx);
        const children: MiChildInfo[] = [];

        let i = 0;
        while (i < body.length) {
            const childStart = body.indexOf('child={', i);
            if (childStart < 0) { break; }
            const braceOpen = body.indexOf('{', childStart);
            if (braceOpen < 0) { break; }
            const braceClose = this.findMatchingBracket(body, braceOpen, '{', '}');
            if (braceClose < 0) { break; }

            const fragment = body.slice(braceOpen + 1, braceClose);
            const name = this.getMiField(fragment, 'name') ?? '';
            const exp = this.getMiField(fragment, 'exp') ?? '';
            const typeName = this.normalizeTypeName(this.getMiField(fragment, 'type') ?? 'unknown');
            const numChild = this.parseIntField(this.getMiField(fragment, 'numchild')) ?? 0;
            const value = this.getMiField(fragment, 'value') ?? '';

            if (name && exp) {
                children.push({ name, exp, typeName, numChild, value });
            }
            i = braceClose + 1;
        }

        return children;
    }

    private async queryChildAddressSizeViaMi(
        parentExpr: string,
        children: MiChildInfo[],
    ): Promise<Map<string, { address: number; size: number }>> {
        const infoByExp = new Map<string, { address: number; size: number }>();
        if (children.length === 0) {
            return infoByExp;
        }

        const childExprs = children.map(ch => this.composeChildExpr(parentExpr, ch.exp));
        const commands: string[] = [];
        for (const expr of childExprs) {
            commands.push(this.miEvaluateExpression(`&(${expr})`));
            commands.push(this.miEvaluateExpression(`sizeof(${expr})`));
        }

        const results = await this.runGdbMi(commands);
        for (let i = 0; i < children.length; i++) {
            const addrRes = results[i * 2];
            const sizeRes = results[i * 2 + 1];
            const address = addrRes && addrRes.cls === 'done'
                ? (this.parseAddressFromMiValue(addrRes.value) ?? 0)
                : 0;
            const parsedSize = sizeRes && sizeRes.cls === 'done'
                ? this.parseIntegerFromMiValue(sizeRes.value)
                : null;
            const size = parsedSize !== null
                ? Math.max(0, parsedSize)
                : this.guessSize(children[i].typeName);
            infoByExp.set(children[i].exp, { address, size });
        }

        return infoByExp;
    }

    private findMatchingBracket(input: string, openIdx: number, openCh: string, closeCh: string): number {
        let depth = 0;
        let inQuote = false;
        let escaped = false;
        for (let i = openIdx; i < input.length; i++) {
            const ch = input[i];
            if (inQuote) {
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if (ch === '\\') {
                    escaped = true;
                    continue;
                }
                if (ch === '"') {
                    inQuote = false;
                }
                continue;
            }
            if (ch === '"') {
                inQuote = true;
                continue;
            }
            if (ch === openCh) {
                depth++;
                continue;
            }
            if (ch === closeCh) {
                depth--;
                if (depth === 0) {
                    return i;
                }
            }
        }
        return -1;
    }

    private composeChildExpr(parentExpr: string, childExp: string): string {
        if (/^\d+$/.test(childExp)) {
            return `(${parentExpr})[${childExp}]`;
        }
        return `(${parentExpr}).${childExp}`;
    }

    private displayNameFromMiChildExp(childExp: string): string {
        if (/^\d+$/.test(childExp)) {
            return `[${childExp}]`;
        }
        return childExp;
    }

    private inferCompositeSize(node: WatchNode): number {
        if (node.address <= 0 || node.children.length === 0) {
            return node.size;
        }
        let maxEnd = node.address;
        for (const child of node.children) {
            if (child.address <= 0 || child.size <= 0) { continue; }
            const end = child.address + child.size;
            if (end > maxEnd) {
                maxEnd = end;
            }
        }
        if (maxEnd <= node.address) {
            return node.size;
        }
        return maxEnd - node.address;
    }

    // ── GDB process execution ──────────────────────────────────

    private runGdbMi(commands: string[]): Promise<MiCommandResult[]> {
        return new Promise((resolve, reject) => {
            const args = ['--interpreter=mi2', '-q', '-nx', '-iex', 'set auto-load off'];
            if (this.elfPath) {
                args.push(this.elfPath);
            }

            const child = spawn(this.gdbPath, args, {
                windowsHide: true,
                stdio: ['pipe', 'pipe', 'pipe'],
            });

            let stdout = '';
            let stderr = '';
            const timeout = setTimeout(() => {
                child.kill();
                reject(new GdbResolveFailure('gdb_failed', `GDB timed out after ${GdbSymbolResolver.GDB_TIMEOUT_MS} ms`));
            }, GdbSymbolResolver.GDB_TIMEOUT_MS);

            child.stdout.setEncoding('utf8');
            child.stderr.setEncoding('utf8');

            child.stdout.on('data', (chunk: string) => {
                stdout += chunk;
                if (stdout.length > GdbSymbolResolver.GDB_MAX_OUTPUT_BYTES) {
                    clearTimeout(timeout);
                    child.kill();
                    reject(new GdbResolveFailure('gdb_failed', 'GDB output exceeded buffer limit'));
                }
            });
            child.stderr.on('data', (chunk: string) => {
                stderr += chunk;
                if (stderr.length > GdbSymbolResolver.GDB_MAX_OUTPUT_BYTES) {
                    clearTimeout(timeout);
                    child.kill();
                    reject(new GdbResolveFailure('gdb_failed', 'GDB stderr exceeded buffer limit'));
                }
            });

            child.on('error', (err) => {
                clearTimeout(timeout);
                reject(new GdbResolveFailure('gdb_failed', `Failed to launch GDB: ${err.message}`));
            });

            child.on('close', () => {
                clearTimeout(timeout);
                try {
                    const parsed = this.parseMiOutput(stdout, commands.length);
                    resolve(parsed);
                } catch (err: any) {
                    const detail = stderr ? `\n${stderr}` : '';
                    reject(new GdbResolveFailure(
                        'parse_failed',
                        `Failed to parse GDB/MI output: ${err instanceof Error ? err.message : String(err)}${detail}`,
                    ));
                }
            });

            const script = commands.join('\n') + '\n-gdb-exit\n';
            child.stdin.write(script);
            child.stdin.end();
        });
    }

    /**
     * Resolve the actual GDB executable path.
     * If the configured path isn't absolute, search common locations.
     */
    // ── Address parsing ────────────────────────────────────────

    /**
     * Parse the address from `print &variable` output.
     * Examples:
     *   $1 = (PidController *) 0x20001000 <speed_pid>
     *   $1 = (float *) 0x20001000
     */
    private parseAddressFromMiValue(value: string): number | null {
        const addrMatch = value.match(/0x[0-9a-fA-F]+/);
        if (!addrMatch) { return null; }
        const parsed = parseInt(addrMatch[0], 16);
        return Number.isFinite(parsed) ? parsed : null;
    }

    private parseWhatisTypeFromConsole(output: string): string | null {
        const match = output.match(/type\s*=\s*([^\n]+)/);
        if (!match) { return null; }
        return this.normalizeTypeName(match[1]);
    }

    private miEvaluateExpression(expression: string): string {
        return `-data-evaluate-expression ${this.quoteMiString(expression)}`;
    }

    private miConsoleCommand(command: string): string {
        return `-interpreter-exec console ${this.quoteMiString(command)}`;
    }

    private quoteMiString(value: string): string {
        return `"${value
            .replace(/\\/g, '\\\\')
            .replace(/"/g, '\\"')
            .replace(/\n/g, '\\n')
            .replace(/\r/g, '\\r')}"`;
    }

    private parseMiOutput(output: string, commandCount: number): MiCommandResult[] {
        const lines = output.split(/\r?\n/);
        const results: MiCommandResult[] = [];
        const pendingConsole: string[] = [];
        const pendingLog: string[] = [];
        const pendingTarget: string[] = [];

        for (const rawLine of lines) {
            const line = rawLine.trim();
            if (!line) { continue; }
            if (line === '(gdb)') { continue; }

            const prefix = line[0];
            if (prefix === '~' || prefix === '&' || prefix === '@') {
                const decoded = this.decodeMiStreamToken(line.substring(1).trim());
                if (prefix === '~') {
                    pendingConsole.push(decoded);
                } else if (prefix === '&') {
                    pendingLog.push(decoded);
                } else {
                    pendingTarget.push(decoded);
                }
                continue;
            }

            if (prefix === '^') {
                const clsMatch = line.match(/^\^([a-zA-Z-]+)/);
                const cls = clsMatch ? clsMatch[1] : 'unknown';
                results.push({
                    cls,
                    message: this.getMiField(line, 'msg') ?? '',
                    value: this.getMiField(line, 'value') ?? '',
                    console: pendingConsole.join(''),
                    log: pendingLog.join(''),
                    target: pendingTarget.join(''),
                    rawResult: line,
                });
                pendingConsole.length = 0;
                pendingLog.length = 0;
                pendingTarget.length = 0;
            }
        }

        // Last command is always `-gdb-exit`; drop its result.
        const filtered = results.filter(r => r.cls !== 'exit');
        if (filtered.length < commandCount) {
            throw new Error(`expected ${commandCount} results, got ${filtered.length}`);
        }
        return filtered.slice(0, commandCount);
    }

    private getMiField(resultRecord: string, key: string): string | null {
        const fieldRegex = new RegExp(`${key}=\"((?:\\\\.|[^\"\\\\])*)\"`);
        const m = resultRecord.match(fieldRegex);
        if (!m) { return null; }
        return this.decodeMiCString(m[1]);
    }

    private parseIntField(value: string | null): number | null {
        if (value === null || value === '') { return null; }
        const parsed = parseInt(value, 10);
        return Number.isFinite(parsed) ? parsed : null;
    }

    private parseIntegerFromMiValue(value: string): number | null {
        const m = value.match(/-?0x[0-9a-fA-F]+|-?\d+/);
        if (!m) { return null; }
        const token = m[0];
        const parsed = token.startsWith('-0x')
            ? -parseInt(token.slice(3), 16)
            : token.startsWith('0x')
                ? parseInt(token, 16)
                : parseInt(token, 10);
        return Number.isFinite(parsed) ? parsed : null;
    }

    private decodeMiStreamToken(token: string): string {
        if (token.startsWith('"') && token.endsWith('"')) {
            return this.decodeMiCString(token.slice(1, -1));
        }
        return token;
    }

    private decodeMiCString(value: string): string {
        try {
            return JSON.parse(`"${value}"`);
        } catch {
            // Fallback C-like unescape for MI strings not valid JSON (e.g. octal escapes).
            return value
                .replace(/\\([0-7]{1,3})/g, (_, oct: string) => String.fromCharCode(parseInt(oct, 8)))
                .replace(/\\x([0-9a-fA-F]{2})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
                .replace(/\\n/g, '\n')
                .replace(/\\r/g, '\r')
                .replace(/\\t/g, '\t')
                .replace(/\\"/g, '"')
                .replace(/\\\\/g, '\\');
        }
    }

    private classifyGdbFailure(message: string, expression: string): GdbResolveFailure {
        if (this.isSymbolMissingMessage(message)) {
            return new GdbResolveFailure('symbol_not_found', `Symbol not found: ${expression}`);
        }
        if (this.isExpressionInvalidMessage(message)) {
            return new GdbResolveFailure('expression_invalid', `Invalid expression "${expression}": ${message}`);
        }
        return new GdbResolveFailure('gdb_failed', `GDB failed for "${expression}": ${message}`);
    }

    private async classifyVarCreateFailure(message: string, expression: string): Promise<GdbResolveFailure> {
        // MI var-create often reports a generic message. Probe address-eval once
        // to obtain a specific diagnostic and classify accurately.
        try {
            const [probe] = await this.runGdbMi([
                this.miEvaluateExpression(`&(${expression})`),
            ]);
            if (probe.cls === 'error') {
                return this.classifyGdbFailure(probe.message || message, expression);
            }
        } catch {
            // ignore probe failures and fall through to generic classification
        }
        return this.classifyGdbFailure(message, expression);
    }

    private isSymbolMissingMessage(message: string): boolean {
        return /no symbol\b|there is no member or method named\b|no type\b/i.test(message);
    }

    private isExpressionInvalidMessage(message: string): boolean {
        return /syntax error|parse error|junk after end of expression|unexpected token/i.test(message);
    }

    private assertExpressionIsSafe(expression: string): void {
        if (/[\r\n\x1a]/.test(expression)) {
            throw new GdbResolveFailure('expression_invalid', 'Expression contains disallowed control characters');
        }
    }

    // ── ptype /o parsing ───────────────────────────────────────

    /**
     * Extract the ptype /o output section from the full GDB output.
     */
    private extractPtypeOutput(output: string): string | null {
        const ptypeStart = output.indexOf('type = ');
        if (ptypeStart === -1) { return null; }
        const section = output.substring(ptypeStart);
        // Check if it's a struct/class/union
        if (!section.match(/type\s*=\s*(class|struct|union)/)) {
            return null;
        }
        return section;
    }

    /**
     * Parse simple (non-struct) type from ptype output.
     */
    private parseSimpleType(output: string): string {
        const match = output.match(/type\s*=\s*(.+)/);
        if (match) {
            return this.normalizeTypeName(match[1]);
        }
        return 'unknown';
    }

    /**
     * Guess the size of a simple type by name.
     */
    private guessSize(typeName: string): number {
        const t = typeName.toLowerCase().replace(/\s+/g, ' ').trim();
        if (t === 'float') { return 4; }
        if (t === 'double') { return 8; }
        if (t === 'bool' || t === '_bool') { return 1; }
        if (t.includes('int8') || t === 'char' || t === 'unsigned char' || t === 'signed char') { return 1; }
        if (t.includes('int16') || t === 'short' || t === 'unsigned short') { return 2; }
        if (t.includes('int64') || t === 'long long' || t === 'unsigned long long') { return 8; }
        if (t.includes('*')) { return 4; } // 32-bit pointer
        return 4; // Default: 32-bit
    }

    /**
     * Parse `ptype /o` output into a WatchNode tree.
     *
     * GDB ptype /o output format:
     *
     *   type = class PidController {
     *     private:
     *   /*    0      |     4 * /    float kp_;
     *   /*    4      |     4 * /    float ki_;
     *   /*   24      |    20 * /    struct FeedbackOverride {
     *   /*   24      |     4 * /        const float *angle_fb;
     *                                } fb_override_;
     *                                / * total size (bytes):   48 * /
     *                              }
     */
    private parsePtypeO(
        ptypeOutput: string,
        nodeName: string,
        expression: string,
        baseAddress: number,
        isRoot: boolean,
        pointerDepth: number,
    ): WatchNode {
        const lines = ptypeOutput.split('\n');

        // Extract top-level type name
        const typeMatch = lines[0].match(/type\s*=\s*(class|struct|union)\s+(.+?)\s*\{/);
        const topTypeName = typeMatch ? this.normalizeTypeName(typeMatch[2]) : nodeName;

        // Extract top-level total size (use the last match, nested types also emit this line)
        const totalSizeMatches = [...ptypeOutput.matchAll(/total size \(bytes\):\s+(\d+)/g)];
        const totalSize = totalSizeMatches.length > 0
            ? parseInt(totalSizeMatches[totalSizeMatches.length - 1][1])
            : 0;

        const root = createWatchNode(nodeName, expression, topTypeName, baseAddress, totalSize, isRoot);

        // Parse fields
        this.parseFields(lines, 1, root, expression, baseAddress, 0, topTypeName, pointerDepth);

        return root;
    }

    /**
     * Recursively parse field lines from ptype /o output.
     */
    private parseFields(
        lines: string[],
        startLine: number,
        parent: WatchNode,
        parentExpr: string,
        baseAddress: number,
        parentOffset: number,
        scopeTypeName: string,
        pointerDepth: number,
    ): number {
        let i = startLine;
        while (i < lines.length) {
            const line = lines[i];

            // Skip access specifiers: "private:", "public:", "protected:"
            if (line.match(/^\s*(private|public|protected)\s*:\s*$/)) {
                i++;
                continue;
            }

            // Skip empty lines, closing brace of top-level
            if (line.trim() === '' || line.trim() === '}' || line.trim() === '};') {
                i++;
                continue;
            }

            // Total size line
            if (line.match(/total size \(bytes\)/)) {
                i++;
                continue;
            }

            // Static members of current scope/class:
            //   static const uint8_t MAX_MOTORS;
            //   static struct DjiDriver::TxGroup tx_groups_[6];
            const staticMatch = line.match(/^\s*static\s+(.+?)\s+(\w+)(\[(\d+)\])?\s*;/);
            if (staticMatch) {
                const staticType = this.normalizeTypeName(staticMatch[1]);
                const fieldName = staticMatch[2];
                const arraySize = staticMatch[4] ? parseInt(staticMatch[4]) : 0;
                const staticExpr = `${scopeTypeName}::${fieldName}`;

                if (arraySize > 0) {
                    const arrNode = createWatchNode(
                        fieldName,
                        staticExpr,
                        `${staticType}[${arraySize}]`,
                        0,
                        0,
                        false,
                    );
                    arrNode.value = `[${arraySize}]`;
                    parent.children.push(arrNode);
                } else {
                    const node = createWatchNode(
                        fieldName,
                        staticExpr,
                        staticType,
                        0,
                        0,
                        false,
                    );
                    parent.children.push(node);
                }

                i++;
                continue;
            }

            // Check for nested struct/class/union opening
            const nestedMatch = line.match(
                /\/\*\s+(\d+)\s+\|\s+(\d+)\s+\*\/\s+(struct|class|union)(?:\s+(.+?))?\s*\{/
            );
            if (nestedMatch) {
                const absOffset = parseInt(nestedMatch[1]);
                const relOffset = Math.max(0, absOffset - parentOffset);
                const size = parseInt(nestedMatch[2]);
                const nestedTypeNameRaw = nestedMatch[4] || nestedMatch[3];
                const nestedTypeName = this.normalizeTypeName(nestedTypeNameRaw);
                // Find the closing line to get the field name
                i++;
                const result = this.findNestedEnd(lines, i);
                const fieldName = result.name;
                const arraySize = result.arraySize;
                const endIdx = result.endLine;

                const fieldExpr = this.composeMemberExpr(parentExpr, fieldName);
                const fieldAddr = baseAddress + relOffset;

                if (arraySize > 0) {
                    // Array of structs
                    const elemSize = size / arraySize;
                    const arrNode = createWatchNode(
                        fieldName, fieldExpr, `${nestedTypeName}[${arraySize}]`,
                        fieldAddr, size, false,
                    );
                    for (let ai = 0; ai < arraySize; ai++) {
                        const elemExpr = this.composeIndexExpr(fieldExpr, ai);
                        const elemNode = createWatchNode(
                            `[${ai}]`, elemExpr, nestedTypeName,
                            fieldAddr + ai * elemSize, elemSize, false,
                        );
                        // Parse the nested struct fields for each element
                        // (they all share the same layout, just different base addresses)
                        this.parseFields(
                            lines,
                            i,
                            elemNode,
                            elemExpr,
                            fieldAddr + ai * elemSize,
                            absOffset,
                            nestedTypeName,
                            pointerDepth,
                        );
                        arrNode.children.push(elemNode);
                    }
                    parent.children.push(arrNode);
                } else {
                    const nestedNode = createWatchNode(
                        fieldName, fieldExpr, nestedTypeName,
                        fieldAddr, size, false,
                    );
                    // Recursively parse nested fields
                    this.parseFields(lines, i, nestedNode, fieldExpr, fieldAddr, absOffset, nestedTypeName, pointerDepth);
                    parent.children.push(nestedNode);
                }

                i = endIdx + 1;
                continue;
            }

            // Regular field: /* offset | size */   type name;
            const fieldMatch = line.match(
                /\/\*\s+(\d+)\s+\|\s+(\d+)\s+\*\/\s+(.+?)\s*([*&]+)?\s*(\w+)(\[(\d+)\])?\s*;/
            );
            if (fieldMatch) {
                const absOffset = parseInt(fieldMatch[1]);
                const relOffset = Math.max(0, absOffset - parentOffset);
                const size = parseInt(fieldMatch[2]);
                let typeName = fieldMatch[3].trim();
                const ptrOrRef = fieldMatch[4] ? fieldMatch[4].trim() : '';
                const fieldName = fieldMatch[5];
                const arraySize = fieldMatch[7] ? parseInt(fieldMatch[7]) : 0;

                if (ptrOrRef) {
                    typeName = `${typeName} ${ptrOrRef}`.trim();
                }

                const fieldExpr = this.composeMemberExpr(parentExpr, fieldName);
                const fieldAddr = baseAddress + relOffset;

                if (arraySize > 0) {
                    // Array field
                    const elemSize = size / arraySize;
                    typeName = this.normalizeTypeName(typeName);
                    const arrNode = createWatchNode(
                        fieldName, fieldExpr, `${typeName}[${arraySize}]`,
                        fieldAddr, size, false,
                    );
                    for (let ai = 0; ai < arraySize; ai++) {
                        const elemExpr = this.composeIndexExpr(fieldExpr, ai);
                        const elemNode = createWatchNode(
                            `[${ai}]`, elemExpr, typeName,
                            fieldAddr + ai * elemSize, elemSize, false,
                        );
                        arrNode.children.push(elemNode);
                    }
                    parent.children.push(arrNode);
                } else {
                    // Scalar field
                    const normalizedType = this.normalizeTypeName(typeName);
                    const node = createWatchNode(
                        fieldName, fieldExpr, normalizedType,
                        fieldAddr, size, false,
                    );
                    const nextPointerDepth = pointerDepth + 1;
                    if (this.shouldCreatePointerDerefChild(normalizedType, nextPointerDepth)) {
                        const pointeeType = this.normalizePointeeType(normalizedType);
                        const isScalarPointee = this.isScalarPointeeType(pointeeType);
                        const derefNode = createWatchNode(
                            `*${fieldName}`,
                            this.composePointerDerefExpr(fieldExpr),
                            pointeeType,
                            0,
                            isScalarPointee ? this.guessSize(pointeeType) : 0,
                            false,
                        );
                        derefNode.pointerDeref = true;
                        derefNode.pointerDerefDepth = nextPointerDepth;
                        derefNode.pointerCompositePending = !isScalarPointee;
                        derefNode.relativeAddress = 0;
                        node.children.push(derefNode);
                    }
                    parent.children.push(node);
                }

                i++;
                continue;
            }

            // Closing brace of a nested struct: "} fieldname;"
            const closingMatch = line.match(/\}\s+(\w+)(\[(\d+)\])?\s*;/);
            if (closingMatch) {
                // End of nested struct — return to parent
                return i;
            }

            // Unrecognized line — skip
            i++;
        }
        return i;
    }

    /**
     * Starting from line `startIdx`, find the closing `} fieldName;` for a nested struct.
     * Handles nested-within-nested structs.
     */
    private findNestedEnd(
        lines: string[],
        startIdx: number,
    ): { name: string; arraySize: number; endLine: number } {
        let depth = 1;
        let i = startIdx;
        while (i < lines.length) {
            const line = lines[i];
            // Opening brace increases depth
            if (line.match(/\{[\s]*$/)) {
                depth++;
            }
            // Closing brace with field name
            const closeMatch = line.match(/\}\s+(\w+)(\[(\d+)\])?\s*;/);
            if (closeMatch) {
                depth--;
                if (depth === 0) {
                    return {
                        name: closeMatch[1],
                        arraySize: closeMatch[3] ? parseInt(closeMatch[3]) : 0,
                        endLine: i,
                    };
                }
            }
            // Plain closing brace (end of top-level type)
            if (line.trim() === '}' || line.trim() === '};') {
                depth--;
                if (depth <= 0) {
                    return { name: '', arraySize: 0, endLine: i };
                }
            }
            i++;
        }
        return { name: '', arraySize: 0, endLine: i };
    }

    private normalizeTypeName(typeName: string): string {
        return typeName.replace(/\s+/g, ' ').trim();
    }

    private isEnumType(typeName: string): boolean {
        return /^enum(\s+class)?\s+/i.test(this.normalizeTypeName(typeName));
    }

    private collectEnumTypes(node: WatchNode, out: Set<string>): void {
        if (this.isEnumType(node.typeName)) {
            out.add(this.normalizeTypeName(node.typeName));
        }
        for (const child of node.children) {
            this.collectEnumTypes(child, out);
        }
    }

    private applyEnumMap(node: WatchNode, enumTypeName: string, map: Record<string, string>): void {
        if (this.normalizeTypeName(node.typeName) === enumTypeName) {
            node.enumValueNames = map;
        }
        for (const child of node.children) {
            this.applyEnumMap(child, enumTypeName, map);
        }
    }

    private async attachEnumValueNames(root: WatchNode): Promise<void> {
        const enumTypes = new Set<string>();
        this.collectEnumTypes(root, enumTypes);
        if (enumTypes.size === 0) { return; }

        for (const enumType of enumTypes) {
            const enumMap = await this.resolveEnumMap(enumType);
            if (!enumMap || Object.keys(enumMap).length === 0) { continue; }
            this.applyEnumMap(root, enumType, enumMap);
        }
    }

    private async resolveEnumMap(enumTypeName: string): Promise<Record<string, string> | null> {
        const cached = this.enumValueCache.get(enumTypeName);
        if (cached !== undefined) {
            return cached;
        }

        for (const candidate of this.buildEnumTypeCandidates(enumTypeName)) {
            try {
                const [res] = await this.runGdbMi([
                    this.miConsoleCommand(`ptype ${candidate}`),
                ]);
                if (res.cls === 'error') {
                    if (this.isSymbolMissingMessage(res.message) || this.isExpressionInvalidMessage(res.message)) {
                        continue;
                    }
                    throw this.classifyGdbFailure(res.message, candidate);
                }
                const parsed = this.parseEnumMap(res.console);
                if (parsed && Object.keys(parsed).length > 0) {
                    this.enumValueCache.set(enumTypeName, parsed);
                    return parsed;
                }
            } catch {
                // Try next candidate form.
            }
        }

        this.enumValueCache.set(enumTypeName, null);
        return null;
    }

    private parseEnumMap(output: string): Record<string, string> | null {
        const start = output.indexOf('{');
        const end = output.lastIndexOf('}');
        if (start === -1 || end === -1 || end <= start) { return null; }

        const body = output.substring(start + 1, end);
        const entries = body
            .split(',')
            .map(s => s.trim())
            .filter(Boolean);

        if (entries.length === 0) { return null; }

        const result: Record<string, string> = {};
        let currentValue = -1;

        for (const entry of entries) {
            const m = entry.match(/^([A-Za-z_]\w*(?:::\w+)*)\s*(?:=\s*(.+))?$/);
            if (!m) { continue; }
            const name = m[1];
            const explicit = m[2]?.trim();

            let value: number | null;
            if (explicit) {
                value = this.parseEnumValueLiteral(explicit);
                if (value === null) { continue; }
            } else {
                value = currentValue + 1;
            }

            currentValue = value;
            result[value.toString()] = name;
        }

        return Object.keys(result).length > 0 ? result : null;
    }

    private parseEnumValueLiteral(expr: string): number | null {
        // remove wrapping parentheses and suffixes like U/UL/LL
        let s = expr.trim();
        while (s.startsWith('(') && s.endsWith(')')) {
            s = s.slice(1, -1).trim();
        }
        s = s.replace(/([uUlL]+)$/g, '');
        if (/^-?0x[0-9a-fA-F]+$/.test(s)) {
            return parseInt(s, 16);
        }
        if (/^-?\d+$/.test(s)) {
            return parseInt(s, 10);
        }
        return null;
    }

    private async attachStaticMemberValues(root: WatchNode): Promise<void> {
        const staticLeaves: WatchNode[] = [];
        this.collectStaticLeaves(root, staticLeaves);
        for (const node of staticLeaves) {
            const value = await this.resolveExpressionValue(node.expression);
            if (value !== null) {
                node.value = value;
            }
        }
    }

    private collectStaticLeaves(node: WatchNode, out: WatchNode[]): void {
        if (
            node.children.length === 0 &&
            node.address === 0 &&
            node.size === 0 &&
            node.expression.includes('::') &&
            !node.typeName.includes('[')
        ) {
            out.push(node);
        }
        for (const child of node.children) {
            this.collectStaticLeaves(child, out);
        }
    }

    private async resolveExpressionValue(expression: string): Promise<string | null> {
        try {
            this.assertExpressionIsSafe(expression);
            const [res] = await this.runGdbMi([
                this.miEvaluateExpression(expression),
            ]);
            if (res.cls === 'error') { return null; }
            return res.value ? res.value.trim() : null;
        } catch {
            return null;
        }
    }

    private buildEnumTypeCandidates(enumTypeName: string): string[] {
        const normalized = this.normalizeTypeName(enumTypeName);
        const raw = normalized.replace(/^enum(\s+class)?\s+/i, '').trim();
        const candidates = [
            normalized,
            `enum ${raw}`,
            `enum class ${raw}`,
            raw,
        ];
        const unique = new Set<string>();
        for (const c of candidates) {
            if (c) { unique.add(c); }
        }
        return Array.from(unique);
    }

    private buildCompositeTypeCandidates(typeName: string): string[] {
        const normalized = this.normalizeTypeName(typeName);
        const stripped = this.normalizeTypeName(normalized.replace(/\b(const|volatile)\b/g, ' '));
        const raw = stripped.replace(/^(class|struct|union)\s+/, '').trim();
        const candidates = [
            normalized,
            stripped,
            raw,
            `struct ${raw}`,
            `class ${raw}`,
            `union ${raw}`,
        ];
        const unique = new Set<string>();
        for (const c of candidates) {
            if (c) { unique.add(c); }
        }
        return Array.from(unique);
    }

    private shouldCreatePointerDerefChild(typeName: string, nextPointerDepth: number): boolean {
        if (nextPointerDepth > GdbSymbolResolver.MAX_AUTO_POINTER_DEREF_DEPTH) {
            return false;
        }
        if (!this.isPointerType(typeName)) { return false; }
        const normalized = this.normalizeTypeName(typeName);
        const pointee = this.normalizePointeeType(normalized).toLowerCase();
        if (!pointee || pointee === 'void') { return false; }
        return true;
    }

    private isPointerType(typeName: string): boolean {
        return this.normalizeTypeName(typeName).includes('*');
    }

    private isScalarPointeeType(typeName: string): boolean {
        const pointee = this.normalizeTypeName(typeName).toLowerCase();
        return (
            pointee === 'float' ||
            pointee === 'double' ||
            pointee === 'bool' ||
            pointee === '_bool' ||
            pointee === 'char' ||
            pointee === 'signed char' ||
            pointee === 'unsigned char' ||
            pointee === 'int8_t' ||
            pointee === 'uint8_t' ||
            pointee === 'int16_t' ||
            pointee === 'uint16_t' ||
            pointee === 'int32_t' ||
            pointee === 'uint32_t' ||
            pointee === 'int' ||
            pointee === 'unsigned int' ||
            pointee === 'short' ||
            pointee === 'signed short' ||
            pointee === 'unsigned short' ||
            pointee === 'long' ||
            pointee === 'signed long' ||
            pointee === 'unsigned long' ||
            pointee.startsWith('enum ')
        );
    }

    private composeMemberExpr(parentExpr: string, fieldName: string): string {
        return `(${parentExpr}).${fieldName}`;
    }

    private composeIndexExpr(parentExpr: string, index: number): string {
        return `(${parentExpr})[${index}]`;
    }

    private composePointerDerefExpr(expr: string): string {
        return `*(${expr})`;
    }

    private normalizePointeeType(typeName: string): string {
        let t = this.normalizeTypeName(typeName);
        t = t.replace(/\s*\*+\s*$/, '');
        t = t.replace(/\b(const|volatile)\b/g, ' ');
        t = this.normalizeTypeName(t);
        t = t.replace(/^(class|struct|union)\s+/, '');
        return this.normalizeTypeName(t);
    }
}
