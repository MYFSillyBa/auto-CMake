import { mountChart } from './charts.mjs';
import { mountCube } from './cube.mjs';
import { mountImage } from './image.mjs';

export const INSTRUMENTS = Object.freeze({
  wave: '波形图',
  button: '按钮',
  delayButton: '延时按钮',
  toggle: '开关',
  slider: '滑块',
  dial: '旋钮',
  materialDial: '材质旋钮',
  joystick: '摇杆',
  light: '指示灯',
  gauge: '仪表',
  circularGauge: '圆形仪表',
  cube: '3D 姿态',
  image: '图像',
});

const rangeDefaults = { min: 0, max: 100, step: 1, value: 0, unit: '', decimals: 1 };
const extraDefaults = {
  wave: {
    mode: 'time', xChannel: -1, yChannel: -1, history: 1000,
    axisAuto: true, yMin: -10, yMax: 10, xAxisAuto: true, xMin: 0, xMax: 10,
    grid: true, lineWidth: 1.5, legend: true, measure: true, bins: 32, dutyThreshold: null,
    analysisOverlay: 'none', fftPoints: 0, barBaseline: 0, traceStyle: 'line', showXAxis: true, showYAxis: true,
  },
  button: { label: '发送', value: 1, trigger: 'click', sendOnRelease: false, offValue: 0, releaseDelayMs: 0 },
  delayButton: { label: '长按发送', value: 1, delayMs: 500, sendOnRelease: false, offValue: 0, releaseDelayMs: 0 },
  toggle: { label: '', value: 0, onValue: 1, offValue: 0, feedback: false, threshold: null, invert: false },
  slider: { ...rangeDefaults, feedback: false, continuous: true },
  dial: { ...rangeDefaults, feedback: false, continuous: true },
  materialDial: { ...rangeDefaults, feedback: false, continuous: true, color: '#a78bfa' },
  joystick: { min: -100, max: 100, step: 1, deadZone: 0.04, spring: true, invertY: false, feedback: false, continuous: true },
  light: { threshold: 0.5, onColor: '#4ade80', offColor: '#30455c', midColor: '#f3bc60', threeState: false, thresholdHigh: 0.8, invert: false },
  gauge: { ...rangeDefaults, warning: null, warningLow: null, invertWarning: false, ticks: 5 },
  circularGauge: { ...rangeDefaults, warning: null, warningLow: null, invertWarning: false, ticks: 5, startAngle: -135, endAngle: 135 },
  cube: { rotation: 'euler', rotationOrder: 'XYZ', quaternionOrder: 'xyzw', angleUnit: 'deg', axisMap: 'xyz', model: null,
    showAxes: true, showModelAxes: false, viewPreset: 'perspective', poseX: 0, poseY: 0, poseZ: 0, antialias: true },
  image: { imageId: 0, format: 'auto', filter: 'none', threshold: 128, fit: 'contain', flipX: false, flipY: false, smoothImage: true },
};

export function defaults(kind) {
  const defaultChannels = kind === 'cube' ? [0, 1, 2] : ['wave', 'light', 'gauge', 'circularGauge'].includes(kind) ? [0] : [];
  return { kind, title: INSTRUMENTS[kind], channels: defaultChannels, commandId: '', color: '#38bdf8', ...extraDefaults[kind] };
}

const field = (key, label, type, extra = {}) => ({ key, label, type, ...extra });
const textField = (key, label) => field(key, label, 'text');
const booleanField = (key, label) => field(key, label, 'checkbox');
const numberField = (key, label, min, max, step = 'any') => field(key, label, 'number', { min, max, step });
const selectField = (key, label, options) => field(key, label, 'select', { options });
const colorField = (key, label) => field(key, label, 'color');
const valueRange = [
  numberField('min', '最小值'), numberField('max', '最大值'), numberField('step', '步长', 0.000001),
];
const displayRange = [textField('unit', '单位'), numberField('decimals', '小数位', 0, 8, 1), colorField('color', '颜色')];
const feedback = booleanField('feedback', '从数据通道读回');
const continuous = booleanField('continuous', '调整时连续发送');

export function instrumentFields(config, context = {}) {
  const fields = [textField('title', '标题')];
  const channelList = context.channels ?? [];
  const selected = [...(config.channels ?? []), config.xChannel, config.yChannel].filter(Number.isInteger);
  const channelCount = Math.max(channelList.length, 12, ...selected.map(index => index + 1));
  const channelOptions = Array.from({ length: Math.min(channelCount, 4096) }, (_, index) =>
    [index, channelList[index]?.name || 'CH' + (index + 1)]);
  const channelsField = field('channels', '数据通道', 'channels', { options: channelOptions });
  const commandField = field('commandId', '发送命令', 'command', {
    options: [['', '无'], ...(context.commands ?? []).map(command => [command.id, command.name])],
  });
  const overlays = [['none', '无'], ['bar', '柱状图'], ['histogram', '直方图'], ['spectrum', 'FFT']];
  switch (config.kind) {
    case 'wave':
      fields.push(channelsField,
        selectField('mode', '图表类型', [['time', '波形'], ['xy', 'XY'], ['spectrum', 'FFT'], ['histogram', '直方图'], ['bar', '柱状图']]),
        numberField('history', '显示采样数', 4, 10_000_000, 1), booleanField('grid', '网格'), booleanField('legend', '图例'),
        numberField('lineWidth', '线宽', 0.5, 8, 0.5), selectField('traceStyle', '绘制方式', [['line', '连线'], ['points', '点']]),
        booleanField('showXAxis', '显示 X 轴'), booleanField('showYAxis', '显示 Y 轴'), textField('xUnit', 'X 轴单位'), textField('yUnit', 'Y 轴单位'),
        booleanField('xAxisAuto', 'X 轴自动范围'), numberField('xMin', 'X 轴下限'), numberField('xMax', 'X 轴上限'),
        booleanField('axisAuto', 'Y 轴自动范围'), numberField('yMin', 'Y 轴下限'), numberField('yMax', 'Y 轴上限'));
      if (config.mode === 'xy') {
        const options = [[-1, '自动配对'], ...channelOptions];
        fields.push(field('xChannel', '指定 X 通道', 'channel', { options }), field('yChannel', '指定 Y 通道', 'channel', { options }));
        fields.push(textField('xyPairs', 'XY 配对（如 0:1,2:3）'));
      }
      if (['time', 'xy'].includes(config.mode)) fields.push(selectField('analysisOverlay', '同步显示分析', overlays));
      if ([config.mode, config.analysisOverlay].includes('histogram')) fields.push(
        numberField('bins', '直方图组数', 4, 256, 1), numberField('binMin', '直方图下限'), numberField('binMax', '直方图上限'));
      if ([config.mode, config.analysisOverlay].includes('spectrum')) fields.push(
        selectField('fftPoints', 'FFT 点数', [[0, '自动'], ...[64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384].map(n => [n, String(n)])]));
      if ([config.mode, config.analysisOverlay].includes('bar')) fields.push(numberField('barBaseline', '条形基线'));
      if (config.mode === 'time') fields.push(booleanField('measure', '波形测量'), numberField('dutyThreshold', '占空比阈值（空白为自动）'));
      break;
    case 'button':
    case 'delayButton':
      fields.push(commandField, textField('label', '按钮文字'), numberField('value', '参数值'), colorField('color', '颜色'));
      if (config.kind === 'delayButton') fields.push(numberField('delayMs', '长按时间 (ms)', 0, 60_000, 1));
      else fields.push(selectField('trigger', '触发方式', [['click', '点击'], ['press', '按下']]));
      if (config.kind === 'delayButton' || config.trigger === 'press') fields.push(
        booleanField('sendOnRelease', '松开后发送'), numberField('offValue', '松开参数'),
        numberField('releaseDelayMs', '松开延时 (ms)', 0, 60_000, 1));
      break;
    case 'toggle':
      fields.push(commandField, textField('label', '开关文字'), numberField('onValue', '打开参数'),
        numberField('offValue', '关闭参数'), colorField('color', '颜色'), feedback, channelsField);
      if (config.feedback) fields.push(numberField('threshold', '读回阈值（空白为就近匹配）'), booleanField('invert', '反转读回状态'));
      break;
    case 'slider':
    case 'dial':
    case 'materialDial':
      fields.push(commandField, ...valueRange, numberField('value', '初始值'), ...displayRange, continuous, feedback, channelsField);
      break;
    case 'joystick':
      fields.push(commandField, ...valueRange, numberField('deadZone', '中心死区', 0, 0.9, 0.01),
        booleanField('spring', '松手回中'), booleanField('invertY', '反转 Y 轴'), continuous, colorField('color', '颜色'), feedback, channelsField);
      break;
    case 'light':
      fields.push(channelsField, commandField, numberField('value', '点击参数'), numberField('threshold', '亮起阈值'),
        booleanField('invert', '反转阈值'), booleanField('threeState', '三色模式'),
        colorField('onColor', '亮起颜色'), colorField('offColor', '熄灭颜色'));
      if (config.threeState) fields.push(numberField('thresholdHigh', '高阈值'), colorField('midColor', '中间颜色'));
      break;
    case 'gauge':
    case 'circularGauge':
      fields.push(channelsField, ...valueRange.slice(0, 2), ...displayRange,
        numberField('warningLow', '告警下限'), numberField('warning', '告警上限'),
        booleanField('invertWarning', '反转告警区间'), numberField('ticks', '刻度数', 2, 30, 1));
      if (config.kind === 'circularGauge') fields.push(numberField('startAngle', '起始角度', -360, 360), numberField('endAngle', '终止角度', -360, 360));
      break;
    case 'cube':
      fields.push(selectField('rotation', '姿态格式', [['euler', '欧拉角'], ['quaternion', '四元数']]));
      const axisNames = config.rotation === 'quaternion'
        ? config.quaternionOrder === 'wxyz' ? ['W', 'X', 'Y', 'Z'] : ['X', 'Y', 'Z', 'W']
        : ['X', 'Y', 'Z'];
      for (let i = 0; i < axisNames.length; i++) fields.push(field('channelPosition' + i, axisNames[i] + ' 通道', 'channel', { options: channelOptions }));
      if (config.rotation === 'quaternion') fields.push(selectField('quaternionOrder', '四元数顺序', [['xyzw', 'X Y Z W'], ['wxyz', 'W X Y Z']]));
      else fields.push(
        selectField('rotationOrder', '旋转顺序', ['XYZ', 'XZY', 'YXZ', 'YZX', 'ZXY', 'ZYX'].map(order => [order, order])),
        selectField('angleUnit', '角度单位', [['deg', '度'], ['rad', '弧度']]));
      fields.push(textField('axisMap', '轴映射（如 x,-z,y）'), booleanField('showAxes', '显示世界坐标轴'),
        booleanField('showModelAxes', '显示模型坐标轴'), selectField('viewPreset', '预设视角',
          [['perspective', '斜视'], ['front', '正视'], ['top', '俯视'], ['side', '侧视']]),
        numberField('poseX', '模型 X 偏置（度）'), numberField('poseY', '模型 Y 偏置（度）'), numberField('poseZ', '模型 Z 偏置（度）'),
        colorField('color', '模型颜色'), booleanField('antialias', '多重采样抗锯齿'),
        field('model', 'OBJ / STL 模型', 'file', { accept: '.obj,.stl' }));
      break;
    case 'image': {
      const ids = [...new Set([config.imageId ?? 0, ...Object.values(context.images ?? {}).map(image => image.id)])].sort((a, b) => a - b);
      fields.push(numberField('imageId', ids.length > 1 ? '图像 ID（' + ids.map(id => '#' + id).join('、') + '）' : '图像 ID', 0, 2 ** 31 - 1, 1),
        selectField('format', '像素格式', [['auto', '自动识别'], ['encoded', '编码图像'], ['binary', '单色位图'], ['binary-lsb', '单色位图 LSB'],
          ['gray8', '灰度 8'], ['gray16', '灰度 16'], ['rgb565', 'RGB565'], ['rgb555', 'RGB555'], ['rgb888', 'RGB888'],
          ['bgr888', 'BGR888'], ['rgb32', 'RGB32'], ['argb32', 'ARGB32'], ['rgba8888', 'RGBA8888']]),
        selectField('filter', '滤镜', [['none', '原图'], ['gray', '灰度'], ['invert', '反相'], ['threshold', '二值化'],
          ['blur', '高斯模糊'], ['sharpen', '锐化'], ['sobel', 'Sobel 边缘检测']]),
        selectField('fit', '缩放模式', [['contain', '等比适应'], ['cover', '等比填满'], ['stretch', '拉伸']]),
        booleanField('flipX', '水平镜像'), booleanField('flipY', '垂直镜像'), booleanField('smoothImage', '平滑缩放'));
      if (config.filter === 'threshold') fields.push(numberField('threshold', '二值化阈值', 0, 255, 1));
      break;
    }
  }
  return fields;
}

function finite(value, fallback) {
  return Number.isFinite(value) ? value : fallback;
}

function bounds(config) {
  const low = finite(config.min, 0);
  const high = finite(config.max, 100);
  return high > low ? [low, high] : [Math.min(high, low), Math.max(high, low + (high === low ? 1 : 0))];
}

function constrain(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

export function controlValue(value, config) {
  const [min, max] = bounds(config);
  const step = finite(config.step, 1);
  const limit = constrain(finite(value, min), min, max);
  if (step <= 0 || limit === min || limit === max) return limit;
  const rounded = Number((min + Math.round((limit - min) / step) * step).toPrecision(14));
  return constrain(rounded, min, max);
}

export function joystickPosition(x, y, deadZone) {
  const length = Math.hypot(x, y);
  if (!length || length <= deadZone) return [0, 0];
  if (length <= 1) return [x, y];
  return [x / length, y / length];
}

function reading(config, data, channelIndex = 0) {
  const index = config.channels?.[channelIndex] ?? channelIndex;
  const channel = data.channels?.[index];
  const value = data.latest?.[index];
  return Number.isFinite(value) ? value * finite(channel?.scale, 1) + finite(channel?.offset, 0) : NaN;
}

function formatReading(value, config) {
  const digits = constrain(Math.trunc(finite(config.decimals, 1)), 0, 8);
  return Number.isFinite(value) ? value.toFixed(digits) + (config.unit ? ' ' + config.unit : '') : '—';
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function text(node, value) {
  if (node.textContent !== value) node.textContent = value;
}

function send(config, api, values) {
  if (config.commandId) api.send(config.commandId, values);
  else api.notify?.('请先绑定发送命令');
}

function frameSender(config, api) {
  let frame = 0;
  let values;
  function flush() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    if (values) {
      const next = values;
      values = undefined;
      send(config, api, next);
    }
  }
  return {
    queue(next) {
      values = next;
      if (!frame) frame = requestAnimationFrame(flush);
    },
    flush,
    cancel() {
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
      values = undefined;
    },
  };
}

function mountButton(container, config, api, events) {
  const button = element('button', 'telemetry-action');
  button.type = 'button';
  const delay = config.kind === 'delayButton';
  const footer = element('small', 'telemetry-caption');
  container.append(button, footer);
  const options = { signal: events.signal };
  let held = false;
  let fired = false;
  let holdTimer;
  const releaseTimers = new Set();

  function press() {
    if (held) return;
    held = true;
    fired = false;
    const duration = delay ? constrain(finite(config.delayMs, 500), 0, 60_000) : 0;
    button.classList.add('is-holding');
    button.style.setProperty('--telemetry-delay', duration + 'ms');
    function fire() {
      fired = true;
      send(config, api, [finite(config.value, 1)]);
    }
    if (duration) holdTimer = setTimeout(fire, duration);
    else fire();
  }

  function release() {
    if (!held) return;
    clearTimeout(holdTimer);
    held = false;
    button.classList.remove('is-holding');
    if (fired && config.sendOnRelease) {
      const timeout = setTimeout(() => {
        releaseTimers.delete(timeout);
        send(config, api, [finite(config.offValue, 0)]);
      }, constrain(finite(config.releaseDelayMs, 0), 0, 60_000));
      releaseTimers.add(timeout);
    }
  }

  const pressMode = () => delay || config.trigger === 'press';
  button.addEventListener('pointerdown', event => {
      if (event.button !== 0 || !pressMode()) return;
      button.setPointerCapture(event.pointerId);
      press();
  }, options);
  for (const event of ['pointerup', 'pointercancel', 'lostpointercapture', 'blur']) button.addEventListener(event, release, options);
  button.addEventListener('keydown', event => {
    if (pressMode() && (event.key === ' ' || event.key === 'Enter')) {
      event.preventDefault();
      press();
    }
  }, options);
  button.addEventListener('keyup', event => {
    if (pressMode() && (event.key === ' ' || event.key === 'Enter')) {
      event.preventDefault();
      release();
    }
  }, options);
  button.addEventListener('click', () => {
    if (!pressMode()) send(config, api, [finite(config.value, 1)]);
  }, options);

  function update() {
    text(button, config.label || (delay ? '长按发送' : '发送'));
    text(footer, delay ? '长按 ' + finite(config.delayMs, 500) + ' ms' : config.commandId ? '发送绑定命令' : '未绑定命令');
  }
  update();
  return {
    update,
    dispose() {
      clearTimeout(holdTimer);
      releaseTimers.forEach(clearTimeout);
    },
  };
}

function mountToggle(container, config, api, events) {
  const button = element('button', 'telemetry-switch');
  button.type = 'button';
  button.setAttribute('role', 'switch');
  button.append(element('span', 'telemetry-switch-thumb'));
  const label = element('span', 'telemetry-caption');
  container.append(button, label);
  const isOn = value => Math.abs(value - finite(config.onValue, 1)) < Math.abs(value - finite(config.offValue, 0));
  let active = isOn(finite(config.value, 0));
  function paint() {
    button.setAttribute('aria-checked', String(active));
    button.setAttribute('aria-label', config.label || config.title || '开关');
    text(label, (config.label ? config.label + ' · ' : '') + (active ? '开启' : '关闭'));
  }
  button.addEventListener('click', () => {
    if (!config.commandId) return send(config, api, []);
    active = !active;
    send(config, api, [active ? finite(config.onValue, 1) : finite(config.offValue, 0)]);
    paint();
  }, { signal: events.signal });
  paint();
  return {
    update(data) {
      const value = config.feedback ? reading(config, data) : NaN;
      if (Number.isFinite(value)) {
        const on = Number.isFinite(config.threshold) ? value >= config.threshold : isOn(value);
        active = on !== Boolean(config.invert);
      }
      paint();
    },
  };
}

const svgNamespace = 'http://www.w3.org/2000/svg';
const arc = 'M 15.35 84.65 A 49 49 0 1 1 84.65 84.65';

function warning(value, config) {
  if (!Number.isFinite(value) || !Number.isFinite(config.warning) && !Number.isFinite(config.warningLow)) return false;
  const outside = (Number.isFinite(config.warning) && value >= config.warning)
    || (Number.isFinite(config.warningLow) && value <= config.warningLow);
  return outside !== !!config.invertWarning;
}

function svgElement(tag, props) {
  const node = document.createElementNS(svgNamespace, tag);
  for (const [name, value] of Object.entries(props)) node.setAttribute(name, value);
  return node;
}

function dialFace(className) {
  const body = element('div', 'telemetry-dial ' + className);
  const svg = svgElement('svg', { viewBox: '-6 -6 112 104', 'aria-hidden': 'true' });
  const background = svgElement('path', { d: arc, pathLength: 100, class: 'telemetry-arc-track' });
  const fill = svgElement('path', { d: arc, pathLength: 100, class: 'telemetry-arc-fill' });
  const marks = svgElement('g', { class: 'telemetry-gauge-ticks' });
  svg.append(background, fill, marks);
  const display = element('output', 'telemetry-dial-reading');
  const minLabel = element('small', 'telemetry-dial-min');
  const maxLabel = element('small', 'telemetry-dial-max');
  body.append(svg, display, minLabel, maxLabel);
  let previousGeometry;
  function paint(value, config) {
    const [min, max] = bounds(config);
    const fraction = constrain(finite((value - min) / (max - min), 0), 0, 1);
    fill.style.strokeDasharray = fraction * 100 + ' 100';
    fill.style.opacity = fraction === 0 ? '0' : '1';
    const start = finite(config.startAngle, -135);
    const angle = constrain(finite(config.endAngle, 135) - start, -359.9, 359.9);
    const ticks = Math.trunc(constrain(finite(config.ticks, 5), 2, 30));
    body.style.setProperty('--telemetry-dial-angle', (start + angle * fraction) + 'deg');
    body.classList.toggle('is-warning', warning(value, config));
    const geometry = [start, angle, ticks].join('/');
    if (config.kind === 'circularGauge' && geometry !== previousGeometry) {
      previousGeometry = geometry;
      const point = (degrees, radius) => {
        const radians = degrees * Math.PI / 180;
        return [50 + radius * Math.sin(radians), 50 - radius * Math.cos(radians)];
      };
      const path = 'M ' + point(start, 49).join(' ') + ' A 49 49 0 ' + (Math.abs(angle) > 180 ? 1 : 0) +
        ' ' + (angle >= 0 ? 1 : 0) + ' ' + point(start + angle, 49).join(' ');
      for (const track of [background, fill]) track.setAttribute('d', path);
      marks.replaceChildren(...Array.from({ length: ticks }, (_, i) => {
        const at = start + angle * i / (ticks - 1);
        const [x1, y1] = point(at, 41);
        const [x2, y2] = point(at, 45);
        return svgElement('line', { x1, y1, x2, y2 });
      }));
    }
    text(display, formatReading(value, config));
    text(minLabel, formatReading(min, { decimals: 0 }));
    text(maxLabel, formatReading(max, { decimals: 0 }));
  }
  return { body, paint };
}

function mountRange(container, config, api, events) {
  const slider = config.kind === 'slider';
  const input = element('input', slider ? 'telemetry-range' : 'telemetry-dial-input');
  input.type = 'range';
  input.setAttribute('aria-label', config.title || INSTRUMENTS[config.kind]);
  const valueLabel = element('output', 'telemetry-reading');
  const dial = slider ? null : dialFace(config.kind === 'materialDial' ? 'telemetry-dial-material' : 'telemetry-dial-basic');
  const feedbackLabel = element('small', 'telemetry-caption');
  if (slider) container.append(valueLabel, input, feedbackLabel);
  else {
    dial.body.append(input);
    container.append(dial.body, feedbackLabel);
  }
  const options = { signal: events.signal };
  const sender = frameSender(config, api);
  let value = controlValue(config.value, config);
  let pressed = false;
  let pointer;
  let lastSent;
  function paint(data) {
    const [min, max] = bounds(config);
    input.min = min;
    input.max = max;
    input.step = config.step > 0 ? config.step : 'any';
    input.value = value;
    input.style.setProperty('--telemetry-progress', 100 * (value - min) / (max - min) + '%');
    if (dial) dial.paint(value, config);
    else text(valueLabel, formatReading(value, config));
    const readback = config.feedback && data ? reading(config, data) : NaN;
    if (data) text(feedbackLabel, Number.isFinite(readback) ? '读回 ' + formatReading(readback, config) : config.commandId ? '' : '未绑定命令');
  }
  function set(next, immediate) {
    value = controlValue(next, config);
    paint();
    if ((config.continuous !== false || immediate) && value !== lastSent) {
      lastSent = value;
      sender.queue([value]);
    }
    if (immediate) sender.flush();
  }
  input.addEventListener('input', () => set(input.valueAsNumber, false), options);
  input.addEventListener('change', () => set(input.valueAsNumber, true), options);

  if (dial) {
    input.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      event.preventDefault();
      input.focus();
      input.setPointerCapture(event.pointerId);
      pointer = { x: event.clientX, y: event.clientY, value };
      pressed = true;
    }, options);
    input.addEventListener('pointermove', event => {
      if (!pointer) return;
      const [min, max] = bounds(config);
      const distance = (event.clientX - pointer.x) * 0.5 + pointer.y - event.clientY;
      set(pointer.value + distance * (max - min) / 145, false);
    }, options);
    input.addEventListener('lostpointercapture', () => {
      if (pointer && value !== pointer.value) set(value, true);
      pointer = undefined;
      pressed = false;
    }, options);
    input.addEventListener('wheel', event => {
      if (document.activeElement !== input) return;
      event.preventDefault();
      set(value - Math.sign(event.deltaY) * finite(config.step, 1), true);
    }, { ...options, passive: false });
  } else {
    input.addEventListener('pointerdown', () => { pressed = true; }, options);
    input.addEventListener('lostpointercapture', () => { pressed = false; }, options);
    input.addEventListener('blur', () => { pressed = false; }, options);
    input.addEventListener('pointerup', () => { pressed = false; }, options);
  }

  paint({});
  return {
    update(data) {
      if (config.feedback && !pressed) {
        const incoming = reading(config, data);
        if (Number.isFinite(incoming)) value = controlValue(incoming, config);
      }
      paint(data);
    },
    dispose() { sender.cancel(); },
  };
}

function mountJoystick(container, config, api, events) {
  const pad = element('div', 'telemetry-joystick');
  pad.tabIndex = 0;
  pad.setAttribute('role', 'group');
  pad.setAttribute('aria-label', '摇杆，方向键移动，空格回中');
  const thumb = element('span', 'telemetry-joystick-thumb');
  const label = element('output', 'telemetry-caption');
  const options = { signal: events.signal };
  const sender = frameSender(config, api);
  pad.append(thumb);
  container.append(pad, label);
  let point = [0, 0];
  let activePointer;
  let lastSent;
  function values() {
    const [min, max] = bounds(config);
    return point.map((value, index) => {
      const mapped = index === 1 && config.invertY ? -value : value;
      return controlValue(min + (mapped + 1) / 2 * (max - min), config);
    });
  }
  function paint() {
    thumb.style.left = (50 + point[0] * 36) + '%';
    thumb.style.top = (50 - point[1] * 36) + '%';
    const [x, y] = values();
    text(label, 'X ' + x + ' · Y ' + y);
  }
  function submit(immediate) {
    const next = values();
    if (!lastSent || next.some((value, index) => value !== lastSent[index])) {
      lastSent = next;
      sender.queue(next);
    }
    if (immediate) sender.flush();
  }
  function move(event) {
    const { left, top, width, height } = pad.getBoundingClientRect();
    const radius = Math.min(width, height) * 0.36;
    point = joystickPosition((event.clientX - left - width / 2) / radius, (top + height / 2 - event.clientY) / radius, config.deadZone);
    paint();
    if (config.continuous !== false) submit(false);
  }
  pad.addEventListener('pointerdown', event => {
    if (event.button !== 0 || activePointer !== undefined) return;
    activePointer = event.pointerId;
    pad.setPointerCapture(event.pointerId);
    pad.focus();
    move(event);
  }, options);
  pad.addEventListener('pointermove', event => {
    if (event.pointerId === activePointer) move(event);
  }, options);
  pad.addEventListener('lostpointercapture', event => {
    if (event.pointerId !== activePointer) return;
    activePointer = undefined;
    submit(true);
    if (config.spring !== false) {
      point = [0, 0];
      paint();
      submit(true);
    }
  }, options);
  pad.addEventListener('keydown', event => {
    const moves = { ArrowLeft: [-0.1, 0], ArrowRight: [0.1, 0], ArrowUp: [0, 0.1], ArrowDown: [0, -0.1] };
    if (event.key === ' ') point = [0, 0];
    else if (moves[event.key]) {
      const [x, y] = moves[event.key];
      point = joystickPosition(point[0] + x, point[1] + y, 0);
    } else return;
    event.preventDefault();
    paint();
    submit(true);
  }, options);
  paint();
  return {
    update(data) {
      if (!config.feedback || activePointer !== undefined) return;
      const [min, max] = bounds(config);
      const x = reading(config, data, 0);
      const y = reading(config, data, 1);
      if (!Number.isFinite(x) || !Number.isFinite(y)) return;
      point = joystickPosition((x - min) / (max - min) * 2 - 1, ((y - min) / (max - min) * 2 - 1) * (config.invertY ? -1 : 1), 0);
      paint();
    },
    dispose() { sender.cancel(); },
  };
}

function mountLight(container, config, api, events) {
  const lamp = element('button', 'telemetry-light');
  lamp.type = 'button';
  const label = element('span', 'telemetry-caption');
  lamp.setAttribute('aria-label', config.title || '指示灯');
  lamp.addEventListener('click', () => {
    if (config.commandId) send(config, api, [finite(config.value, 1)]);
  }, { signal: events.signal });
  container.append(lamp, label);
  function update(data) {
    const value = reading(config, data);
    const low = finite(config.threshold, 0.5);
    const high = Math.max(low, finite(config.thresholdHigh, 0.8));
    let level = !Number.isFinite(value) ? 0 : config.threeState && value >= low && value < high ? 1 : value >= (config.threeState ? high : low) ? 2 : 0;
    if (Number.isFinite(value) && config.invert) level = 2 - level;
    lamp.classList.toggle('is-on', level > 0);
    lamp.disabled = !config.commandId;
    lamp.style.setProperty('--telemetry-lamp', [config.offColor, config.midColor, config.onColor][level]);
    text(label, Number.isFinite(value) ? ['灭', '中间', '亮'][level] + ' · ' + formatReading(value, { decimals: 3 }) : '等待数据');
  }
  update({});
  return { update };
}

function mountGauge(container, config) {
  if (config.kind === 'circularGauge') {
    const dial = dialFace('telemetry-dial-gauge');
    container.append(dial.body);
    dial.paint(NaN, config);
    return { update(data) { dial.paint(reading(config, data), config); } };
  }
  const output = element('output', 'telemetry-reading');
  const gauge = element('div', 'telemetry-level');
  gauge.setAttribute('role', 'meter');
  gauge.setAttribute('aria-label', config.title || '仪表');
  const fill = element('span', 'telemetry-level-fill');
  gauge.append(fill);
  const minLabel = element('span', 'telemetry-level-min');
  const maxLabel = element('span', 'telemetry-level-max');
  const markers = element('div', 'telemetry-level-markers');
  markers.append(minLabel, maxLabel);
  container.append(output, gauge, markers);
  function update(data) {
    const value = reading(config, data);
    const [min, max] = bounds(config);
    const limit = constrain(finite(value, min), min, max);
    fill.style.width = 100 * (limit - min) / (max - min) + '%';
    gauge.classList.toggle('is-warning', warning(value, config));
    gauge.style.setProperty('--ticks', Math.trunc(constrain(finite(config.ticks, 5), 2, 30)) - 1);
    gauge.setAttribute('aria-valuemin', min);
    gauge.setAttribute('aria-valuemax', max);
    gauge.setAttribute('aria-valuenow', limit);
    text(output, formatReading(value, config));
    text(minLabel, formatReading(min, { decimals: 0 }));
    text(maxLabel, formatReading(max, { decimals: 0 }));
  }
  update({});
  return { update };
}

function mountWave(container, config, api) {
  const main = element('div', 'telemetry-plot-pane');
  container.append(main);
  const chart = mountChart(main, config, api);
  let overlay;
  let sub;
  let subConfig;
  return {
    update(data) {
      chart.update(data);
      const kind = ['time', 'xy'].includes(config.mode) ? config.analysisOverlay : 'none';
      if (!['bar', 'histogram', 'spectrum'].includes(kind)) {
        sub?.dispose();
        overlay?.remove();
        overlay = sub = undefined;
        return;
      }
      if (!sub) {
        overlay = element('div', 'telemetry-plot-pane telemetry-plot-secondary');
        container.append(overlay);
        subConfig = {};
        sub = mountChart(overlay, subConfig, {
          ...api, change: patch => api.change({ analysisOverlay: patch.mode })
        }, { analysisOnly: true });
      }
      Object.assign(subConfig, config, { mode: kind, xAxisAuto: true, axisAuto: true, xUnit: '', yUnit: '' });
      sub.update(data);
    },
    dispose() {
      chart.dispose();
      sub?.dispose();
    }
  };
}

export function mountInstrument(container, config, api) {
  const body = element('div', 'telemetry-instrument telemetry-kind-' + config.kind);
  body.style.setProperty('--telemetry-accent', config.color || '#38bdf8');
  container.append(body);
  const events = new AbortController();
  const mounts = {
    wave: mountWave, cube: mountCube, image: mountImage,
    button: mountButton, delayButton: mountButton, toggle: mountToggle,
    slider: mountRange, dial: mountRange, materialDial: mountRange, joystick: mountJoystick,
    light: mountLight, gauge: mountGauge, circularGauge: mountGauge,
  };
  const widget = mounts[config.kind]?.(body, config, api, events);
  return {
    update(data) {
      body.style.setProperty('--telemetry-accent', config.color || '#38bdf8');
      widget?.update(data);
    },
    dispose() {
      events.abort();
      widget?.dispose?.();
      body.remove();
    },
  };
}
