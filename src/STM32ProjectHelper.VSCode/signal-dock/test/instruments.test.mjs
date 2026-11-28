import test from 'node:test';
import assert from 'node:assert/strict';

test('zoom anchors to the cursor and can expand beyond the currently rendered samples', async () => {
  const { zoomRange, fitWindow, plotZone } = await import('../web/charts.mjs');
  assert.deepEqual(zoomRange([100, 200], 2, 0.25), [75, 275]);
  assert.deepEqual(fitWindow([75, 275], [0, 500]), [75, 275]);
  assert.deepEqual(fitWindow([-25, 175], [0, 500]), [0, 200]);
  const plot = { left: 55, right: 400, top: 27, bottom: 300 };
  assert.equal(plotZone(plot, 170, 190), 'both');
  assert.equal(plotZone(plot, 170, 315), 'x');
  assert.equal(plotZone(plot, 30, 180), 'y');
});

test('hover reads curve values at the same time without interpolating across gaps', async () => {
  const { valueAtSample } = await import('../web/charts.mjs');
  const series = { start: 20, end: 26, indices: [20, 21, 24, 25] };
  assert.equal(valueAtSample(series, [0, 4, 16, 20], 22), 8);
  assert.equal(valueAtSample(series, [0, null, 16, 20], 21.5), NaN);
  assert.equal(valueAtSample(series, [0, 4, 16, 20], 25), 20);
});

test('the waveform envelope retains impulse extrema and their sample order', async () => {
  const { minMaxEnvelope } = await import('../web/charts.mjs');
  const values = new Float32Array(16);
  values[5] = 12;
  values[6] = -9;
  assert.deepEqual(Array.from(minMaxEnvelope(values, 0, 16, 4)), [0, 3, 4, 5, 6, 7, 8, 11, 12, 15]);
});

test('the spectrum estimates the frequency and amplitude of a sampled sine', async () => {
  const { spectrum } = await import('../web/charts.mjs');
  const samples = Float64Array.from({ length: 512 }, (_, i) => 3 * Math.sin(i * Math.PI / 8) + 7);
  const bins = spectrum(samples, 1);
  const peak = bins.reduce((largest, value, i) => value > bins[largest] ? i : largest, 0);
  assert.equal(peak, 32);
  assert.ok(Math.abs(bins[peak] - 3) < 0.01);
  assert.ok(bins[0] < 0.01);
});

test('the histogram counts the inclusive maximum and ignores missing samples', async () => {
  const { histogram, metrics } = await import('../web/charts.mjs');
  assert.deepEqual(Array.from(histogram([0, 0, 0.5, 1, NaN], 4, 0, 1)), [2, 0, 1, 1]);
  assert.deepEqual(metrics([-1, -1, 1, 1, -1, -1, 1, 1, -1], 2), {
    min: -1, max: 1, mean: -1 / 9, rms: 1, peakToPeak: 2, duty: 50, hz: 125,
  });
});

test('the raw image decoder supports little-endian RGB565, monochrome and validation', async () => {
  const { rawToRgba } = await import('../web/image.mjs');
  assert.deepEqual(Array.from(rawToRgba(Uint8Array.of(0x00, 0xf8, 0xe0, 0x07), 2, 1, 'rgb565')), [255, 0, 0, 255, 0, 255, 0, 255]);
  assert.deepEqual(Array.from(rawToRgba(Uint8Array.of(0x80, 0x40), 2, 2, 'binary')), [255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255]);
  assert.throws(() => rawToRgba(new Uint8Array(2), 2, 2, 'rgb888'), /字节/);
});

test('image filters apply grayscale, threshold and inversion to all pixels', async () => {
  const { filterRgba } = await import('../web/image.mjs');
  const pixels = Uint8ClampedArray.of(255, 0, 0, 255, 0, 255, 0, 127);
  assert.deepEqual(Array.from(filterRgba(pixels, 'gray')), [54, 54, 54, 255, 182, 182, 182, 127]);
  assert.deepEqual(Array.from(filterRgba(pixels, 'threshold', 100)), [0, 0, 0, 255, 255, 255, 255, 127]);
  assert.deepEqual(Array.from(filterRgba(pixels, 'invert')), [0, 255, 255, 255, 255, 0, 255, 127]);
});

test('all thirteen instruments have serializable defaults and configurable fields', async () => {
  const { INSTRUMENTS, defaults, instrumentFields, controlValue } = await import('../web/instruments.mjs');
  assert.equal(Object.keys(INSTRUMENTS).length, 13);
  for (const [kind, title] of Object.entries(INSTRUMENTS)) {
    const config = defaults(kind);
    assert.equal(config.kind, kind);
    assert.equal(config.title, title);
    assert.deepEqual(config, JSON.parse(JSON.stringify(config)));
    const fields = instrumentFields(config, { channels: [], commands: [], images: {} });
    assert.ok(fields.some(field => field.key === 'title'));
    assert.equal(fields.length, new Set(fields.map(field => field.key)).size);
  }
  assert.equal(controlValue(9.97, { min: 0, max: 10, step: 0.5 }), 10);
  assert.equal(controlValue(-2, { min: 0, max: 10, step: 0.5 }), 0);
  assert.equal(controlValue(0.3, { min: 0, max: 10, step: 0.1 }), 0.3);
});

test('joystick dead zone and radial clamping keep the motion within the command range', async () => {
  const { joystickPosition } = await import('../web/instruments.mjs');
  assert.deepEqual(joystickPosition(0.01, -0.04, 0.06), [0, 0]);
  assert.deepEqual(joystickPosition(0.6, 0.8, 0), [0.6, 0.8]);
  assert.deepEqual(joystickPosition(6, 8, 0), [0.6, 0.8]);
});

test('the 3D instrument normalizes sensor quaternion data and supports signed Euler mappings', async () => {
  const { orientation } = await import('../web/cube.mjs');
  const data = [90, 0, 0, 90];
  const euler = orientation({ channels: [0, 1, 2], axisMap: '-x,z,y', angleUnit: 'deg' }, data);
  assert.ok(Math.abs(euler.x + Math.SQRT1_2) < 1e-12);
  assert.ok(Math.abs(euler.w - Math.SQRT1_2) < 1e-12);
  const quaternion = orientation({ channels: [0, 1, 2, 3], rotation: 'quaternion', quaternionOrder: 'wxyz' }, data);
  assert.ok(Math.abs(quaternion.z - Math.SQRT1_2) < 1e-12);
  assert.ok(Math.abs(quaternion.w - Math.SQRT1_2) < 1e-12);
});

test('VOFA packed color formats decode alpha and channel order', async () => {
  const { rawToRgba } = await import('../web/image.mjs');
  assert.deepEqual(Array.from(rawToRgba(Uint8Array.of(0x80, 0x00, 0x80), 1, 1, 8)), [255, 0, 0, 128]);
  assert.deepEqual(Array.from(rawToRgba(Uint8Array.of(0xff, 0x03, 0x00, 0x00), 1, 1, 19)), [255, 0, 0, 255]);
  assert.deepEqual(Array.from(rawToRgba(Uint8Array.of(0xf0, 0xa0), 1, 1, 15)), [0, 255, 0, 170]);
});

test('encoded PNM and X11 image data decode without relying on browser codecs', async () => {
  const { decodePortableImage } = await import('../web/image.mjs');
  const encode = text => new TextEncoder().encode(text);
  const ppm = Uint8Array.of(...encode('P6\n# camera\n2 1\n255\n'), 32, 10, 35, 4, 5, 6);
  const result = decodePortableImage(ppm, 31);
  assert.deepEqual([result.width, result.height, ...result.rgba], [2, 1, 32, 10, 35, 255, 4, 5, 6, 255]);
  const xbm = encode('#define img_width 2\n#define img_height 1\nstatic unsigned char img_bits[] = { 0x01 };');
  assert.deepEqual(Array.from(decodePortableImage(xbm, 32).rgba), [0, 0, 0, 255, 255, 255, 255, 255]);
  const xpm = encode('/* XPM */\nstatic char *x[]={"2 1 2 1", "a c #123456", "b c None", "ab"};');
  assert.deepEqual(Array.from(decodePortableImage(xpm, 33).rgba), [18, 52, 86, 255, 0, 0, 0, 0]);
});

test('Sobel highlights a grayscale boundary and keeps uniform regions dark', async () => {
  const { filterRgba } = await import('../web/image.mjs');
  const pixels = Uint8ClampedArray.from({ length: 5 * 3 * 4 }, (_, i) => i % 4 === 3 || (i >>> 2) % 5 > 2 ? 255 : 0);
  const filtered = filterRgba(pixels, 'sobel', 128, 5, 3);
  assert.equal(filtered[(1 * 5 + 1) * 4], 0);
  assert.equal(filtered[(1 * 5 + 2) * 4], 255);
  assert.equal(filtered[(1 * 5 + 3) * 4], 255);
});
test('instrument inspector offers channel and command bindings', async () => {
  const { instrumentFields, defaults } = await import('../web/instruments.mjs');
  const context = { channels: [{ name: 'Yaw' }, { name: 'Pitch' }], commands: [{ id: 'set', name: 'Set' }] };
  const wave = instrumentFields(defaults('wave'), context).find(field => field.key === 'channels');
  assert.deepEqual(wave.options.slice(0, 2), [[0, 'Yaw'], [1, 'Pitch']]);
  const slider = instrumentFields(defaults('slider'), context).find(field => field.key === 'commandId');
  assert.deepEqual(slider.options, [['', '无'], ['set', 'Set']]);
});
