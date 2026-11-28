const TAIL = Buffer.from([0x00, 0x00, 0x80, 0x7f]);
const LF = Buffer.from([0x0a]);
const CR = 0x0d;
const MAX_CHANNELS = 4096;
const MAX_LINE = 64 * 1024;
const MAX_IMAGE = 16 * 1024 * 1024;
const MAX_DIMENSION = 32768;
const MAX_IMAGE_PIXELS = 16 * 1024 * 1024;
const IMAGE_METADATA_SIZE = 5 * 4;
const DECIMAL = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;
const INTEGER = /^[+-]?\d+$/;

function imageShape(id, width, height, format) {
  if (![id, width, height, format].every(Number.isInteger)
    || id < 0 || id > 0x7fffffff || format < 1 || format > 34) return false;

  const validDimension = (size) => (size === -1 && format >= 25) || (size > 0 && size <= MAX_DIMENSION);
  return validDimension(width) && validDimension(height)
    && (width === -1 || height === -1 || width * height <= MAX_IMAGE_PIXELS);
}

function validImage(fields) {
  const [id, size, width, height, format] = fields;
  return imageShape(id, width, height, format) && Number.isInteger(size) && size > 0 && size <= MAX_IMAGE;
}

function createDecoder(engine, { expectedChannels } = {}) {
  if (engine === 'rawdata') {
    return {
      push: (bytes) => bytes.length ? [{ kind: 'raw', bytes: Buffer.from(bytes) }] : [],
      flush: () => [],
    };
  }
  if (engine !== 'justfloat' && engine !== 'firewater') throw new Error(`Unknown data engine: ${engine}`);
  if (expectedChannels === 0) expectedChannels = undefined;
  if (expectedChannels !== undefined
    && (!Number.isInteger(expectedChannels) || expectedChannels < 1 || expectedChannels > MAX_CHANNELS)) {
    throw new RangeError(`Expected channel count must be between 1 and ${MAX_CHANNELS}`);
  }

  const binary = engine === 'justfloat';
  const delimiter = binary ? TAIL : LF;
  const limit = binary ? MAX_CHANNELS * 4 : MAX_LINE;
  let buffer = Buffer.allocUnsafe(1024);
  let start = 0;
  let end = 0;
  let search = 0;
  let discard = false;
  let mayHaveCR = false;
  let image;

  function append(bytes) {
    if (end + bytes.length > buffer.length) {
      const required = end - start + bytes.length;
      const next = required > buffer.length ? Buffer.allocUnsafe(Math.max(required, buffer.length * 2)) : buffer;
      buffer.copy(next, 0, start, end);
      buffer = next;
      search -= start;
      end -= start;
      start = 0;
    }
    bytes.copy(buffer, end);
    end += bytes.length;
  }

  function advance(length) {
    start += length;
    search = start;
  }

  function take(length) {
    const bytes = Buffer.from(buffer.subarray(start, start + length));
    advance(length);
    return bytes;
  }

  function startImage(headerLength, [id, size, width, height, format]) {
    const length = headerLength + size;
    const bytes = Buffer.allocUnsafe(Math.min(length, Math.max(headerLength, 4096)));
    buffer.copy(bytes, 0, start, start + headerLength);
    advance(headerLength);
    image = { id, width, height, format, headerLength, length, bytes, filled: headerLength };
  }

  function readImage(flushing) {
    const count = Math.min(end - start, image.length - image.filled);
    if (image.filled + count > image.bytes.length) {
      const capacity = Math.min(image.length, Math.max(image.filled + count, image.bytes.length * 2));
      const bytes = Buffer.allocUnsafe(capacity);
      image.bytes.copy(bytes, 0, 0, image.filled);
      image.bytes = bytes;
    }
    buffer.copy(image.bytes, image.filled, start, start + count);
    image.filled += count;
    advance(count);
    if (image.filled < image.length && !flushing) return;

    const { id, width, height, format, headerLength, filled, length } = image;
    const bytes = image.bytes.subarray(0, filled);
    image = undefined;
    if (filled < length) return { kind: 'invalid', bytes };
    return { kind: 'image', id, width, height, format, data: bytes.subarray(headerLength), bytes };
  }

  function validCount(count) {
    return count > 0 && count <= MAX_CHANNELS && (expectedChannels === undefined || count === expectedChannels);
  }

  function readJustFloat(at, flushing) {
    const payloadLength = at - start;
    const length = payloadLength + TAIL.length;
    if (payloadLength === IMAGE_METADATA_SIZE) {
      const fields = Array.from({ length: 5 }, (_, index) => buffer.readInt32LE(start + index * 4));
      const [id, , width, height, format] = fields;
      if (imageShape(id, width, height, format)) {
        // Only metadata-shaped samples need lookahead for the optional second marker.
        const next = start + length;
        const prefix = buffer.subarray(next, Math.min(end, next + TAIL.length));
        if (prefix.equals(TAIL.subarray(0, prefix.length))) {
          if (prefix.length === TAIL.length) {
            if (!validImage(fields)) return { kind: 'invalid', bytes: take(length + TAIL.length) };
            startImage(length + TAIL.length, fields);
            return readImage(flushing);
          }
          if (!flushing) return;
        }
      }
    }

    const bytes = take(length);
    const count = payloadLength / 4;
    if (!Number.isInteger(count) || !validCount(count)) return { kind: 'invalid', bytes };
    const values = Array.from({ length: count }, (_, index) => bytes.readFloatLE(index * 4));
    if (!values.every(Number.isFinite)) return { kind: 'invalid', bytes };
    return { kind: 'sample', values, bytes };
  }

  function readFireWater(at, flushing) {
    const crlf = at > start && buffer[at - 1] === CR;
    const lfcr = !crlf && at + 1 < end && buffer[at + 1] === CR;
    const possibleCR = !crlf && at + 1 === end;
    const length = at - start + (lfcr ? 2 : 1);
    const line = buffer.toString('utf8', start, crlf ? at - 1 : at).trim();
    const colon = line.lastIndexOf(':');
    const prefix = line.slice(line.lastIndexOf(':', colon - 1) + 1, colon).trim();
    const fields = line.slice(colon + 1).split(',').map((field) => field.trim());

    if (colon >= 0 && prefix === 'image') {
      if (fields.length !== 5 || !fields.every((field) => INTEGER.test(field)) || !validImage(fields.map(Number))) {
        mayHaveCR = possibleCR;
        return { kind: 'invalid', bytes: take(length) };
      }
      if (possibleCR && !flushing) return;
      startImage(length, fields.map(Number));
      return readImage(flushing);
    }

    const bytes = take(length);
    mayHaveCR = possibleCR;
    if (!fields.every((field) => DECIMAL.test(field))) return { kind: 'raw', bytes };
    const values = fields.map(Number);
    if (!validCount(values.length) || !values.every(Number.isFinite)) return { kind: 'invalid', bytes };
    return { kind: 'sample', values, bytes };
  }

  function read(flushing) {
    if (image) return readImage(flushing);
    if (start === end) return;
    if (mayHaveCR) {
      mayHaveCR = false;
      // A split LFCR must not delay an ordinary LF-terminated sample.
      if (buffer[start] === CR) return { kind: 'raw', bytes: take(1) };
    }

    const at = buffer.subarray(0, end).indexOf(delimiter, search);
    if (at < 0) {
      if (flushing) return { kind: binary || discard ? 'invalid' : 'raw', bytes: take(end - start) };
      const scanned = end - start - delimiter.length + 1;
      if (scanned > limit) {
        discard = true;
        return { kind: 'invalid', bytes: take(scanned) };
      }
      search = Math.max(start, start + scanned);
      return;
    }
    search = at;
    if (discard || at - start > limit) {
      discard = false;
      return { kind: 'invalid', bytes: take(at - start + delimiter.length) };
    }
    const expectedTail = start + expectedChannels * 4;
    if (binary && (at - start) % 4 !== 0 && at < expectedTail) {
      // A marker spanning two floats can occur before the known frame boundary.
      if (!flushing && end < expectedTail + TAIL.length) return;
      if (end >= expectedTail + TAIL.length
        && buffer.subarray(expectedTail, expectedTail + TAIL.length).equals(TAIL)) {
        return readJustFloat(expectedTail, flushing);
      }
    }
    return binary ? readJustFloat(at, flushing) : readFireWater(at, flushing);
  }

  function drain(frames, flushing = false) {
    let frame;
    while ((frame = read(flushing))) frames.push(frame);
  }

  return {
    push(bytes) {
      const frames = [];
      for (let offset = 0; offset < bytes.length; offset += MAX_LINE) {
        append(bytes.subarray(offset, offset + MAX_LINE));
        drain(frames);
      }
      return frames;
    },
    flush() {
      const frames = [];
      drain(frames, true);
      discard = false;
      mayHaveCR = false;
      return frames;
    },
  };
}

function encodeSample(engine, values) {
  if (!values.length || values.length > MAX_CHANNELS || !Array.from(values).every(Number.isFinite)) {
    throw new RangeError(`Sample requires 1–${MAX_CHANNELS} finite values`);
  }
  if (engine === 'firewater' || engine === 'rawdata') {
    const bytes = Buffer.from(`${Array.from(values).join(',')}\n`);
    if (engine === 'firewater' && bytes.length - 1 > MAX_LINE) throw new RangeError('FireWater sample is too long');
    return bytes;
  }
  if (engine !== 'justfloat') throw new Error(`Unknown data engine: ${engine}`);
  const bytes = Buffer.allocUnsafe(values.length * 4 + TAIL.length);
  for (let index = 0; index < values.length; index++) {
    if (!Number.isFinite(Math.fround(values[index]))) throw new RangeError('Sample value does not fit in float32');
    bytes.writeFloatLE(values[index], index * 4);
  }
  TAIL.copy(bytes, values.length * 4);
  return bytes;
}

function encodeImage(engine, { id, width, height, format, data }) {
  const fields = [id, data.length, width, height, format];
  if (!validImage(fields)) throw new RangeError('Invalid image metadata or payload length');
  if (engine === 'firewater') {
    // CRLF makes the first payload byte unambiguous, including a leading CR.
    return Buffer.concat([Buffer.from(`image:${fields.join(',')}\r\n`), data]);
  }
  if (engine !== 'justfloat') throw new Error(`Image frames are unavailable for ${engine}`);
  const header = Buffer.allocUnsafe(IMAGE_METADATA_SIZE + TAIL.length * 2);
  fields.forEach((field, index) => header.writeInt32LE(field, index * 4));
  TAIL.copy(header, IMAGE_METADATA_SIZE);
  TAIL.copy(header, IMAGE_METADATA_SIZE + TAIL.length);
  return Buffer.concat([header, data]);
}

module.exports = { createDecoder, encodeSample, encodeImage };
