const { setTimeout: wait } = require('node:timers/promises');
const iconv = require('iconv-lite');

const MAX_PARAMETERS = 10;
const LINE_ENDINGS = new Map([
  ['', ''], ['none', ''], ['lf', '\n'], ['cr', '\r'], ['crlf', '\r\n'], ['lfcr', '\n\r'],
  ['\n', '\n'], ['\r', '\r'], ['\r\n', '\r\n'], ['\n\r', '\n\r'],
]);

function parseHex(text) {
  const compact = text.replace(/\s/g, '');
  if (compact.length % 2 !== 0 || /[^0-9a-f]/i.test(compact)) {
    throw new Error('Hex input must consist of complete hexadecimal bytes');
  }
  return Buffer.from(compact, 'hex');
}

function numericParameter(value) {
  if (typeof value === 'string' && value.trim()) value = Number(value);
  if (!Number.isFinite(value)) throw new Error('Command parameter must be a finite number');
  return value;
}

function hexParameter(value) {
  if (typeof value === 'string') return parseHex(value);
  value = numericParameter(value);
  if (!Number.isFinite(Math.fround(value))) throw new RangeError('Command parameter does not fit in float32');
  const bytes = Buffer.allocUnsafe(4);
  bytes.writeFloatLE(value);
  return bytes;
}

function encodeCommand({ text = '', hex = false, eol = 'none', encoding = 'utf8' }, params = []) {
  if (!Array.isArray(params) || params.length > MAX_PARAMETERS) {
    throw new RangeError(`A command supports up to ${MAX_PARAMETERS} parameters`);
  }
  const ending = LINE_ENDINGS.get(String(eol).toLowerCase());
  if (ending === undefined) throw new Error(`Unknown line ending: ${eol}`);

  let index = 0;
  const nextParameter = () => {
    if (index >= MAX_PARAMETERS) throw new RangeError(`A command supports up to ${MAX_PARAMETERS} parameters`);
    if (index >= params.length) throw new Error(`Missing command parameter ${index + 1}`);
    return params[index++];
  };

  if (hex) {
    const segments = text.split('%%');
    const bytes = [parseHex(segments[0])];
    for (const segment of segments.slice(1)) bytes.push(hexParameter(nextParameter()), parseHex(segment));
    bytes.push(Buffer.from(ending, 'ascii'));
    return Buffer.concat(bytes);
  }

  const output = text.replace(/%%|%(?:(\d*)\.(\d+))?([df])/g, (token, width, precision, type) => {
    if (token === '%%') return '%';
    const value = numericParameter(nextParameter());
    if (type === 'd') return Math.trunc(value).toString();
    const digits = precision === undefined ? 6 : Number(precision);
    if (digits > 100 || Number(width) > 100) throw new RangeError('Command format width and precision must be at most 100');
    return value.toFixed(digits).padStart(Number(width) || 0);
  });
  if (!iconv.encodingExists(encoding)) throw new Error(`Unknown text encoding: ${encoding}`);
  return iconv.encode(output + ending, encoding);
}

async function repeatSend(send, { count = 1, delayMs = 0, signal } = {}) {
  if (!Number.isSafeInteger(count)) throw new RangeError('Repeat count must be an integer');
  if (!Number.isFinite(delayMs) || delayMs < 0 || delayMs > 0x7fffffff) {
    throw new RangeError('Repeat delay must be between 0 and 2147483647 ms');
  }
  let sent = 0;
  while (!signal?.aborted && (count < 0 || sent < count)) {
    if (sent > 0) {
      try {
        await wait(delayMs, undefined, { signal });
      } catch (error) {
        if (error.name === 'AbortError' && signal?.aborted) break;
        throw error;
      }
      if (signal?.aborted) break;
    }
    await send();
    sent++;
  }
  return sent;
}

module.exports = { encodeCommand, repeatSend };
