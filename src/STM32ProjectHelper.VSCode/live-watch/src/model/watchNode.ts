/**
 * Display format for watch values
 */
export enum DisplayFormat {
    Auto = 'auto',
    Hex = 'hex',
    Decimal = 'decimal',
    Float = 'float',
    Binary = 'binary',
}

/**
 * Represents a single node in the watch tree.
 * Root nodes are top-level watched variables.
 * Children are struct fields, array elements, etc.
 */
export interface WatchNode {
    id: string;
    name: string;            // Display name: "kp_", "[0]", etc.
    expression: string;      // Full GDB expression for symbol resolution
    typeName: string;        // Type string: "float", "struct PidConfig", etc.
    address: number;         // Absolute memory address
    size: number;            // Size in bytes
    children: WatchNode[];
    value: string;           // Formatted display value
    rawValue: number;        // Raw 32-bit word (for types <= 4 bytes)
    hasRawValue: boolean;    // True when rawValue is populated from target memory
    displayFormat: DisplayFormat;
    expanded: boolean;
    isRoot: boolean;
    changed: boolean;        // Value changed since last poll
    error?: string;          // Error message if resolution failed
    enumValueNames?: Record<string, string>; // key: integer value (string), value: enum label
    pointerDeref?: boolean;  // Synthetic child node for one-level pointer dereference
    pointerDerefDepth?: number; // Auto-deref nesting depth for pointer chains
    pointerCompositePending?: boolean; // Composite pointer deref layout not resolved yet
    relativeAddress?: number; // Offset relative to pointer target base (for dynamic rebasing)
    pointerBaseExpr?: string | null; // Cached pointer expression; null means no runtime base
    pointerStorageAddress?: number; // Address where pointer value is stored
    pointerBaseType?: string; // Cached pointer type name
}

/**
 * Serializable form sent to the WebView as flattened rows.
 */
export interface ViewRow {
    id: string;
    depth: number;
    name: string;
    value: string;
    address: string;
    typeName: string;
    hasChildren: boolean;
    expanded: boolean;
    changed: boolean;
    isInput: boolean;        // Last row — the add-variable input
    displayFormat: string;
    error?: string;
}

/**
 * Create a new empty WatchNode
 */
export function createWatchNode(
    name: string,
    expression: string,
    typeName: string,
    address: number,
    size: number,
    isRoot: boolean = false,
): WatchNode {
    return {
        id: isRoot ? name : `${expression}`,
        name,
        expression,
        typeName,
        address,
        size,
        children: [],
        value: '',
        rawValue: 0,
        hasRawValue: false,
        displayFormat: DisplayFormat.Auto,
        expanded: isRoot,
        isRoot,
        changed: false,
    };
}

/**
 * Flatten a WatchNode tree into ViewRows for the WebView,
 * respecting expanded/collapsed state.
 */
export function flattenTree(nodes: WatchNode[], depth: number = 0): ViewRow[] {
    const rows: ViewRow[] = [];
    for (const node of nodes) {
        rows.push({
            id: node.id,
            depth,
            name: node.name,
            value: node.error ? `<${node.error}>` : node.value,
            address: node.address ? `0x${node.address.toString(16).toUpperCase().padStart(8, '0')}` : '',
            typeName: node.typeName,
            hasChildren: node.children.length > 0,
            expanded: node.expanded,
            changed: node.changed,
            isInput: false,
            displayFormat: node.displayFormat,
            error: node.error,
        });
        if (node.expanded && node.children.length > 0) {
            rows.push(...flattenTree(node.children, depth + 1));
        }
    }
    return rows;
}

// ── Value Formatting ────────────────────────────────────────────

const buf = new ArrayBuffer(4);
const f32 = new Float32Array(buf);
const u32 = new Uint32Array(buf);
const i32 = new Int32Array(buf);
const u8 = new Uint8Array(buf);
const dv = new DataView(buf);

/**
 * Format a raw 32-bit word according to the node's type and display format.
 * Bytes are little-endian as read from the MCU.
 */
export function formatValue(node: WatchNode, rawWord: number): string {
    const fmt = node.displayFormat;
    const type = valueType(node.typeName);

    // Struct / class / union → no value for parent, only children.
    // Exception: pointer nodes may have children for deref-preview, but still
    // should display their own pointer value.
    if (node.children.length > 0 && !type.includes('[') && !type.includes('*')) {
        return '';
    }

    // Force-hex display
    if (fmt === DisplayFormat.Hex) {
        return '0x' + (rawWord >>> 0).toString(16).toUpperCase().padStart(node.size * 2, '0');
    }
    // Force-binary display
    if (fmt === DisplayFormat.Binary) {
        return '0b' + (rawWord >>> 0).toString(2).padStart(node.size * 8, '0');
    }

    // Force-float
    if (fmt === DisplayFormat.Float) {
        u32[0] = rawWord;
        return f32[0].toPrecision(6);
    }

    // Force-decimal
    if (fmt === DisplayFormat.Decimal) {
        if (type.startsWith('u') || type === 'bool' || type.includes('unsigned')) {
            return (rawWord >>> 0).toString();
        }
        i32[0] = rawWord;
        return i32[0].toString();
    }

    // ── Auto format based on type ──
    // float
    if (type === 'float') {
        u32[0] = rawWord;
        return f32[0].toPrecision(6);
    }
    // double (only lower 32 bits shown — need 8-byte read for full precision)
    if (type === 'double') {
        return '0x' + (rawWord >>> 0).toString(16).toUpperCase().padStart(8, '0') + ' (partial)';
    }
    // bool
    if (type === 'bool' || type === '_bool') {
        return (rawWord & 0xFF) ? 'true' : 'false';
    }
    // Pointer
    if (type.includes('*')) {
        return '0x' + (rawWord >>> 0).toString(16).toUpperCase().padStart(8, '0');
    }
    // uint8_t
    if (type === 'uint8_t' || type === 'unsigned char' || type === 'char') {
        return (rawWord & 0xFF).toString();
    }
    // int8_t / signed char
    if (type === 'int8_t' || type === 'signed char') {
        const v = rawWord & 0xFF;
        return (v > 127 ? v - 256 : v).toString();
    }
    // uint16_t
    if (type === 'uint16_t' || type === 'unsigned short') {
        return (rawWord & 0xFFFF).toString();
    }
    // int16_t
    if (type === 'int16_t' || type === 'short' || type === 'signed short') {
        const v = rawWord & 0xFFFF;
        return (v > 32767 ? v - 65536 : v).toString();
    }
    // uint32_t / flags → default hex
    if (type === 'uint32_t' || type === 'unsigned int' || type === 'unsigned long') {
        return (rawWord >>> 0).toString();
    }
    // int32_t
    if (type === 'int32_t' || type === 'int' || type === 'long' || type === 'signed int') {
        i32[0] = rawWord;
        return i32[0].toString();
    }
    // enum (show as integer)
    if (type.startsWith('enum')) {
        if (node.enumValueNames) {
            const unsigned = rawWord >>> 0;
            const unsignedKey = unsigned.toString();
            const byUnsigned = node.enumValueNames[unsignedKey];
            if (byUnsigned) {
                return `${byUnsigned} (${unsigned})`;
            }

            i32[0] = rawWord;
            const signed = i32[0];
            const bySigned = node.enumValueNames[signed.toString()];
            if (bySigned) {
                return `${bySigned} (${signed})`;
            }
        }
        return (rawWord >>> 0).toString();
    }

    // Fallback: hex
    return '0x' + (rawWord >>> 0).toString(16).toUpperCase().padStart(8, '0');
}

/**
 * Parse a user-entered string into a raw 32-bit value suitable for writing back.
 * Supports: "15.0" (float), "0x1234" (hex), "123" (decimal), "true"/"false".
 */
function valueType(typeName: string): string {
    return typeName.toLowerCase().replace(/\b(?:const|volatile|restrict)\b/g, '').replace(/\s+/g, ' ').trim();
}

export function parseWriteValue(input: string, typeName: string, size = 4): number | null {
    const s = input.trim();
    const type = valueType(typeName);

    if (s === '') { return null; }

    // Boolean
    if (type === 'bool' || type === '_bool') {
        if (s === 'true' || s === '1') { return 1; }
        if (s === 'false' || s === '0') { return 0; }
        return null;
    }

    // Float
    if (type === 'float') {
        const fv = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(s) ? Number(s) : NaN;
        if (!Number.isFinite(Math.fround(fv))) return null;
        f32[0] = fv;
        return u32[0];
    }

    if (!/^(?:[+-]?\d+|0x[0-9a-f]+|0b[01]+)$/i.test(s)) return null;
    const v = Number(s);
    return Number.isSafeInteger(v) && v >= -(2 ** (size * 8 - 1)) && v < 2 ** (size * 8) ? v : null;
}
