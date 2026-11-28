const { createWriteStream, createReadStream, mkdtempSync } = require('node:fs');
const { rename, rm } = require('node:fs/promises');
const { join, dirname } = require('node:path');
const { tmpdir } = require('node:os');
const { randomUUID } = require('node:crypto');
const { createInterface } = require('node:readline');
const { Readable } = require('node:stream');
const { finished, pipeline } = require('node:stream/promises');
const { csvHeader, csvRow } = require('./samples.cjs');

class CaptureFile {
  constructor(onError) {
    this.directory = mkdtempSync(join(tmpdir(), 'signal-dock-capture-'));
    this.path = join(this.directory, 'samples.jsonl');
    this.file = createWriteStream(this.path, { flags: 'wx' });
    this.count = this.width = this.bytes = this.readers = 0;
    this.done = finished(this.file);
    this.done.catch(error => this.fail(error, onError));
    this.onError = onError;
  }

  fail(error, onError = this.onError) {
    if (this.error) return;
    this.error = error;
    onError(error);
  }

  append(values) {
    if (this.error || this.discarded) return;
    if (this.file.writableLength > 8 * 1024 * 1024) {
      const error = new Error('临时采集文件写入过慢，完整记录已中止。请检查磁盘后清空采样重新采集。');
      this.fail(error);
      this.file.destroy(error);
      return;
    }
    const row = Buffer.from(JSON.stringify(values) + '\n');
    this.file.write(row);
    this.bytes += row.length;
    this.width = Math.max(this.width, values.length);
    this.count++;
  }

  async snapshot() {
    if (this.error) throw this.error;
    if (this.discarded || !this.count) throw new Error('没有可保存的采样数据');
    const snapshot = { bytes: this.bytes, count: this.count, width: this.width };
    this.readers++;
    try {
      if (this.file.writableEnded) {
        await this.done;
      } else {
        // The callback completes after all earlier records have reached the file.
        await new Promise((resolve, reject) => this.file.write('', error => error ? reject(error) : resolve()));
      }
      if (this.error) throw this.error;
      return snapshot;
    } catch (error) {
      await this.release();
      throw error;
    }
  }

  async *csv(snapshot, names, dtMs, time) {
    yield csvHeader(names.slice(0, snapshot.width), time);
    const input = createReadStream(this.path, { start: 0, end: snapshot.bytes - 1 });
    const lines = createInterface({ input, crlfDelay: Infinity });
    const forwardError = error => lines.emit('error', error);
    input.on('error', forwardError);
    let index = 0;
    try {
      for await (const line of lines) {
        const row = JSON.parse(line);
        yield csvRow(row, snapshot.width, time ? index * dtMs : undefined);
        index++;
      }
      if (index !== snapshot.count) throw new Error('临时采集文件不完整，保存已取消');
    } finally {
      lines.close();
      input.destroy();
    }
  }

  async save(path, names, dtMs, time) {
    const snapshot = await this.snapshot();
    const temporary = join(dirname(path), '.signal-dock-export-' + randomUUID() + '.tmp');
    try {
      await pipeline(Readable.from(this.csv(snapshot, names, dtMs, time)), createWriteStream(temporary, { flags: 'wx' }));
      await rename(temporary, path);
    } finally {
      try {
        await rm(temporary, { force: true });
      } finally {
        await this.release();
      }
    }
  }

  async release() {
    this.readers--;
    if (this.discarded && !this.readers) await this.remove();
  }

  async remove() {
    await this.done.catch(() => {});
    await rm(this.directory, { recursive: true, force: true });
  }

  finish() {
    if (!this.file.writableEnded) this.file.end();
  }

  async dispose() {
    this.discarded = true;
    this.finish();
    await this.done.catch(() => {});
    if (!this.readers) await this.remove();
  }
}

module.exports = { CaptureFile };
