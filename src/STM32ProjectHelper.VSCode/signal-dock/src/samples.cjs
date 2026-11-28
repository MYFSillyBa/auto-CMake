const { createWriteStream } = require('node:fs');
const { finished } = require('node:stream/promises');
const { parse } = require('csv-parse/sync');

const MAX_CHANNELS = 4096;
const MAX_CELLS = 16_000_000;
const MAX_CAPACITY = 2_000_000;
const numberCell = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;

class SampleRing {
  static maxCapacity(channelCount) {
    return Math.min(MAX_CAPACITY, Math.floor(MAX_CELLS / Math.max(1, channelCount)));
  }

  constructor(capacity) {
    this.columns = [];
    this.count = 0;
    this.end = 0;
    this.resize(capacity);
  }

  get start() {
    return this.end - this.count;
  }

  resize(capacity, channelCount = this.columns.length) {
    this.requested = Math.max(1, Math.min(MAX_CAPACITY, Math.trunc(capacity) || 1));
    const size = Math.min(this.requested, SampleRing.maxCapacity(channelCount));
    if (size === this.capacity && channelCount === this.columns.length) return;
    const keep = Math.min(this.count, size);
    const columns = Array.from({ length: channelCount }, (_, index) => {
      const column = new Float64Array(size).fill(NaN);
      const previous = this.columns[index];
      if (previous) {
        for (let i = this.end - keep; i < this.end; i++) column[i % size] = previous[i % this.capacity];
      }
      return column;
    });
    this.columns = columns;
    this.capacity = size;
    this.count = keep;
  }

  push(values) {
    if (!values.length || values.length > MAX_CHANNELS) throw new Error('Invalid sample channel count');
    if (values.length > this.columns.length) this.resize(this.requested, values.length);
    const slot = this.end % this.capacity;
    for (let ch = 0; ch < this.columns.length; ch++) this.columns[ch][slot] = values[ch] ?? NaN;
    this.end++;
    this.count = Math.min(this.count + 1, this.capacity);
  }

  clear() {
    this.columns = [];
    this.count = this.end = 0;
    this.resize(this.requested);
  }

  row(index) {
    return this.columns.map(column => column[index % this.capacity]);
  }

  slice(start, end, budget = Infinity) {
    start = Math.max(this.start, Math.min(this.end, Math.trunc(start)));
    end = Math.max(start, Math.min(this.end, Math.trunc(end)));
    const result = { start, end, columns: [] };
    if (end - start <= budget) {
      result.columns = this.columns.map(column => Array.from(
        { length: end - start }, (_, i) => finiteCell(column[(start + i) % this.capacity])
      ));
      return result;
    }

    const pointsPerBucket = 2 + this.columns.length * 2;
    const buckets = Math.max(1, Math.floor(budget / pointsPerBucket));
    const step = Math.ceil((end - start) / buckets);
    const indices = [];
    for (let left = start; left < end; left += step) {
      const right = Math.min(left + step, end);
      const picks = new Set([left, right - 1]);
      for (const column of this.columns) {
        let min = Infinity;
        let max = -Infinity;
        let minIndex = left;
        let maxIndex = left;
        for (let i = left; i < right; i++) {
          const value = column[i % this.capacity];
          if (value < min) { min = value; minIndex = i; }
          if (value > max) { max = value; maxIndex = i; }
        }
        picks.add(minIndex);
        picks.add(maxIndex);
      }
      indices.push(...[...picks].sort((a, b) => a - b));
    }
    result.indices = indices;
    result.columns = this.columns.map(column => indices.map(i => finiteCell(column[i % this.capacity])));
    return result;
  }
}

function finiteCell(value) {
  return Number.isFinite(value) ? value : null;
}

function csvString(value) {
  const text = String(value);
  return /["\r\n,]/.test(text) ? '"' + text.replaceAll('"', '""') + '"' : text;
}

function header(names, time) {
  return (time ? ['time_ms', ...names] : names).map(csvString).join(',') + '\r\n';
}

function csvRow(row, size, time) {
  const fields = Array.from({ length: size }, (_, i) => finiteCell(row[i]) ?? '');
  if (time !== undefined) fields.unshift(time);
  return fields.join(',') + '\r\n';
}

function* snapshotCsv(ring, names, dtMs, time = false) {
  yield header(names, time);
  for (let i = ring.start; i < ring.end; i++) {
    yield csvRow(ring.row(i), names.length, time ? i * dtMs : undefined);
  }
}

function parseCsv(text) {
  const records = parse(text, { bom: true, skip_empty_lines: true, trim: true });
  if (!records.length || !records[0].length) throw new Error('CSV contains no data');
  const first = records[0];
  const hasHeader = first.some(cell => cell !== '' && !numberCell.test(cell));
  const time = hasHeader && first[0].toLowerCase() === 'time_ms';
  const offset = time ? 1 : 0;
  if (first.length <= offset || first.length - offset > MAX_CHANNELS) throw new Error('Invalid CSV channel count');
  const names = first.slice(offset).map((cell, i) => hasHeader && cell ? cell : 'I' + i);
  const rows = records.slice(hasHeader ? 1 : 0).map((row, index) => row.slice(offset).map(cell => {
    if (!cell || /^nan$/i.test(cell)) return NaN;
    if (!numberCell.test(cell) || !Number.isFinite(Number(cell))) {
      throw new Error('Non-numeric sample at CSV row ' + (index + (hasHeader ? 2 : 1)));
    }
    return Number(cell);
  }));
  let dtMs;
  if (time && rows.length > 1) {
    const times = records.slice(1);
    const start = Number(times[0][0]);
    const step = (Number(times.at(-1)[0]) - start) / (times.length - 1);
    if (step > 0 && times.every((row, i) => row[0] && Math.abs(Number(row[0]) - start - i * step) < step * 1e-6)) dtMs = step;
  }
  return { names, rows, dtMs };
}

class CsvRecorder {
  constructor(path, names, dtMs, onError) {
    this.width = names.length;
    this.dtMs = dtMs;
    this.file = createWriteStream(path);
    this.done = finished(this.file);
    this.done.catch(onError);
    this.ready = new Promise((resolve, reject) => {
      this.file.once('open', resolve);
      this.file.once('error', reject);
    });
    this.file.write(header(names, true));
  }

  append(values, index) {
    if (this.file.destroyed || this.file.writableEnded) return;
    if (values.length > this.width) {
      throw new Error('Recording channel count changed. Start a new recording with the new channels.');
    }
    if (this.file.writableLength > 8 * 1024 * 1024) {
      throw new Error('Recording disk cannot keep up. The CSV was stopped after writing the queued rows.');
    }
    this.firstIndex ??= index;
    this.file.write(csvRow(values, this.width, (index - this.firstIndex) * this.dtMs));
  }

  close() {
    if (!this.file.writableEnded) this.file.end();
    return this.done;
  }
}

module.exports = { SampleRing, parseCsv, snapshotCsv, CsvRecorder, csvHeader: header, csvRow };
