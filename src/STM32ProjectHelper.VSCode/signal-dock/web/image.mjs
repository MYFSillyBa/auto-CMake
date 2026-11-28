const numericFormats = {
  1: 'binary',
  2: 'binary-lsb',
  3: 'gray8',
  4: 'rgb32',
  5: 'argb32',
  6: 'argb32-premultiplied',
  7: 'rgb565',
  8: 'argb8565-premultiplied',
  9: 'rgb666',
  10: 'argb6666-premultiplied',
  11: 'rgb555',
  12: 'argb8555-premultiplied',
  13: 'rgb888',
  14: 'rgb444',
  15: 'argb4444-premultiplied',
  16: 'rgbx8888',
  17: 'rgba8888',
  18: 'rgba8888-premultiplied',
  19: 'bgr30',
  20: 'a2bgr30-premultiplied',
  21: 'rgb30',
  22: 'a2rgb30-premultiplied',
  23: 'alpha8',
  24: 'gray8',
};

const pixelSizes = {
  gray8: 1, alpha8: 1, gray16: 2, rgb565: 2, rgb555: 2, rgb444: 2, rgb888: 3,
  bgr888: 3, rgb32: 4, argb32: 4, 'argb32-premultiplied': 4,
  rgbx8888: 4, rgba8888: 4, 'rgba8888-premultiplied': 4,
};

// Packed Qt pixels use native little-endian 16/32-bit words and byte-ordered 24-bit words.
// Each pair specifies the shift and maximum of an R, G, B, or A component.
const packedFormats = {
  rgb565: [2, 11, 31, 5, 63, 0, 31],
  'argb8565-premultiplied': [3, 19, 31, 13, 63, 8, 31, 0, 255],
  rgb666: [3, 12, 63, 6, 63, 0, 63],
  'argb6666-premultiplied': [3, 12, 63, 6, 63, 0, 63, 18, 63],
  rgb555: [2, 10, 31, 5, 31, 0, 31],
  'argb8555-premultiplied': [3, 18, 31, 13, 31, 8, 31, 0, 255],
  rgb444: [2, 8, 15, 4, 15, 0, 15],
  'argb4444-premultiplied': [2, 8, 15, 4, 15, 0, 15, 12, 15],
  bgr30: [4, 0, 1023, 10, 1023, 20, 1023],
  'a2bgr30-premultiplied': [4, 0, 1023, 10, 1023, 20, 1023, 30, 3],
  rgb30: [4, 20, 1023, 10, 1023, 0, 1023],
  'a2rgb30-premultiplied': [4, 20, 1023, 10, 1023, 0, 1023, 30, 3],
};

const maxImagePixels = 16_777_216;

function imagePixels(width, height) {
  const pixels = width * height;
  if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width < 1 || height < 1 || pixels > maxImagePixels) {
    throw new Error('图像尺寸无效或超过 16777216 像素');
  }
  return pixels;
}

export function rawToRgba(bytes, width, height, format) {
  format = Object.hasOwn(numericFormats, format) ? numericFormats[format] : String(format).toLowerCase();
  const bitPacked = format === 'binary' || format === 'binary-lsb';
  const packed = Object.hasOwn(packedFormats, format) ? packedFormats[format] : null;
  const pixelSize = packed?.[0] ?? (Object.hasOwn(pixelSizes, format) ? pixelSizes[format] : 0);
  if (!bitPacked && !pixelSize) throw new Error('不支持图像格式 ' + format);
  const pixels = imagePixels(width, height);
  const row = bitPacked ? Math.ceil(width / 8) : width * pixelSize;
  const stride = bytes.length === row * height ? row : Math.ceil(row / 4) * 4;
  if (bytes.length !== stride * height) throw new Error('图像字节数与宽高、格式不匹配');
  const rgba = new Uint8ClampedArray(pixels * 4);
  const premultiplied = format.endsWith('-premultiplied');
  let to = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const from = y * stride + (bitPacked ? x >>> 3 : x * pixelSize);
      let r = 0;
      let g = 0;
      let b = 0;
      let a = 255;
      const first = bytes[from];
      const second = bytes[from + 1];
      const third = bytes[from + 2];
      const fourth = bytes[from + 3];
      if (packed) {
        const value = pixelSize === 3 ? (first << 16) | (second << 8) | third
          : first | (second << 8) | (pixelSize === 4 ? (third << 16) | (fourth << 24) : 0);
        r = ((value >>> packed[1]) & packed[2]) * 255 / packed[2];
        g = ((value >>> packed[3]) & packed[4]) * 255 / packed[4];
        b = ((value >>> packed[5]) & packed[6]) * 255 / packed[6];
        if (packed[8]) a = ((value >>> packed[7]) & packed[8]) * 255 / packed[8];
      } else {
        switch (format) {
          case 'binary': r = g = b = ((first >> (7 - (x & 7))) & 1) * 255; break;
          case 'binary-lsb': r = g = b = ((first >> (x & 7)) & 1) * 255; break;
          case 'gray8': r = g = b = first; break;
          case 'gray16': r = g = b = ((second << 8) | first) / 257; break;
          case 'alpha8': r = g = b = 255; a = first; break;
          case 'argb32':
          case 'argb32-premultiplied': a = fourth; // fall through
          case 'rgb32':
          case 'bgr888': r = third; g = second; b = first; break;
          case 'rgba8888':
          case 'rgba8888-premultiplied': a = fourth; // fall through
          default: r = first; g = second; b = third;
        }
      }
      const scale = premultiplied ? (a > 0 ? 255 / a : 0) : 1;
      rgba[to++] = r * scale;
      rgba[to++] = g * scale;
      rgba[to++] = b * scale;
      rgba[to++] = a;
    }
  }
  return rgba;
}

function spatialFilter(rgba, width, height, filter) {
  const output = new Uint8ClampedArray(rgba.length);
  const sobel = filter === 'sobel';
  const luma = sobel ? Float32Array.from({ length: width * height }, (_, i) =>
    rgba[i * 4] * 0.2126 + rgba[i * 4 + 1] * 0.7152 + rgba[i * 4 + 2] * 0.0722) : null;
  for (let y = 0; y < height; y++) {
    const top = Math.max(0, y - 1) * width;
    const row = y * width;
    const bottom = Math.min(height - 1, y + 1) * width;
    for (let x = 0; x < width; x++) {
      const left = Math.max(0, x - 1);
      const right = Math.min(width - 1, x + 1);
      const i = (row + x) * 4;
      if (sobel) {
        const dx = luma[top + right] - luma[top + left] + 2 * (luma[row + right] - luma[row + left]) + luma[bottom + right] - luma[bottom + left];
        const dy = luma[bottom + left] - luma[top + left] + 2 * (luma[bottom + x] - luma[top + x]) + luma[bottom + right] - luma[top + right];
        output[i] = output[i + 1] = output[i + 2] = Math.sqrt(dx * dx + dy * dy);
      } else {
        for (let c = 0; c < 3; c++) {
          const edges = rgba[(top + x) * 4 + c] + rgba[(bottom + x) * 4 + c] + rgba[(row + left) * 4 + c] + rgba[(row + right) * 4 + c];
          if (filter === 'sharpen') {
            output[i + c] = 5 * rgba[i + c] - edges;
          } else {
            const corners = rgba[(top + left) * 4 + c] + rgba[(top + right) * 4 + c] + rgba[(bottom + left) * 4 + c] + rgba[(bottom + right) * 4 + c];
            output[i + c] = (corners + 2 * edges + 4 * rgba[i + c]) / 16;
          }
        }
      }
      output[i + 3] = rgba[i + 3];
    }
  }
  return output;
}

export function filterRgba(rgba, filter, threshold = 128, width, height) {
  if (filter === 'none' || !filter) return rgba;
  if (filter === 'sobel' || filter === 'blur' || filter === 'sharpen') {
    return width * height * 4 === rgba.length ? spatialFilter(rgba, width, height, filter) : rgba;
  }
  const output = new Uint8ClampedArray(rgba.length);
  for (let i = 0; i < rgba.length; i += 4) {
    if (filter === 'invert') {
      output[i] = 255 - rgba[i];
      output[i + 1] = 255 - rgba[i + 1];
      output[i + 2] = 255 - rgba[i + 2];
    } else {
      const luma = rgba[i] * 0.2126 + rgba[i + 1] * 0.7152 + rgba[i + 2] * 0.0722;
      output[i] = output[i + 1] = output[i + 2] = filter === 'threshold' ? (luma >= threshold ? 255 : 0) : luma;
    }
    output[i + 3] = rgba[i + 3];
  }
  return output;
}

export function decodeBase64(base64) {
  if (typeof Uint8Array.fromBase64 === 'function') return Uint8Array.fromBase64(base64);
  const decoded = atob(base64);
  const bytes = new Uint8Array(decoded.length);
  for (let i = 0; i < decoded.length; i++) bytes[i] = decoded.charCodeAt(i);
  return bytes;
}

function encodedType(bytes) {
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return 'image/jpeg';
  if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) return 'image/png';
  if (bytes[0] === 0x42 && bytes[1] === 0x4d) return 'image/bmp';
  if (bytes[0] === 0x47 && bytes[1] === 0x49 && bytes[2] === 0x46) return 'image/gif';
  if (bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[8] === 0x57 && bytes[9] === 0x45 && bytes[10] === 0x42 && bytes[11] === 0x50) return 'image/webp';
  return '';
}

function decodePnm(bytes) {
  let position = 0;
  function token() {
    while (position < bytes.length) {
      if (bytes[position] === 35) {
        while (position < bytes.length && bytes[position] !== 10 && bytes[position] !== 13) position++;
      } else if (bytes[position] <= 32) position++;
      else break;
    }
    const from = position;
    while (position < bytes.length && bytes[position] > 32 && bytes[position] !== 35) position++;
    if (position - from > 16) throw new Error('PNM 数据无效');
    return String.fromCharCode(...bytes.subarray(from, position));
  }
  const magic = token();
  if (!/^P[1-6]$/.test(magic)) throw new Error('PNM 格式头无效');
  const kind = Number(magic[1]);
  const width = Number(token());
  const height = Number(token());
  const pixels = imagePixels(width, height);
  const maxValue = kind === 1 || kind === 4 ? 1 : Number(token());
  if (!Number.isInteger(maxValue) || maxValue < 1 || maxValue > 65535) throw new Error('PNM 颜色深度无效');
  if (kind >= 4) {
    if (position >= bytes.length || bytes[position] > 32) throw new Error('PNM 数据缺失');
    position += bytes[position] === 13 && bytes[position + 1] === 10 ? 2 : 1;
  }
  if (kind === 4) {
    const row = Math.ceil(width / 8);
    if (bytes.length - position < row * height) throw new Error('PBM 数据缺失');
    const rgba = rawToRgba(bytes.subarray(position, position + row * height), width, height, 'binary');
    return { width, height, rgba: filterRgba(rgba, 'invert') };
  }
  const channels = kind === 3 || kind === 6 ? 3 : 1;
  const word = maxValue > 255 ? 2 : 1;
  if (kind >= 5 && bytes.length - position < pixels * channels * word) throw new Error('PNM 像素数据缺失');
  function sample() {
    if (kind < 4) {
      const next = token();
      if (!/^\d+$/.test(next)) throw new Error('PNM 像素数据无效');
      return kind === 1 ? 1 - Number(next) : Number(next);
    }
    return word === 2 ? (bytes[position++] << 8) | bytes[position++] : bytes[position++];
  }
  const rgba = new Uint8ClampedArray(pixels * 4);
  for (let i = 0; i < rgba.length; i += 4) {
    const first = sample();
    rgba[i] = first * 255 / maxValue;
    rgba[i + 1] = (channels === 3 ? sample() : first) * 255 / maxValue;
    rgba[i + 2] = (channels === 3 ? sample() : first) * 255 / maxValue;
    rgba[i + 3] = 255;
  }
  return { width, height, rgba };
}

function decodeXbm(text) {
  const width = Number(/#define\s+\w+_width\s+(\d+)/.exec(text)?.[1]);
  const height = Number(/#define\s+\w+_height\s+(\d+)/.exec(text)?.[1]);
  imagePixels(width, height);
  const payload = /\{([^}]+)\}/.exec(text)?.[1];
  if (!payload) throw new Error('XBM 数据缺失');
  const words = /\bshort\b[^{}]*\{/.test(text);
  const row = Math.ceil(width / 8);
  const storedRow = words ? Math.ceil(row / 2) * 2 : row;
  const bytes = new Uint8Array(row * height);
  let position = 0;
  for (const [token] of payload.matchAll(/0x[0-9a-f]+|\d+/gi)) {
    const value = Number(token);
    const length = words ? 2 : 1;
    for (let part = 0; part < length && position < storedRow * height; part++, position++) {
      const column = position % storedRow;
      if (column < row) bytes[Math.floor(position / storedRow) * row + column] = (value >>> (part * 8)) & 255;
    }
    if (position >= storedRow * height) break;
  }
  if (position < storedRow * height) throw new Error('XBM 数据缺失');
  return { width, height, rgba: filterRgba(rawToRgba(bytes, width, height, 'binary-lsb'), 'invert') };
}

function xpmColor(value, context) {
  const hex = /^#([0-9a-f]+)$/i.exec(value)?.[1];
  if (hex && [3, 6, 9, 12].includes(hex.length)) {
    const width = hex.length / 3;
    const scale = 255 / (16 ** width - 1);
    return [0, 1, 2].map(i => parseInt(hex.slice(i * width, (i + 1) * width), 16) * scale).concat(255);
  }
  if (/^none$/i.test(value)) return [0, 0, 0, 0];
  const gray = /^gr[ae]y(\d+)$/i.exec(value)?.[1];
  if (gray !== undefined) return [1, 1, 1].map(() => Number(gray) * 2.55).concat(255);
  if (!context) return [0, 0, 0, 255];
  context.clearRect(0, 0, 1, 1);
  context.fillStyle = '#000000';
  context.fillStyle = value.replaceAll(/\s+/g, '');
  context.fillRect(0, 0, 1, 1);
  return context.getImageData(0, 0, 1, 1).data;
}

function decodeXpm(text) {
  const rows = text.startsWith('! XPM2')
    ? text.split(/\r?\n/).slice(1)
    : Array.from(text.matchAll(/"((?:[^"\\]|\\.)*)"/g), ([, line]) => line.replaceAll(/\\(["\\])/g, '$1'));
  const [width, height, colors, chars] = rows[0]?.trim().split(/\s+/).map(Number) ?? [];
  const pixels = imagePixels(width, height);
  if (!Number.isInteger(colors) || colors < 1 || !Number.isInteger(chars) || chars < 1 || chars > 16 || rows.length < 1 + colors + height) {
    throw new Error('XPM 调色板或像素数据无效');
  }
  const palette = new Map();
  const canvas = globalThis.document?.createElement('canvas');
  if (canvas) canvas.width = canvas.height = 1;
  const ctx = canvas?.getContext('2d', { willReadFrequently: true });
  for (let i = 1; i <= colors; i++) {
    const row = rows[i];
    const attributes = row.slice(chars);
    const color = /\s+c\s+(.+?)(?:\s+(?:g4|g|m|s)\s+|$)/.exec(attributes)?.[1].trim()
      ?? /\s+(?:g4|g|m)\s+(.+?)\s*$/.exec(attributes)?.[1].trim() ?? 'black';
    palette.set(row.slice(0, chars), xpmColor(color, ctx));
  }
  const rgba = new Uint8ClampedArray(pixels * 4);
  let to = 0;
  for (let y = 0; y < height; y++) {
    const row = rows[y + 1 + colors];
    for (let x = 0; x < width; x++) {
      const color = palette.get(row.slice(x * chars, (x + 1) * chars));
      if (!color) throw new Error('XPM 像素引用无效');
      rgba.set(color, to);
      to += 4;
    }
  }
  return { width, height, rgba };
}

export function decodePortableImage(bytes, format) {
  if (Number(format) >= 29 && Number(format) <= 31) return decodePnm(bytes);
  if (Number(format) === 32 || Number(format) === 33) {
    const text = new TextDecoder().decode(bytes);
    return Number(format) === 32 ? decodeXbm(text) : decodeXpm(text);
  }
  return null;
}

async function loadBitmap(bytes, mime) {
  const blob = new Blob([bytes], { type: mime });
  if (mime !== 'image/svg+xml') return createImageBitmap(blob);
  const url = URL.createObjectURL(blob);
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    return img;
  } finally {
    URL.revokeObjectURL(url);
  }
}

async function drawFrame(canvas, ctx, frame, settings, keep) {
  const bytes = decodeBase64(frame.data.replace(/^data:[^,]+,/, ''));
  const automatic = settings.format === 'auto' || !settings.format;
  const format = automatic ? frame.format : settings.format;
  const id = Number(format);
  const mime = id === 34 ? 'image/svg+xml' : (id >= 25 || !id && (automatic || format === 'encoded')) ? encodedType(bytes) : '';
  if (mime) {
    const bitmap = await loadBitmap(bytes, mime);
    try {
      if (!keep()) return;
      imagePixels(bitmap.width, bitmap.height);
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      ctx.drawImage(bitmap, 0, 0);
      if (settings.filter && settings.filter !== 'none') {
        const image = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
        image.data.set(filterRgba(image.data, settings.filter, settings.threshold, bitmap.width, bitmap.height));
        ctx.putImageData(image, 0, 0);
      }
    } finally {
      bitmap.close?.();
    }
  } else if (keep()) {
    const decoded = decodePortableImage(bytes, format);
    const { width, height, rgba } = decoded ?? { ...frame, rgba: rawToRgba(bytes, frame.width, frame.height, format) };
    canvas.width = width;
    canvas.height = height;
    ctx.putImageData(new ImageData(filterRgba(rgba, settings.filter, settings.threshold, width, height), width, height), 0, 0);
  }
}

export function mountImage(container, config) {
  const canvas = document.createElement('canvas');
  canvas.className = 'telemetry-image';
  canvas.setAttribute('aria-label', '图像通道');
  canvas.hidden = true;
  const caption = document.createElement('span');
  caption.className = 'telemetry-image-caption';
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  container.append(canvas, caption);
  let latest;
  let queued;
  let running = false;
  let disposed = false;

  async function draw() {
    running = true;
    try {
      while (queued && !disposed) {
        const job = queued;
        queued = undefined;
        const keep = () => !disposed && latest?.frame?.id === job.frame.id && latest?.key === job.key;
        try {
          await drawFrame(canvas, ctx, job.frame, job.settings, keep);
          if (keep()) {
            canvas.hidden = false;
            caption.textContent = '#' + job.frame.id + ' · ' + canvas.width + ' × ' + canvas.height;
          }
        } catch (error) {
          if (keep()) {
            canvas.hidden = true;
            caption.textContent = error.message;
          }
        }
      }
    } finally {
      running = false;
    }
  }

  return {
    update(data) {
      const frame = data.images?.[config.imageId ?? 0];
      const key = [config.format, config.filter, config.threshold].join('/');
      canvas.style.objectFit = config.fit === 'stretch' ? 'fill' : config.fit || 'contain';
      canvas.style.transform = 'scale(' + (config.flipX ? -1 : 1) + ',' + (config.flipY ? -1 : 1) + ')';
      canvas.style.imageRendering = config.smoothImage === false ? 'pixelated' : 'auto';
      if (!frame) {
        latest = undefined;
        queued = undefined;
        canvas.hidden = true;
        caption.textContent = '等待图像 #' + (config.imageId ?? 0);
        return;
      }
      if (latest?.frame?.id === frame.id && latest.frame.seq === frame.seq && latest.frame.data === frame.data && latest.key === key) return;
      const settings = { format: config.format, filter: config.filter, threshold: config.threshold };
      queued = latest = { frame, key, settings };
      if (!running) void draw();
    },
    dispose() {
      disposed = true;
      queued = undefined;
      container.replaceChildren();
    },
  };
}
