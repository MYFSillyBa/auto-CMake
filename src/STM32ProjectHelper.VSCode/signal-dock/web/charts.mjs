import { t } from './ui.mjs';

const channelPalette = ['#38bdf8', '#fbbf24', '#c084fc', '#4ade80', '#fb7185', '#f97316', '#67e8f9', '#e879f9'];

export function minMaxEnvelope(values, start, end, pixels) {
  const buckets = Math.max(1, Math.floor(pixels));
  if (end - start <= buckets * 2) return null;
  const points = new Int32Array(buckets * 4);
  let count = 0;
  for (let bucket = 0; bucket < buckets; bucket++) {
    const first = start + Math.floor(bucket * (end - start) / buckets);
    const last = start + Math.floor((bucket + 1) * (end - start) / buckets) - 1;
    let low = first;
    let high = first;
    for (let index = first + 1; index <= last; index++) {
      const sample = values[index];
      if (!Number.isFinite(sample)) continue;
      if (!Number.isFinite(values[low]) || sample < values[low]) low = index;
      if (!Number.isFinite(values[high]) || sample > values[high]) high = index;
    }
    points[count++] = first;
    const innerStart = Math.min(low, high);
    const innerEnd = Math.max(low, high);
    if (innerStart > first && innerStart < last) points[count++] = innerStart;
    if (innerEnd !== innerStart && innerEnd > first && innerEnd < last) points[count++] = innerEnd;
    if (last !== first) points[count++] = last;
  }
  return points.subarray(0, count);
}

export function spectrum(values, dtMs, maxSize = 16384) {
  const size = 2 ** Math.floor(Math.log2(Math.min(values.length, maxSize)));
  if (size < 4 || dtMs <= 0) return new Float64Array(0);
  const real = new Float64Array(size);
  const imaginary = new Float64Array(size);
  const from = values.length - size;
  let mean = 0;
  let valid = 0;
  for (let i = from; i < values.length; i++) {
    if (Number.isFinite(values[i])) {
      mean += values[i];
      valid++;
    }
  }
  mean /= valid || 1;
  let windowSum = 0;
  for (let i = 0, reverse = 0; i < size; i++) {
    const window = (1 - Math.cos(2 * Math.PI * i / (size - 1))) / 2;
    windowSum += window;
    real[reverse] = Number.isFinite(values[from + i]) ? (values[from + i] - mean) * window : 0;
    let bit = size >> 1;
    while (bit && (reverse & bit)) {
      reverse ^= bit;
      bit >>= 1;
    }
    reverse ^= bit;
  }
  for (let width = 2; width <= size; width *= 2) {
    const half = width / 2;
    const stepReal = Math.cos(-2 * Math.PI / width);
    const stepImaginary = Math.sin(-2 * Math.PI / width);
    for (let block = 0; block < size; block += width) {
      let wReal = 1;
      let wImaginary = 0;
      for (let offset = 0; offset < half; offset++) {
        const even = block + offset;
        const odd = even + half;
        const r = wReal * real[odd] - wImaginary * imaginary[odd];
        const im = wReal * imaginary[odd] + wImaginary * real[odd];
        real[odd] = real[even] - r;
        imaginary[odd] = imaginary[even] - im;
        real[even] += r;
        imaginary[even] += im;
        const nextReal = wReal * stepReal - wImaginary * stepImaginary;
        wImaginary = wReal * stepImaginary + wImaginary * stepReal;
        wReal = nextReal;
      }
    }
  }
  return Float64Array.from({ length: size / 2 + 1 }, (_, i) =>
    Math.hypot(real[i], imaginary[i]) * (i === 0 || i === size / 2 ? 1 : 2) / windowSum);
}

export function histogram(values, bins, min, max) {
  const counts = new Uint32Array(bins);
  const width = max - min;
  if (width <= 0 || !bins) return counts;
  for (const value of values) {
    if (Number.isFinite(value) && value >= min && value <= max) {
      counts[Math.min(bins - 1, Math.floor((value - min) / width * bins))]++;
    }
  }
  return counts;
}

export function metrics(values, dtMs, threshold) {
  let min = Infinity;
  let max = -Infinity;
  let sum = 0;
  let squared = 0;
  let count = 0;
  for (const value of values) {
    if (!Number.isFinite(value)) continue;
    min = Math.min(min, value);
    max = Math.max(max, value);
    sum += value;
    squared += value * value;
    count++;
  }
  if (!count) return null;
  const crossing = Number.isFinite(threshold) ? threshold : (max + min) / 2;
  let firstRise;
  let lastRise;
  let rises = 0;
  let highDuration = 0;
  let totalDuration = 0;
  let previous = NaN;
  for (let i = 0; i < values.length; i++) {
    const value = values[i];
    if (Number.isFinite(previous) && Number.isFinite(value)) {
      totalDuration++;
      if (previous > crossing && value > crossing) highDuration++;
      else if (previous > crossing) highDuration += (previous - crossing) / (previous - value);
      else if (value > crossing) highDuration += (value - crossing) / (value - previous);
    }
    if (previous <= crossing && value > crossing) {
      const position = i - 1 + (crossing - previous) / (value - previous);
      firstRise ??= position;
      lastRise = position;
      rises++;
    }
    previous = value;
  }
  return {
    min, max, mean: sum / count, rms: Math.sqrt(squared / count),
    peakToPeak: max - min, duty: totalDuration ? highDuration / totalDuration * 100 : 0,
    hz: rises > 1 && dtMs > 0 ? 1000 * (rises - 1) / ((lastRise - firstRise) * dtMs) : null,
  };
}

function numberOr(value, fallback) {
  return Number.isFinite(value) ? value : fallback;
}

function channelScale(channels, index) {
  return numberOr(channels[index]?.scale, 1);
}

function channelOffset(channels, index) {
  return numberOr(channels[index]?.offset, 0);
}

function scaled(value, scale, offset) {
  return Number.isFinite(value) ? value * scale + offset : NaN;
}

function calibrated(values, channels, index, from = 0, to = values.length) {
  const scale = channelScale(channels, index);
  const offset = channelOffset(channels, index);
  return Float64Array.from({ length: Math.max(0, to - from) }, (_, i) => scaled(values[from + i], scale, offset));
}

function extent(series, from = 0, to = series.length, domain = [Infinity, -Infinity], scale = 1, offset = 0) {
  for (let i = from; i < to; i++) {
    const value = scaled(series[i], scale, offset);
    if (!Number.isFinite(value)) continue;
    if (value < domain[0]) domain[0] = value;
    if (value > domain[1]) domain[1] = value;
  }
  return domain;
}

function autoRange([low, high], pad = 0.07) {
  if (!Number.isFinite(low) || !Number.isFinite(high)) return [-1, 1];
  if (low === high) {
    const margin = Math.max(0.5, Math.abs(low) * 0.1);
    return [low - margin, high + margin];
  }
  const margin = (high - low) * pad;
  return [low - margin, high + margin];
}

function axis(auto, min, max, fallback) {
  return auto === false && Number.isFinite(min) && Number.isFinite(max) && max > min ? [min, max] : fallback;
}

function format(value, digits = 3) {
  if (!Number.isFinite(value)) return '—';
  if (value !== 0 && (Math.abs(value) >= 1e5 || Math.abs(value) < 0.001)) return value.toExponential(2);
  return String(Number(value.toFixed(Math.max(0, Math.min(15, Number(digits) || 0)))));
}

function label(channel, index) {
  return channel?.name || ('CH' + (index + 1));
}

function sampleIndex(series, i) {
  return series.indices?.[i] ?? (series.start + i * (series.stride ?? 1));
}

function findIndex(series, size, target) {
  let lo = 0;
  let hi = size;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (sampleIndex(series, mid) < target) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

function visibleWindow(series, size, window) {
  return [
    Math.max(0, findIndex(series, size, window[0]) - 1),
    Math.min(size, findIndex(series, size, window[1]) + 1),
  ];
}

function analysisWindow(series, size, window) {
  return window.map(value => findIndex(series, size, value));
}

export function zoomRange([low, high], factor, fraction) {
  const span = high - low;
  const anchor = low + span * fraction;
  return [anchor - span * factor * fraction, anchor + span * factor * (1 - fraction)];
}

export function fitWindow([left, right], [start, end]) {
  const span = Math.min(end - start, Math.max(2, right - left));
  const from = Math.max(start, Math.min(end - span, left));
  return [from, from + span];
}

export function plotZone(plot, x, y) {
  if (!plot || plot.right <= plot.left || plot.bottom <= plot.top) return '';
  if (y >= plot.top && y <= plot.bottom) {
    if (x >= plot.left && x <= plot.right) return 'both';
    if (x >= 0 && x < plot.left) return 'y';
  }
  if (x >= plot.left && x <= plot.right && y > plot.bottom && y <= plot.bottom + 24) return 'x';
  return '';
}

export function valueAtSample(series, values, target, points = false) {
  if (!values?.length || target < sampleIndex(series, 0) || target > sampleIndex(series, values.length - 1)) return NaN;
  const right = findIndex(series, values.length, target);
  const rightIndex = sampleIndex(series, right);
  const after = values[right];
  if (rightIndex === target || !right) return Number.isFinite(after) ? after : NaN;
  const left = right - 1;
  const before = values[left];
  const leftIndex = sampleIndex(series, left);
  if (!Number.isFinite(before) || !Number.isFinite(after)) return NaN;
  if (points) return target - leftIndex <= rightIndex - target ? before : after;
  return before + (after - before) * (target - leftIndex) / (rightIndex - leftIndex);
}

function setCanvasSize(canvas, container, ctx) {
  const width = Math.max(1, container.clientWidth);
  const height = Math.max(1, container.clientHeight);
  const ratio = Math.min(2, globalThis.devicePixelRatio || 1);
  const pixelsWide = Math.round(width * ratio);
  const pixelsHigh = Math.round(height * ratio);
  if (canvas.width !== pixelsWide || canvas.height !== pixelsHigh) {
    canvas.width = pixelsWide;
    canvas.height = pixelsHigh;
  }
  ctx.setTransform(pixelsWide / width, 0, 0, pixelsHigh / height, 0, 0);
  return [width, height];
}
function plotColor(style, name, fallback) {
  return style.getPropertyValue(name).trim() || fallback;
}

export function mountChart(container, config, api, { analysisOnly = false } = {}) {
  const canvas = document.createElement('canvas');
  canvas.className = 'telemetry-plot';
  canvas.setAttribute('aria-label', t('波形与数据分析'));
  canvas.setAttribute('aria-description', t('图内滚轮缩放双轴，轴上滚轮缩放单轴，拖动平移，双击设置游标'));
  const controls = document.createElement('div');
  controls.className = 'telemetry-plot-controls';
  const mode = document.createElement('select');
  mode.setAttribute('aria-label', t('图表类型'));
  const modes = [['spectrum', 'FFT'], ['histogram', '直方图'], ['bar', '柱状图']];
  if (!analysisOnly) modes.unshift(['time', '波形'], ['xy', 'XY']);
  for (const [value, name] of modes) {
    mode.add(new Option(t(name), value));
  }
  const reset = document.createElement('button');
  reset.type = 'button';
  reset.textContent = 'Auto';
  reset.title = t('自动量程并回到实时');
  controls.append(mode, reset);
  const hover = document.createElement('div');
  hover.className = 'telemetry-plot-hover';
  hover.hidden = true;
  const hoverTime = document.createElement('div');
  hoverTime.className = 'telemetry-hover-time';
  const hoverValues = document.createElement('div');
  hoverValues.className = 'telemetry-hover-values';
  hover.append(hoverTime, hoverValues);
  container.append(canvas, controls, hover);
  const ctx = canvas.getContext('2d');
  const events = new AbortController();
  const options = { signal: events.signal };
  let data;
  let lastSeries;
  let lastAnalysisSeries;
  let renderId = 0;
  let cursor;
  let cursorY;
  let reference;
  let secondReference;
  let drag;
  let viewport;
  let requestedRevision;
  let axisViewport = {};
  let activeWindow;
  let plot;
  let currentMode;
  let lastAxes;
  const analysisCache = new Map();

  const observer = new ResizeObserver(schedule);
  observer.observe(container);

  function schedule() {
    if (!renderId) renderId = requestAnimationFrame(render);
  }

  function windowFor(series) {
    if (viewport && requestedRevision !== undefined) return viewport;
    const end = Math.max(series.start + 1, series.end);
    const total = end - series.start;
    const right = viewport ? Math.min(end, Math.max(series.start + 1, viewport[1])) : end;
    const span = Math.min(total, viewport ? viewport[1] - viewport[0]
      : currentMode === 'time' && api.navigate ? total : Math.max(2, Number(config.history) || total));
    return [Math.max(series.start, right - span), right];
  }

  function analysis(kind, series, index, from, to, step, channels, bins, histogramDomain) {
    const key = [kind, index, from, to, step, bins, channelScale(channels, index), channelOffset(channels, index),
      config.dutyThreshold, config.fftPoints, histogramDomain].join('/');
    if (analysisCache.has(key)) return analysisCache.get(key);
    if (analysisCache.size > 128) analysisCache.clear();
    const values = calibrated(series.columns[index], channels, index, from, to);
    let result;
    if (kind === 'spectrum') {
      const amplitudes = spectrum(values, step, config.fftPoints || 16384);
      result = { values: amplitudes, step: amplitudes.length > 1 ? 1000 / (2 * (amplitudes.length - 1) * step) : 1 };
    } else if (kind === 'histogram') {
      result = { values: histogram(values, bins, ...histogramDomain), start: histogramDomain[0],
        step: (histogramDomain[1] - histogramDomain[0]) / bins };
    } else {
      result = metrics(values, step, config.dutyThreshold);
    }
    analysisCache.set(key, result);
    return result;
  }

  function render() {
    renderId = 0;
    if (!ctx) return;
    const [width, height] = setCanvasSize(canvas, container, ctx);
    const style = getComputedStyle(container);
    const background = plotColor(style, '--telemetry-plot-bg', '#091423');
    const gridColor = plotColor(style, '--telemetry-plot-grid', '#21354d');
    const textColor = plotColor(style, '--telemetry-plot-fg', '#adc6df');
    ctx.fillStyle = background;
    ctx.fillRect(0, 0, width, height);
    const series = data?.series;
    currentMode = config.mode || 'time';
    mode.value = currentMode;
    const measureSpace = currentMode === 'time' && config.measure !== false ? 33 : 0;
    plot = { left: 55, top: 27, right: width - 14, bottom: height - 24 - measureSpace };
    if (plot.right <= plot.left || plot.bottom <= plot.top) {
      hover.hidden = true;
      return;
    }
    const channels = data?.channels ?? [];
    const selected = config.channels ?? [0];
    const channelsUsed = selected.filter(index => channels[index]?.visible !== false);
    const fallbackDt = numberOr(data?.dtMs, 1);
    const dt = fallbackDt > 0 ? fallbackDt : 1;
    const traces = [];
    let xExtent = [Infinity, -Infinity];
    let yExtent = [Infinity, -Infinity];
    const kind = currentMode;
    let window = [0, Math.max(2, config.history || 1000)];
    let barNames = [];
    const source = data?.analysisSeries ?? series;
    const contiguous = source && !source.indices;
    if (series) {
      window = windowFor(series);
      if (kind === 'time' && !viewport) {
        const fixedRange = axisViewport.x ?? axis(config.xAxisAuto, config.xMin, config.xMax, null);
        if (fixedRange) window = fixedRange.map(value => value * 1000 / dt);
      }
      activeWindow = window;
      if (kind === 'xy') {
        const explicit = [...String(config.xyPairs ?? '').matchAll(/(\d+)\s*:\s*(\d+)/g)].flatMap(pair => [Number(pair[1]), Number(pair[2])]);
        const configuredPair = config.xChannel >= 0 && config.yChannel >= 0;
        const pairs = explicit.length ? explicit : configuredPair ? [config.xChannel, config.yChannel] : selected;
        for (let i = 0; i + 1 < pairs.length; i += 2) {
          const [x, y] = [pairs[i], pairs[i + 1]];
          const xs = series.columns[x];
          const ys = series.columns[y];
          if (!xs || !ys || channels[x]?.visible === false || channels[y]?.visible === false) continue;
          const [from, to] = visibleWindow(series, Math.min(xs.length, ys.length), window);
          const xScale = channelScale(channels, x);
          const xOffset = channelOffset(channels, x);
          const yScale = channelScale(channels, y);
          const yOffset = channelOffset(channels, y);
          extent(xs, from, to, xExtent, xScale, xOffset);
          extent(ys, from, to, yExtent, yScale, yOffset);
          traces.push({
            channel: y, name: label(channels[y], y) + ' / ' + label(channels[x], x), from, to, ys,
            x: k => scaled(xs[k], xScale, xOffset), y: k => scaled(ys[k], yScale, yOffset),
          });
        }
      } else if (kind === 'bar' && channelsUsed.length) {
        const baseline = numberOr(config.barBaseline, 0);
        const values = Float64Array.from(channelsUsed, index =>
          scaled(series.columns[index]?.at(-1), channelScale(channels, index), channelOffset(channels, index)));
        barNames = channelsUsed.map(index => label(channels[index], index));
        xExtent = [0, Math.max(1, values.length)];
        yExtent = extent(values, 0, values.length, [baseline, baseline]);
        traces.push({ channel: channelsUsed[0], from: 0, to: values.length, x: i => i, y: i => values[i], bars: 1, barChannels: channelsUsed, baseline });
      } else if (kind === 'spectrum' || kind === 'histogram') {
        let binDomain;
        if (contiguous && kind === 'histogram') {
          const domain = [Infinity, -Infinity];
          for (const index of channelsUsed) {
            const column = source.columns[index];
            if (!column?.length) continue;
            const [from, to] = analysisWindow(source, column.length, window);
            extent(column, from, to, domain, channelScale(channels, index), channelOffset(channels, index));
          }
          binDomain = axis(false, config.binMin, config.binMax, axis(config.xAxisAuto, config.xMin, config.xMax, autoRange(domain, 0)));
        }
        for (const index of contiguous ? channelsUsed : []) {
          const column = source.columns[index];
          if (!column?.length) continue;
          const [from, to] = analysisWindow(source, column.length, window);
          if (to - from < (kind === 'spectrum' ? config.fftPoints || 4 : 1)) continue;
          const bins = Math.floor(Math.max(4, Math.min(256, Number(config.bins) || 32)));
          const result = analysis(kind, source, index, from, to, dt * (source.stride ?? 1), channels, bins, binDomain);
          const { values, start = 0, step } = result;
          const bars = kind === 'histogram' ? step : 0;
          xExtent[0] = Math.min(xExtent[0], start);
          xExtent[1] = Math.max(xExtent[1], start + step * (values.length - (bars ? 0 : 1)));
          extent(values, 0, values.length, yExtent);
          traces.push({
            channel: index, name: label(channels[index], index), from: 0, to: values.length, ys: values,
            x: i => start + i * step, y: i => values[i], bars,
          });
        }
      } else {
        const scaleTime = dt / 1000;
        xExtent = window.map(index => index * scaleTime);
        for (const index of channelsUsed) {
          const column = series.columns[index];
          if (!column?.length) continue;
          const scale = channelScale(channels, index);
          const offset = channelOffset(channels, index);
          const xOffset = numberOr(channels[index]?.xOffset, 0);
          const [from, to] = visibleWindow(series, column.length, [window[0] - xOffset, window[1] - xOffset]);
          extent(column, from, to, yExtent, scale, offset);
          traces.push({
            channel: index, name: label(channels[index], index), from, to, ys: column,
            x: i => (sampleIndex(series, i) + xOffset) * scaleTime, y: i => scaled(column[i], scale, offset),
          });
        }
      }
    }
    const { left, right, top, bottom } = plot;
    if (kind === 'spectrum' || kind === 'histogram') yExtent[0] = Math.min(0, yExtent[0]);
    const xDomain = axisViewport.x ?? axis(config.xAxisAuto, config.xMin, config.xMax,
      kind === 'time' || kind === 'bar' ? (Number.isFinite(xExtent[0]) ? xExtent : [0, 1]) : autoRange(xExtent, kind === 'xy' ? 0.06 : 0));
    const yDomain = axisViewport.y ?? axis(config.axisAuto, config.yMin, config.yMax, autoRange(yExtent));
    const px = value => left + (value - xDomain[0]) / (xDomain[1] - xDomain[0]) * (right - left);
    const py = value => bottom - (value - yDomain[0]) / (yDomain[1] - yDomain[0]) * (bottom - top);
    ctx.strokeStyle = gridColor;
    ctx.fillStyle = textColor;
    ctx.font = '11px system-ui, sans-serif';
    ctx.lineWidth = 1;
    const horizontal = Math.max(2, Math.floor((right - left) / 92));
    const vertical = Math.max(2, Math.floor((bottom - top) / 44));
    ctx.beginPath();
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    for (let i = 0; i <= vertical; i++) {
      const value = yDomain[0] + (yDomain[1] - yDomain[0]) * i / vertical;
      const y = py(value);
      if (config.grid !== false) {
        ctx.moveTo(left, y);
        ctx.lineTo(right, y);
      }
      if (config.showYAxis !== false) ctx.fillText(format(value) + (i === vertical && config.yUnit ? ' ' + config.yUnit : ''), left - 7, y, left - 10);
    }
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    if (kind === 'bar') {
      const maxBars = Math.max(1, Math.floor((right - left) / 24));
      const stride = Math.max(1, Math.ceil(barNames.length / maxBars));
      for (let i = 0; i < barNames.length; i += stride) {
        if (config.showXAxis !== false) ctx.fillText(barNames[i], px(i + 0.5), bottom + 7, Math.max(18, (right - left) / barNames.length - 3));
      }
    } else {
      const unit = config.xUnit ? ' ' + config.xUnit : kind === 'time' ? ' s' : kind === 'spectrum' ? ' Hz' : '';
      for (let i = 0; i <= horizontal; i++) {
        const value = xDomain[0] + (xDomain[1] - xDomain[0]) * i / horizontal;
        const x = px(value);
        if (config.grid !== false) {
          ctx.moveTo(x, top);
          ctx.lineTo(x, bottom);
        }
        if (config.showXAxis !== false) ctx.fillText(format(value) + (i === horizontal ? unit : ''), Math.min(right - 16, x), bottom + 7);
      }
    }
    ctx.rect(left, top, right - left, bottom - top);
    ctx.stroke();
    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top, right - left, bottom - top);
    ctx.clip();
    const stroke = index => channels[index]?.color || channelPalette[index % channelPalette.length] || channelPalette[0];
    for (const trace of traces) {
      ctx.strokeStyle = stroke(trace.channel);
      ctx.fillStyle = ctx.strokeStyle;
      ctx.lineWidth = Math.max(0.5, Number(config.lineWidth) || 1.5);
      if (trace.bars) {
        ctx.globalAlpha = trace.barChannels ? 0.9 : 0.55;
        const baseline = py(trace.baseline ?? 0);
        for (let i = trace.from; i < trace.to; i++) {
          if (trace.barChannels) ctx.fillStyle = stroke(trace.barChannels[i]);
          const value = trace.y(i);
          if (!Number.isFinite(value)) continue;
          const x = trace.x(i);
          const y = py(value);
          ctx.fillRect(px(x + trace.bars * 0.08), Math.min(y, baseline),
            Math.max(1, px(x + trace.bars * 0.92) - px(x + trace.bars * 0.08)), Math.abs(y - baseline));
        }
        ctx.globalAlpha = 1;
        continue;
      }
      const envelope = kind === 'time' ? minMaxEnvelope(trace.ys, trace.from, trace.to, right - left) : null;
      const length = envelope?.length ?? trace.to - trace.from;
      let previous = false;
      const points = config.traceStyle === 'points';
      ctx.beginPath();
      for (let i = 0; i < length; i++) {
        const at = envelope ? envelope[i] : trace.from + i;
        const x = px(trace.x(at));
        const y = py(trace.y(at));
        if (!Number.isFinite(x) || !Number.isFinite(y)) {
          previous = false;
        } else if (points) {
          ctx.fillRect(x - ctx.lineWidth / 2, y - ctx.lineWidth / 2, ctx.lineWidth, ctx.lineWidth);
        } else if (previous) {
          ctx.lineTo(x, y);
        } else {
          ctx.moveTo(x, y);
          if (length === 1) ctx.lineTo(x + 1, y);
          previous = true;
        }
      }
      ctx.stroke();
    }
    if (kind === 'time' && (cursor !== undefined || Number.isFinite(reference))) {
      ctx.strokeStyle = textColor;
      ctx.setLineDash([4, 4]);
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (const x of [cursor, px(reference), px(secondReference)]) {
        if (!Number.isFinite(x) || x < left || x > right) continue;
        ctx.moveTo(x, top);
        ctx.lineTo(x, bottom);
      }
      ctx.stroke();
      ctx.setLineDash([]);
    }
    const hoverSample = cursor === undefined ? NaN : xDomain[0] + (cursor - left) / (right - left) * (xDomain[1] - xDomain[0]);
    const read = (trace, seconds) => {
      const index = trace.channel;
      const target = seconds * 1000 / dt - numberOr(channels[index]?.xOffset, 0);
      const raw = source?.columns[index];
      const useRaw = raw?.length && target >= sampleIndex(source, 0) && target <= sampleIndex(source, raw.length - 1);
      const values = useRaw ? raw : series?.columns[index];
      return scaled(valueAtSample(useRaw ? source : series, values, target, config.traceStyle === 'points'),
        channelScale(channels, index), channelOffset(channels, index));
    };
    if (kind === 'time' && Number.isFinite(hoverSample) && traces.length && !drag) {
      hoverTime.textContent = 't  ' + format(hoverSample, 6) + ' s';
      while (hoverValues.children.length < traces.length) {
        const row = document.createElement('div');
        row.append(document.createElement('span'), document.createElement('b'));
        hoverValues.append(row);
      }
      while (hoverValues.children.length > traces.length) hoverValues.lastElementChild.remove();
      for (const [i, trace] of traces.entries()) {
        const value = read(trace, hoverSample);
        const row = hoverValues.children[i];
        const color = stroke(trace.channel);
        row.style.setProperty('--trace-color', color);
        row.firstElementChild.textContent = trace.name;
        row.lastElementChild.textContent = format(value, channels[trace.channel]?.decimals ?? 3);
        const y = py(value);
        if (!Number.isFinite(y) || y < top || y > bottom) continue;
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.arc(cursor, y, 3.5, 0, Math.PI * 2);
        ctx.fill();
      }
      hover.hidden = false;
      hover.style.maxHeight = bottom - top + 'px';
      const boxWidth = hover.offsetWidth;
      hover.style.left = Math.max(left, cursor + 12 + boxWidth <= right ? cursor + 12 : cursor - boxWidth - 12) + 'px';
      hover.style.top = Math.max(top, Math.min(bottom - hover.offsetHeight, cursorY + 8)) + 'px';
    } else {
      hover.hidden = true;
    }
    ctx.restore();

    ctx.textBaseline = 'middle';
    ctx.textAlign = 'left';
    if (config.legend !== false) {
      let legendX = left;
      for (const trace of traces) {
        if (!trace.name || legendX >= right - 100) break;
        ctx.fillStyle = stroke(trace.channel);
        ctx.fillRect(legendX, top - 13, 12, 3);
        ctx.fillStyle = textColor;
        ctx.fillText(trace.name, legendX + 17, top - 12);
        legendX += ctx.measureText(trace.name).width + 31;
      }
    }
    if (!traces.length) {
      ctx.textAlign = 'center';
      ctx.fillStyle = textColor;
      const empty = kind === 'xy' ? '选择成对的 X / Y 通道'
        : !channelsUsed.length ? '未选择显示通道'
        : (kind === 'spectrum' || kind === 'histogram') && series?.end > series?.start ? '该区间缺少连续采样' : '等待通道数据';
      ctx.fillText(t(empty), (left + right) / 2, (top + bottom) / 2);
      ctx.textAlign = 'left';
    }
    plot.domain = xDomain;
    plot.yDomain = yDomain;
    if (kind === 'time' && config.measure !== false && series) {
      const sample = secondReference ?? reference;
      const index = channelsUsed[0];
      const sourceValues = source.columns[index];
      ctx.fillStyle = textColor;
      let stats;
      if (contiguous && sourceValues?.length) {
        const [from, to] = analysisWindow(source, sourceValues.length, window);
        stats = analysis('metrics', source, index, from, to, dt * (source.stride ?? 1), channels);
      }
      if (stats) {
        const { min, max, mean, rms, peakToPeak, duty, hz } = stats;
        ctx.fillText(t('最小') + ' ' + format(min) + '  ' + t('最大') + ' ' + format(max) + '  ' + t('均值') + ' ' + format(mean) +
          '  RMS ' + format(rms) + '  ' + t('峰峰') + ' ' + format(peakToPeak) + '  ' + t('占空') + ' ' + format(duty, 1) + '%  ' + t('频率') + ' ' + format(hz) + ' Hz',
          left, bottom + 28, right - left);
      }
      if (Number.isFinite(sample)) {
        const readings = traces.map(trace => {
          const value = read(trace, sample);
          const decimals = channels[trace.channel]?.decimals ?? 3;
          const delta = Number.isFinite(reference) && reference !== sample
            ? ' (Δ ' + format(value - read(trace, reference), decimals) + ')' : '';
          return trace.name + ' ' + format(value, decimals) + delta;
        });
        const delta = Number.isFinite(reference) ? '  Δt ' + format((sample - reference) * 1000) + ' ms' : '';
        ctx.fillText('t ' + format(sample) + ' s' + delta + '  ' + readings.join('  '), left, bottom + 45, right - left);
      }
    }
  }

  function inPlot(event) {
    return plotZone(plot, event.offsetX, event.offsetY) === 'both';
  }

  function setWindow(left, right) {
    const series = data?.series;
    if (!series) return;
    const bounds = api.navigate ? [data.start ?? series.start, data.end ?? series.end] : [series.start, series.end];
    if (bounds[1] - bounds[0] < 2) return;
    viewport = fitWindow([left, right], bounds);
    if (api.navigate) requestedRevision = api.navigate(viewport);
    if (config.xAxisAuto === false) axisViewport.x = viewport.map(index => index * (data.dtMs || 1) / 1000);
    schedule();
  }

  const resetPlot = () => {
    viewport = undefined;
    requestedRevision = undefined;
    axisViewport = {};
    cursor = undefined;
    reference = undefined;
    secondReference = undefined;
    schedule();
  };
  const auto = () => {
    resetPlot();
    if (config.axisAuto === false || config.xAxisAuto === false) api.change({ axisAuto: true, xAxisAuto: true });
    if (currentMode === 'time') api.navigate?.(null);
  };
  const placeCursor = x => {
    if (currentMode !== 'time' || config.measure === false || !plot?.domain) return;
    const fraction = (x - plot.left) / (plot.right - plot.left);
    const [low, high] = plot.domain;
    const time = low + (high - low) * fraction;
    if (Number.isFinite(reference) && !Number.isFinite(secondReference)) secondReference = time;
    else {
      reference = time;
      secondReference = undefined;
    }
    schedule();
  };
  mode.addEventListener('change', () => {
    api.change({ mode: mode.value });
    resetPlot();
  }, options);
  reset.addEventListener('click', auto, options);
  canvas.addEventListener('dblclick', event => {
    const zone = plotZone(plot, event.offsetX, event.offsetY);
    if (zone === 'x' || zone === 'both') placeCursor(event.offsetX);
  }, options);
  canvas.addEventListener('contextmenu', event => {
    if (!api.menu || plotZone(plot, event.offsetX, event.offsetY) !== 'x') return;
    api.menu(event, [
      ['Auto', auto], ['添加游标', () => placeCursor(event.offsetX)],
      ['清除游标', () => { reference = secondReference = undefined; schedule(); }]
    ]);
  }, options);
  canvas.addEventListener('wheel', event => {
    const zone = plotZone(plot, event.offsetX, event.offsetY);
    if (!activeWindow || !plot?.domain || !zone || currentMode === 'bar') return;
    event.preventDefault();
    const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? canvas.clientHeight : 1);
    const factor = Math.exp(Math.max(-0.7, Math.min(0.7, delta * 0.002)));
    const x = (event.offsetX - plot.left) / (plot.right - plot.left);
    const y = (plot.bottom - event.offsetY) / (plot.bottom - plot.top);
    const zoomAxis = (axis, range, fraction) => {
      const next = zoomRange(range, factor, fraction);
      const span = next[1] - next[0];
      if (span >= 1e-12 && span <= 1e20) axisViewport[axis] = next;
    };
    if (zone === 'y' || zone === 'both') zoomAxis('y', axisViewport.y ?? plot.yDomain, y);
    if (zone === 'x' || (zone === 'both' && !event.shiftKey)) {
      if (currentMode === 'time') {
        setWindow(...zoomRange(viewport ?? activeWindow, factor, x));
      } else {
        zoomAxis('x', axisViewport.x ?? plot.domain, x);
      }
    }
    schedule();
  }, { ...options, passive: false });
  canvas.addEventListener('pointerdown', event => {
    const zone = plotZone(plot, event.offsetX, event.offsetY);
    if (!activeWindow || !plot?.domain || !zone || event.button !== 0 || currentMode === 'bar') return;
    if (event.shiftKey && zone === 'both' && currentMode === 'time') {
      placeCursor(event.offsetX);
    } else {
      drag = { zone, x: event.offsetX, y: event.offsetY, window: activeWindow, xDomain: plot.domain, yDomain: plot.yDomain };
      canvas.setPointerCapture(event.pointerId);
    }
    schedule();
  }, options);
  canvas.addEventListener('pointermove', event => {
    canvas.dataset.zone = plotZone(plot, event.offsetX, event.offsetY);
    cursor = inPlot(event) ? event.offsetX : undefined;
    cursorY = event.offsetY;
    if (drag) {
      if (!drag.moved && Math.hypot(drag.x - event.offsetX, drag.y - event.offsetY) < 3) return;
      drag.moved = true;
      canvas.classList.add('is-panning');
      const horizontal = (drag.x - event.offsetX) / (plot.right - plot.left);
      const vertical = (event.offsetY - drag.y) / (plot.bottom - plot.top);
      if (drag.zone !== 'y' && currentMode === 'time') {
        const shift = horizontal * (drag.window[1] - drag.window[0]);
        if (Math.abs(drag.x - event.offsetX) >= 1) setWindow(drag.window[0] + shift, drag.window[1] + shift);
      } else if (drag.zone !== 'y') {
        const dx = horizontal * (drag.xDomain[1] - drag.xDomain[0]);
        axisViewport.x = drag.xDomain.map(value => value + dx);
      }
      if (drag.zone !== 'x') {
        const dy = vertical * (drag.yDomain[1] - drag.yDomain[0]);
        axisViewport.y = drag.yDomain.map(value => value + dy);
      }
    }
    schedule();
  }, options);
  canvas.addEventListener('pointerleave', () => {
    cursor = undefined;
    schedule();
  }, options);
  canvas.addEventListener('lostpointercapture', () => {
    drag = undefined;
    canvas.classList.remove('is-panning');
    schedule();
  }, options);
  schedule();

  return {
    update(next) {
      if (next.series !== lastSeries || next.analysisSeries !== lastAnalysisSeries) {
        if (next.series?.end < lastSeries?.end) resetPlot();
        lastSeries = next.series;
        lastAnalysisSeries = next.analysisSeries;
        analysisCache.clear();
      }
      data = next;
      if (requestedRevision !== undefined && next.viewRevision >= requestedRevision) {
        viewport = undefined;
        requestedRevision = undefined;
      }
      const axes = [config.mode, config.history, config.axisAuto, config.yMin, config.yMax, config.xAxisAuto, config.xMin, config.xMax].join('/');
      if (lastAxes !== undefined && lastAxes !== axes) resetPlot();
      lastAxes = axes;
      schedule();
    },
    dispose() {
      events.abort();
      observer.disconnect();
      if (renderId) cancelAnimationFrame(renderId);
      container.replaceChildren();
    },
  };
}
