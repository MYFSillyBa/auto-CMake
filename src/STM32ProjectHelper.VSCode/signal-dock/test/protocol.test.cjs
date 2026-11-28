const assert = require('node:assert/strict');
const { test } = require('node:test');

const { createDecoder, encodeSample, encodeImage } = require('../src/protocol.cjs');

const tail = Buffer.from('0000807f', 'hex');
const sample = Buffer.from('0000a03f000020c00000807f', 'hex');
const imageData = Buffer.from('0a0d0000807f00ff', 'hex');
const image = { id: 3, width: -1, height: -1, format: 28, data: imageData };
const justFloatImage = Buffer.concat([
  Buffer.from('0300000008000000ffffffffffffffff1c0000000000807f0000807f', 'hex'),
  imageData,
]);

function decodeChunks(engine, bytes, split, options) {
  const decoder = createDecoder(engine, options);
  const frames = [];
  for (let offset = 0; offset < bytes.length; offset += split) {
    frames.push(...decoder.push(bytes.subarray(offset, offset + split)));
  }
  frames.push(...decoder.flush());
  assert.deepEqual(Buffer.concat(frames.map((frame) => frame.bytes)), bytes);
  return frames;
}

test('JustFloat decodes little-endian samples and interleaved images across byte splits', () => {
  const bytes = Buffer.concat([sample, justFloatImage, sample]);
  for (let split = 1; split <= bytes.length; split++) {
    assert.deepEqual(decodeChunks('justfloat', bytes, split, { expectedChannels: 2 }), [
      { kind: 'sample', values: [1.25, -2.5], bytes: sample },
      { kind: 'image', ...image, bytes: justFloatImage },
      { kind: 'sample', values: [1.25, -2.5], bytes: sample },
    ]);
  }
});

test('JustFloat emits a five-channel sample immediately when its bytes cannot be image metadata', () => {
  const decoder = createDecoder('justfloat');
  const bytes = encodeSample('justfloat', [1, 2, 3, 4, 5]);
  assert.deepEqual(decoder.push(bytes), [{ kind: 'sample', values: [1, 2, 3, 4, 5], bytes }]);
});

test('JustFloat resolves a lone image-like numeric frame on flush', () => {
  const decoder = createDecoder('justfloat');
  const bytes = encodeImage('justfloat', { ...image, width: 2, height: 4, format: 24 }).subarray(0, 24);
  assert.deepEqual(decoder.push(bytes), []);
  assert.equal(decoder.flush()[0].kind, 'sample');
  assert.deepEqual(decoder.flush(), []);
});

test('JustFloat rejects malformed or wrong-width frames and resumes after their delimiter', () => {
  const malformed = Buffer.concat([Buffer.from([0xff]), tail]);
  const wrongWidth = Buffer.concat([sample.subarray(0, 8), sample]);
  const bytes = Buffer.concat([malformed, wrongWidth, sample, Buffer.from([1, 2, 3])]);
  assert.deepEqual(decodeChunks('justfloat', bytes, 3, { expectedChannels: 2 }), [
    { kind: 'invalid', bytes: malformed },
    { kind: 'invalid', bytes: wrongWidth },
    { kind: 'sample', values: [1.25, -2.5], bytes: sample },
    { kind: 'invalid', bytes: Buffer.from([1, 2, 3]) },
  ]);
});

test('JustFloat strict width distinguishes the aligned tail from marker bytes across two values', () => {
  const payload = Buffer.from('00000000807f803f', 'hex');
  const bytes = Buffer.concat([payload, tail]);
  for (let split = 1; split <= bytes.length; split++) {
    assert.deepEqual(decodeChunks('justfloat', bytes, split, { expectedChannels: 2 }), [
      { kind: 'sample', values: [0, payload.readFloatLE(4)], bytes },
    ]);
  }
});

test('JustFloat bounds garbage without a delimiter and resumes at the next frame', () => {
  const bytes = Buffer.concat([Buffer.alloc(100_000, 0xaa), tail, sample]);
  const frames = decodeChunks('justfloat', bytes, 53);
  assert.equal(frames.at(-1).kind, 'sample');
  assert.ok(frames.slice(0, -1).every((frame) => frame.kind === 'invalid'));
});

test('FireWater accepts prefixed, signed, exponent and plain numbers with LF, CRLF or LFCR', () => {
  const bytes = Buffer.from('telemetry: imu: +1.25, -.25E+2\r\n2,-3.\n\r4,5\n');
  for (let split = 1; split <= bytes.length; split++) {
    const samples = decodeChunks('firewater', bytes, split).filter((frame) => frame.kind === 'sample');
    assert.deepEqual(samples.map((frame) => frame.values), [[1.25, -25], [2, -3], [4, 5]]);
  }
  assert.equal(createDecoder('firewater').push(Buffer.from('1\n'))[0].kind, 'sample');
});

test('FireWater reads length-delimited binary images with all three line endings', () => {
  for (const ending of ['\n', '\r\n', '\n\r']) {
    const imageBytes = Buffer.concat([Buffer.from(`image: 3,8,-1,-1,28${ending}`), imageData]);
    const bytes = Buffer.concat([Buffer.from('1,2\n'), imageBytes, Buffer.from('3,4\n')]);
    for (let split = 1; split <= bytes.length; split++) {
      const frames = decodeChunks('firewater', bytes, split);
      assert.deepEqual(frames[1], { kind: 'image', ...image, bytes: imageBytes });
      assert.deepEqual(frames.map((frame) => frame.kind), ['sample', 'image', 'sample']);
    }
  }
});

test('FireWater preserves a long image header and a payload starting with CR', () => {
  const data = Buffer.from([0x0d, 0x0a]);
  const bytes = Buffer.concat([Buffer.from(`image:${' '.repeat(5000)}3,2,-1,-1,28\r\n`), data]);
  assert.deepEqual(decodeChunks('firewater', bytes, 83), [{ kind: 'image', ...image, data, bytes }]);
});

test('FireWater preserves text, rejects invalid image metadata and recovers from oversized lines', () => {
  const bytes = Buffer.from(`log: starting\nimage:1,999999999,0,0,27\n${'x'.repeat(70_000)}\n1,2\n`);
  const frames = decodeChunks('firewater', bytes, 203, { expectedChannels: 2 });
  assert.equal(frames[0].kind, 'raw');
  assert.equal(frames.at(-1).kind, 'sample');
  assert.ok(frames.slice(1, -1).every((frame) => frame.kind === 'invalid'));
});

test('incomplete image payloads flush as invalid bytes', () => {
  for (const engine of ['justfloat', 'firewater']) {
    const incomplete = encodeImage(engine, image).subarray(0, -1);
    assert.deepEqual(decodeChunks(engine, incomplete, 1), [{ kind: 'invalid', bytes: incomplete }]);
  }
});

test('RawData passes through every byte immediately', () => {
  const decoder = createDecoder('rawdata');
  assert.deepEqual(decoder.push(imageData), [{ kind: 'raw', bytes: imageData }]);
  assert.deepEqual(decoder.push(Buffer.alloc(0)), []);
  assert.deepEqual(decoder.flush(), []);
});

test('sample and image encoders emit protocol wire bytes', () => {
  assert.deepEqual(encodeSample('justfloat', [1.25, -2.5]), sample);
  assert.deepEqual(encodeSample('firewater', [1.25, -2.5]), Buffer.from('1.25,-2.5\n'));
  assert.deepEqual(encodeImage('justfloat', image), justFloatImage);
  assert.deepEqual(encodeImage('firewater', image), Buffer.concat([
    Buffer.from('image:3,8,-1,-1,28\r\n'), imageData,
  ]));
  assert.throws(() => encodeSample('justfloat', [3.5e38]), /float32/);
  assert.throws(() => encodeImage('firewater', { ...image, format: 0 }), /image/);
});
