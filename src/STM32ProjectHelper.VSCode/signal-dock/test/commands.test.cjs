const assert = require('node:assert/strict');
const { test } = require('node:test');
const { setTimeout: wait } = require('node:timers/promises');

const { encodeCommand, repeatSend } = require('../src/commands.cjs');

test('text commands substitute integer and fixed decimal parameters and append a line ending', () => {
  assert.equal(encodeCommand({ text: 'P=%d, %.2f, %f, %%', eol: 'crlf' }, [-3.9, 1.25, 2]).toString(),
    'P=-3, 1.25, 2.000000, %\r\n');
});

test('text commands can use legacy encodings and plain literals', () => {
  assert.deepEqual(encodeCommand({ text: '中文 100%', encoding: 'gbk', eol: 'lf' }),
    Buffer.from('d6d0cec420313030250a', 'hex'));
});

test('hex commands insert little-endian float or supplied hexadecimal bytes', () => {
  assert.deepEqual(encodeCommand({ text: 'fa fb %% %% fb fa', hex: true, eol: 'lfcr' }, [1, 'AB cd']),
    Buffer.from('fafb0000803fabcdfbfa0a0d', 'hex'));
  assert.deepEqual(encodeCommand({ text: 'FF 00', hex: true }), Buffer.from('ff00', 'hex'));
});

test('command validation rejects malformed hex and invalid or missing parameters', () => {
  assert.throws(() => encodeCommand({ text: 'ab z1', hex: true }), /hex/i);
  assert.throws(() => encodeCommand({ text: 'abc', hex: true }), /hex/i);
  assert.throws(() => encodeCommand({ text: 'a %% b', hex: true }, [1]), /hex/i);
  assert.throws(() => encodeCommand({ text: '%%', hex: true }, ['a']), /hex/i);
  assert.throws(() => encodeCommand({ text: '%f' }), /parameter/i);
  assert.throws(() => encodeCommand({ text: '%d' }, ['hello']), /parameter/i);
  assert.throws(() => encodeCommand({ text: '%d'.repeat(11) }, Array(11).fill(0)), /10/);
});

test('repeatSend sends serially and returns the number sent', async () => {
  let active = 0;
  const calls = [];
  const sent = await repeatSend(async () => {
    assert.equal(active++, 0);
    await wait(1);
    calls.push('send');
    active--;
  }, { count: 3, delayMs: 1 });
  assert.equal(sent, 3);
  assert.equal(calls.length, 3);
  assert.equal(await repeatSend(() => assert.fail('must not send'), { count: 0 }), 0);
});

test('repeatSend stops an infinite loop and cancels a pending delay', async () => {
  const controller = new AbortController();
  const sent = repeatSend(() => setImmediate(() => controller.abort()), {
    count: -1, delayMs: 10_000, signal: controller.signal,
  });
  assert.equal(await sent, 1);
  assert.equal(await repeatSend(() => assert.fail('must not send'), { count: 3, signal: controller.signal }), 0);
});
