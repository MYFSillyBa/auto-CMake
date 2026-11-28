import * as vscode from 'vscode';
import path from 'node:path';
import { existsSync, statSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { DEFAULT_POLL_INTERVAL, LiveWatchService, POLL_INTERVALS, ServiceEvent } from './service/liveWatchService';
import { DisplayFormat, ViewRow } from './model/watchNode';
import { Endpoint, isTclServer, portFromArgs, portNumber, windowsOpenOcdPorts } from './endpoint';

const viewId = 'stm32ProjectHelper.liveWatch';
const viewContainerId = 'workbench.view.extension.stm32ProjectHelper';
const configuration = 'stm32ProjectHelper.liveWatch';
const firmwareExtensions = /\.(?:elf|axf|out)$/i;

interface Workbench {
    language: string;
    folder?: vscode.WorkspaceFolder;
    state: {
        options: { firmwareElfPath?: string; buildDirectory?: string };
        inspection?: { tools?: { compiler?: string } };
        toolInventory?: { tools?: { compiler?: string } };
    };
    tools(): { compiler?: string };
    output: { appendLine(message: string): void };
}

function executable(value: unknown): value is string {
    if (typeof value !== 'string' || !value) return false;
    try { return statSync(value).isFile(); } catch { return false; }
}

export class LiveWatchController implements vscode.WebviewViewProvider, vscode.Disposable {
    private readonly service: LiveWatchService;
    private readonly sessions = new Map<string, { tcl?: number; args?: vscode.DebugConfiguration; excluded?: Set<number> }>();
    private view?: vscode.WebviewView;
    private ready = false;
    private pending = true;
    private sidebarExpanded = false;
    private sequence = 0;
    private awaiting?: number;
    private last?: { rows: Map<string, ViewRow>; ids: string[] };
    private viewTimer?: ReturnType<typeof setImmediate>;
    private notices = '';
    private actions: Promise<void> = Promise.resolve();
    private activeSession?: vscode.DebugSession;
    private dialogFirmware?: string;
    private disconnected = false;
    private optionsRevision = 0;
    private readonly disposables: vscode.Disposable[];

    constructor(private readonly context: vscode.ExtensionContext, private readonly workbench: Workbench) {
        this.service = new LiveWatchService(context, {
            endpoint: () => this.endpoint(),
            firmware: () => this.firmware(),
        });
        this.service.onEvent(event => this.onEvent(event));
        this.disposables = [
            vscode.window.registerWebviewViewProvider(viewId, this),
            vscode.debug.onDidStartDebugSession(session => this.debugChanged(session)),
            vscode.debug.onDidChangeActiveDebugSession(session => this.debugChanged(session)),
            vscode.debug.onDidTerminateDebugSession(session => {
                this.sessions.delete(session.id);
                if (session.id === this.activeSession?.id) this.debugChanged(undefined);
            }),
            vscode.debug.onDidReceiveDebugSessionCustomEvent(event => this.debugEvent(event)),
        ];
        if (vscode.debug.activeDebugSession) this.debugChanged(vscode.debug.activeDebugSession);
        else if (vscode.workspace.workspaceFolders?.length && process.platform === 'win32' && !vscode.env.remoteName) {
            void this.service.tryConnect();
        }
    }

    async open(): Promise<void> {
        if (await this.revealView()) return;
        const commands = await vscode.commands.getCommands(true);
        if (commands.includes(viewContainerId)) {
            await vscode.commands.executeCommand(viewContainerId);
            if (await this.revealView()) return;
        }
        const reload = this.label('重新加载窗口', 'Reload Window');
        const choice = await vscode.window.showInformationMessage(
            this.label('VS Code 尚未注册实时变量视图。重新加载当前窗口后即可使用。', 'VS Code has not registered the Live Variables view. Reload this window to finish loading it.'),
            reload,
        );
        if (choice === reload) await vscode.commands.executeCommand('workbench.action.reloadWindow');
    }

    private async revealView(): Promise<boolean> {
        if (this.view) {
            this.view.show(false);
            return true;
        }
        const commands = await vscode.commands.getCommands(true);
        const reveal = [viewId + '.focus', viewId + '.open'].find(command => commands.includes(command));
        if (!reveal) return false;
        try {
            await vscode.commands.executeCommand(reveal);
            return true;
        } catch (error) {
            if ((await vscode.commands.getCommands(true)).includes(reveal)) throw error;
            return false;
        }
    }

    updateProject(): void {
        this.optionsRevision++;
        if (this.activeSession) return;
        this.dialogFirmware = undefined;
        this.service.reconnect();
        if (this.view?.visible) void this.service.refreshSymbols(false).catch(error => this.report(error));
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        this.ready = false;
        this.last = undefined;
        this.awaiting = undefined;
        const assets = vscode.Uri.joinPath(this.context.extensionUri, 'media');
        const uri = (name: string) => view.webview.asWebviewUri(vscode.Uri.joinPath(assets, name));
        view.webview.options = { enableScripts: true, localResourceRoots: [assets] };
        view.title = this.label('实时变量查看', 'Live Variables');
        const rates = POLL_INTERVALS.map(interval => interval === 0
            ? '<option value="0" id="poll-unlimited"></option>'
            : `<option value="${interval}" title="${interval} ms"${interval === DEFAULT_POLL_INTERVAL ? ' selected' : ''}>${Number((1000 / interval).toFixed(1))} Hz</option>`).join('');
        const nonce = randomBytes(24).toString('base64');
        view.webview.html = `<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${view.webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="${uri('live-watch.css')}"></head>
<body><header>
<div class="connection-row">
<div class="status" id="status" role="status"><span class="dot"></span><strong id="state"></strong><span id="endpoint"></span></div>
<button id="sidebar-size" type="button" aria-pressed="false" hidden></button>
</div>
<div class="actions"><button id="connect"></button><button id="pause"></button><select id="poll-rate">${rates}</select><button id="options"><span id="options-text"></span></button></div>
<div id="firmware"></div><p id="notice" role="status" hidden></p>
</header>
<div class="watch-table" tabindex="0"><table>
<thead><tr><th id="label-name"></th><th id="label-value"></th><th id="label-address"></th><th id="label-type"></th></tr></thead>
<tbody id="rows"></tbody>
<tfoot><tr><td colspan="4"><form id="add">
<input id="name" autocomplete="off" maxlength="240" required><button id="submit" type="submit"></button>
</form></td></tr></tfoot>
</table></div>
<div id="menu" hidden></div>
<script nonce="${nonce}" src="${uri('live-watch.js')}"></script></body></html>`;
        const subscription = view.webview.onDidReceiveMessage(message => this.receive(message));
        view.onDidChangeVisibility(() => {
            if (view !== this.view) return;
            this.service.setVisible(view.visible);
            if (view.visible) { this.awaiting = undefined; this.last = undefined; this.refresh(); }
        });
        view.onDidDispose(() => {
            subscription.dispose();
            if (view !== this.view) return;
            this.view = undefined;
            this.ready = false;
            this.service.setVisible(false);
        });
        this.service.setVisible(view.visible);
        if (!this.disconnected) void this.service.tryConnect();
        if (this.service.getConnectionState() !== 'connected') void this.service.refreshSymbols(false).catch(error => this.report(error));
    }

    refresh(): void {
        this.pending = true;
        if (this.view) {
            const title = this.label('实时变量查看', 'Live Variables');
            if (this.view.title !== title) this.view.title = title;
        }
        if (!this.view?.visible || !this.ready || this.awaiting !== undefined || this.viewTimer) return;
        this.viewTimer = setImmediate(() => {
            this.viewTimer = undefined;
            this.flush();
        });
    }

    private flush(): void {
        if (!this.view?.visible || !this.ready || this.awaiting !== undefined || !this.pending) return;
        this.pending = false;
        const previous = this.last;
        const ids: string[] = [];
        const current = new Map<string, ViewRow>();
        const changed: ViewRow[] = [];
        for (const row of this.service.getRows()) {
            const existing = previous?.rows.get(row.id);
            ids.push(row.id); current.set(row.id, row);
            if (!existing || existing.value !== row.value || existing.address !== row.address ||
                existing.expanded !== row.expanded || existing.typeName !== row.typeName ||
                existing.displayFormat !== row.displayFormat || existing.hasChildren !== row.hasChildren ||
                existing.name !== row.name || existing.depth !== row.depth || existing.error !== row.error) changed.push(row);
        }
        const order = !previous || ids.length !== previous.ids.length || ids.some((id, index) => id !== previous.ids[index]) ? ids : undefined;
        this.last = { rows: current, ids };
        const id = this.awaiting = ++this.sequence;
        void this.view.webview.postMessage({
            type: 'frame', id, changed, order, language: this.workbench.language, notice: this.notices,
            sidebarExpanded: this.sidebarExpanded,
            pollInterval: this.service.getPollInterval(),
            paused: this.service.isPaused(), state: this.service.getConnectionState(), endpoint: this.service.getEndpoint(),
            firmware: this.service.getElfPath(),
        }).then(sent => {
            if (!sent && this.awaiting === id) { this.awaiting = undefined; this.last = undefined; }
        });
    }

    private receive(message: any): void {
        if (!message || typeof message.type !== 'string') return;
        if (message.type === 'ack') {
            if (message.id === this.awaiting) { this.awaiting = undefined; if (this.pending) this.refresh(); }
            return;
        }
        if (message.type === 'ready') {
            this.ready = true; this.awaiting = undefined; this.last = undefined; this.refresh(); return;
        }
        this.actions = this.actions.then(async () => {
            this.notices = '';
            const id = typeof message.id === 'string' ? message.id : '';
            switch (message.type) {
                case 'add': if (typeof message.name === 'string') await this.service.addWatch(message.name); break;
                case 'remove': this.service.removeWatch(id); break;
                case 'expand': this.service.toggleExpand(id); break;
                case 'format':
                    if (Object.values(DisplayFormat).includes(message.value)) this.service.setDisplayFormat(id, message.value);
                    break;
                case 'write':
                    if (typeof message.value === 'string' && message.value.length <= 120) await this.service.writeValue(id, message.value);
                    break;
                case 'pause': this.service.togglePause(); break;
                case 'pollInterval': this.service.setPollInterval(message.value); break;
                case 'toggleSidebar': {
                    const expanded = !this.sidebarExpanded;
                    await vscode.commands.executeCommand('setContext', 'stm32ProjectHelper.liveWatchExpanded', expanded);
                    this.sidebarExpanded = expanded;
                    break;
                }
                case 'connect':
                    this.disconnected = this.service.getConnectionState() === 'connected';
                    this.service.setAutoConnect(!this.disconnected);
                    break;
                case 'options': await this.options(); break;
            }
        }).catch(error => this.report(error)).finally(() => this.refresh());
    }

    private label(zh: string, en: string): string { return this.workbench.language === 'en' ? en : zh; }

    private async options(): Promise<void> {
        const pick = await vscode.window.showQuickPick([
            { id: 'elf', label: this.label('选择 ELF 固件…', 'Choose ELF firmware…') },
            { id: 'refresh', label: this.label('重新读取符号', 'Reload symbols') },
            { id: 'clear', label: this.label('清空变量列表', 'Clear variables') },
            { id: 'settings', label: this.label('连接与工具设置', 'Connection and tool settings') },
        ]);
        if (pick?.id === 'elf') {
            const file = await vscode.window.showOpenDialog({ filters: { ELF: ['elf','axf','out'] }, canSelectMany: false });
            if (file?.[0]) { this.dialogFirmware = file[0].fsPath; await this.service.refreshSymbols(); }
        } else if (pick?.id === 'refresh') await this.service.refreshSymbols();
        else if (pick?.id === 'clear') await this.service.clearWatches();
        else if (pick?.id === 'settings') await vscode.commands.executeCommand('workbench.action.openSettings', configuration);
    }

    private report(error: unknown): void {
        this.notices = error instanceof Error ? error.message : String(error);
        this.workbench.output.appendLine(this.notices);
        this.refresh();
    }

    private onEvent(event: ServiceEvent): void {
        if (event.message) this.notices = event.message;
        else if (event.connectionState === 'connected') this.notices = '';
        this.refresh();
    }

    private debugChanged(session?: vscode.DebugSession): void {
        const supported = session?.type === 'cortex-debug' ? session : undefined;
        if (supported?.id === this.activeSession?.id) return;
        this.activeSession = supported;
        this.dialogFirmware = undefined;
        this.optionsRevision++;
        this.disconnected = false;
        this.service.setAutoConnect(true);
        if (supported) {
            void Promise.resolve(supported.customRequest('get-arguments')).then(args => {
                if (args && supported.id === this.activeSession?.id) this.updateSession(supported, args);
            }, () => {});
        }
    }

    private debugEvent(event: vscode.DebugSessionCustomEvent): void {
        if (event.session.type !== 'cortex-debug') return;
        const name = event.event.replace(/^custom-event-/, '');
        if (name === 'ports-allocated') this.updateSession(event.session, undefined, event.body);
        if (name === 'post-start-server' || name === 'post-start-gdb') this.updateSession(event.session, event.body);
    }

    private updateSession(session: vscode.DebugSession, args?: vscode.DebugConfiguration, ports?: Record<string, unknown>): void {
        const old = this.sessions.get(session.id);
        const activePorts = ports ?? args?.pvtPorts;
        const tcl = portNumber(activePorts?.tclPort) ?? old?.tcl;
        const excluded = activePorts
            ? new Set(Object.entries(activePorts).flatMap(([key, value]) => !/tcl/i.test(key) && portNumber(value) ? [Number(value)] : []))
            : old?.excluded;
        this.sessions.set(session.id, { tcl, excluded, args: args ?? old?.args });
        if (session.id === this.activeSession?.id && !this.disconnected &&
            (tcl !== old?.tcl || (args?.executable && args.executable !== old?.args?.executable))) {
            this.optionsRevision++;
            this.service.reconnect();
        }
    }

    private async endpoint(): Promise<Endpoint | undefined> {
        if (this.disconnected || !vscode.workspace.isTrusted || vscode.env.remoteName || process.platform !== 'win32') return;
        const config = vscode.workspace.getConfiguration(configuration, this.workbench.folder?.uri);
        const manual = portNumber(config.get('tclPort'));
        const session = this.activeSession;
        const cached = session && this.sessions.get(session.id);
        const args = cached?.args ?? session?.configuration;
        const typed = args?.servertype;
        const explicit = manual ?? portFromArgs(args?.serverArgs) ?? cached?.tcl;
        if (explicit) {
            const candidates = ['127.0.0.1', '::1'].map(host => ({ host, port: explicit }));
            const reachable = await Promise.all(candidates.map(candidate => isTclServer(candidate)));
            return candidates.find((_, index) => reachable[index]);
        }
        if (typed && typed !== 'openocd' && typed !== 'external') return;
        const listeners = await windowsOpenOcdPorts();
        const candidates = listeners.filter(item => !cached?.excluded?.has(item.port)).slice(0, 20);
        const results = await Promise.all(candidates.map(async item => await isTclServer(item) ? item : undefined));
        const unique = new Map<number, Endpoint>();
        for (const item of results) {
            if (item && !unique.has(item.port)) unique.set(item.port, item);
        }
        if (unique.size === 1) return unique.values().next().value;
        if (unique.size > 1) this.notices = 'Multiple OpenOCD Tcl servers detected. Select a Tcl port in settings.';
        return;
    }

    private async firmware(): Promise<{ path: string; gdb: string; message?: string }> {
        const config = vscode.workspace.getConfiguration(configuration, this.workbench.folder?.uri);
        const session = this.activeSession;
        const args = session && (this.sessions.get(session.id)?.args ?? session.configuration);
        const folder = this.workbench.folder ?? session?.workspaceFolder ?? vscode.workspace.workspaceFolders?.[0];
        const base = folder?.uri.fsPath;
        const options = this.workbench.state.options;
        const sessionBase = session?.workspaceFolder?.uri.fsPath ?? base;
        const revision = this.optionsRevision;
        const expand = (file: unknown, root = base) => {
            if (typeof file !== 'string' || !file.trim()) return undefined;
            let name = file.replace(/\$\{workspaceFolder\}/g, () => root ?? '');
            if (name.includes('${')) return undefined;
            if (!path.isAbsolute(name)) name = root ? path.resolve(root, name) : name;
            return name;
        };
        const compiler = this.workbench.tools().compiler ?? this.workbench.state.inspection?.tools?.compiler ??
            this.workbench.state.toolInventory?.tools?.compiler;
        const adjacent = compiler && path.join(path.dirname(compiler), path.basename(compiler).replace(/(?:gcc|g\+\+)(\.exe)?$/i, 'gdb$1'));
        const expandGdb = (value: unknown, root?: string) => typeof value === 'string' && /^[\w.-]+$/.test(value) ? value : expand(value, root);
        const configuredGdb = expandGdb(config.get('gdbPath'));
        const gdb = configuredGdb || expandGdb(args?.gdbPath, sessionBase) ||
            (adjacent && adjacent !== compiler && executable(adjacent) ? adjacent : 'arm-none-eabi-gdb.exe');
        const selected = this.dialogFirmware || expand(config.get('elfPath')) || expand(args?.executable, sessionBase);
        if (selected) return executable(selected) && firmwareExtensions.test(selected) ? { path: selected, gdb } : { path: '', gdb, message: 'Selected ELF firmware was not found.' };
        if (executable(options.firmwareElfPath) && firmwareExtensions.test(options.firmwareElfPath)) return { path: options.firmwareElfPath, gdb };
        if (!folder) return { path: '', gdb, message: 'No ELF firmware found. Choose a firmware file.' };
        const launches = vscode.workspace.getConfiguration('launch', folder.uri).get<vscode.DebugConfiguration[]>('configurations', []);
        const launchFiles = new Set(launches.map(item => expand(item.executable ?? item.program))
            .filter((file): file is string => executable(file) && firmwareExtensions.test(file)));
        if (launchFiles.size === 1) return { path: [...launchFiles][0], gdb };
        const pattern = '**/{build,Debug,Release,Objects,bin,out,output,cmake-build-*,.pio}/**/*.{elf,axf,out}';
        const files = launchFiles.size > 1 ? [] : await vscode.workspace.findFiles(new vscode.RelativePattern(folder, pattern), '**/{.git,node_modules}/**', 3);
        if (revision !== this.optionsRevision) return this.firmware();
        return files.length === 1 ? { path: files[0].fsPath, gdb } : {
            path: '', gdb, message: files.length > 1 || launchFiles.size > 1 ?
                'Multiple ELF firmware files found. Choose the active firmware.' : 'No ELF firmware found. Choose a firmware file.',
        };
    }

    dispose(): void {
        if (this.viewTimer) clearImmediate(this.viewTimer);
        this.view = undefined;
        this.service.dispose();
        for (const disposable of this.disposables) disposable.dispose();
    }
}
