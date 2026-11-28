const translations = {
  '实时波形': 'Live Signals', '运行示例': 'Run demo',
  '实时监测': 'Live monitoring', '控件': 'Widget', '命令': 'Commands', '终端': 'Terminal', '通道': 'Channel', '设置': 'Settings',
  '连接类型': 'Connection type', '串口设备': 'Serial device', '协议引擎': 'Protocol',
  '保存本次采集的全部数据': 'Save the full capture', '工作台视图': 'Dashboard views', '标签选项': 'Tab options',
  '锁定布局': 'Lock layout', '仪表画布': 'Instrument canvas', '暂停或跟随最新': 'Pause or follow latest',
  '采样位置': 'Sample position', '可视点数': 'Visible points', '采样参数': 'Sampling settings', '选择波形图': 'Select waveform',
  '上一页通道': 'Previous channel page', '下一页通道': 'Next channel page', 'RX TX 终端': 'RX/TX terminal',
  '拖动调整终端高度': 'Drag to resize the terminal', '显示接收': 'Show received', '显示发送': 'Show sent',
  '十六进制显示': 'Show hexadecimal', '上一页': 'Previous page', '下一页': 'Next page', '复制当前页': 'Copy current page',
  '接收和发送日志': 'Receive and send logs', '发送格式': 'Send format', '键入下行命令…': 'Enter a command…',
  '发送内容': 'Command text', '发送原始文件': 'Send raw file', '停止连续发送': 'Stop sending',
  '配置已载入': 'Configuration loaded', '配置已导出': 'Configuration exported', '至少保留一个标签': 'Keep at least one tab',
  '拖入画布，或点击添加。右键设置通道与命令。': 'Drag to the canvas or click to add. Right-click to configure channels and commands.',
  '从控件库添加一个仪表，开始搭建监测面板。': 'Add an instrument from the library to build your dashboard.',
  '控件扩展未加载': 'Widget extension is unavailable', '绑定命令': 'Bind command', '此通道已全局隐藏': 'This channel is hidden globally',
  '连接设备或运行 Demo 后显示通道。': 'Connect a device or run the demo to see channels.',
  '等待收发数据。可在终端设置中显示数值帧和图片帧。': 'Waiting for traffic. Enable numeric and image frames in Terminal settings.',
  '截图已保存': 'Screenshot saved', '暂无发送记录': 'No send history', '监测': 'Monitor',
  '选择串口设备': 'Select a serial device', '未检测到串口': 'No serial ports detected',
  '波形与数据分析': 'Waveforms and analysis', '图内滚轮缩放双轴，轴上滚轮缩放单轴，拖动平移，双击设置游标': 'Scroll to zoom, scroll on an axis to zoom that axis, drag to pan, or double-click to place a cursor',
  '图表类型': 'Chart type', '直方图': 'Histogram', '柱状图': 'Bar chart', '自动量程并回到实时': 'Auto range and follow latest',
  '选择成对的 X / Y 通道': 'Select paired X/Y channels', '未选择显示通道': 'No channels selected',
  '该区间缺少连续采样': 'Not enough contiguous samples', '等待通道数据': 'Waiting for channel data',
  '最小': 'Min', '最大': 'Max', '均值': 'Mean', '峰峰': 'Peak-to-peak', '占空': 'Duty', '频率': 'Frequency',
  '添加游标': 'Add cursor', '清除游标': 'Clear cursors',
  '端口': 'Port', '连接后发送': 'Send on connect', '监听地址': 'Listen address', '监听端口': 'Listen port', '本地地址': 'Local address',
  '远端地址': 'Remote address', '数据源': 'Data source', '组发送时跳过': 'Skip when sending group', '编辑命令组': 'Edit command group',
  '编辑命令': 'Edit command', '命令组': 'Command group', '编辑': 'Edit', '带参数发送': 'Send with parameters',
  '参数用逗号分隔，最多 10 个。Hex 字节参数用 0x 前缀。': 'Separate up to 10 parameters with commas. Prefix hexadecimal bytes with 0x.',
  '上移': 'Move up', '下移': 'Move down', '删除': 'Delete', '创建命令后可绑定到按钮、滑条、摇杆或状态灯。': 'Create commands to bind to buttons, sliders, joysticks, or indicators.',
  '自定义主题': 'Custom theme',
  '开关': 'Toggle', '滑块': 'Slider', '仪表': 'Gauge', '3D 姿态': '3D attitude', '图像': 'Image', '长按发送': 'Hold to send',
  '最小值': 'Minimum', '最大值': 'Maximum', '步长': 'Step', '单位': 'Unit', '从数据通道读回': 'Read back from channel',
  '调整时连续发送': 'Send continuously while adjusting', '数据通道': 'Data channels', '发送命令': 'Send command',
  '显示采样数': 'Visible samples', '网格': 'Grid', '图例': 'Legend', '线宽': 'Line width', '绘制方式': 'Drawing style',
  '连线': 'Line', '点': 'Points', '显示 X 轴': 'Show X axis', '显示 Y 轴': 'Show Y axis', 'X 轴单位': 'X-axis unit',
  'Y 轴单位': 'Y-axis unit', 'X 轴自动范围': 'Auto X range', 'X 轴下限': 'X minimum', 'X 轴上限': 'X maximum',
  'Y 轴自动范围': 'Auto Y range', 'Y 轴下限': 'Y minimum', 'Y 轴上限': 'Y maximum', '自动配对': 'Automatic pairing',
  '指定 X 通道': 'X channel', '指定 Y 通道': 'Y channel', 'XY 配对（如 0:1,2:3）': 'XY pairs (e.g. 0:1,2:3)',
  '同步显示分析': 'Analysis overlay', '直方图组数': 'Histogram bins', '直方图下限': 'Histogram minimum', '直方图上限': 'Histogram maximum',
  'FFT 点数': 'FFT points', '自动': 'Automatic', '条形基线': 'Bar baseline', '波形测量': 'Waveform measurements',
  '占空比阈值（空白为自动）': 'Duty-cycle threshold (blank = auto)', '按钮文字': 'Button text', '参数值': 'Parameter value',
  '长按时间 (ms)': 'Hold duration (ms)', '触发方式': 'Trigger', '点击': 'Click', '按下': 'Press', '松开后发送': 'Send on release',
  '松开参数': 'Release parameter', '松开延时 (ms)': 'Release delay (ms)', '开关文字': 'Toggle text', '打开参数': 'On parameter',
  '关闭参数': 'Off parameter', '读回阈值（空白为就近匹配）': 'Readback threshold (blank = nearest match)', '反转读回状态': 'Invert readback',
  '初始值': 'Initial value', '中心死区': 'Center dead zone', '松手回中': 'Return to center', '反转 Y 轴': 'Invert Y axis', '点击参数': 'Click parameter',
  '亮起阈值': 'On threshold', '反转阈值': 'Invert threshold', '三色模式': 'Three-color mode', '亮起颜色': 'On color', '熄灭颜色': 'Off color',
  '高阈值': 'High threshold', '中间颜色': 'Middle color', '告警下限': 'Alarm minimum', '告警上限': 'Alarm maximum',
  '反转告警区间': 'Invert alarm range', '刻度数': 'Tick count', '起始角度': 'Start angle', '终止角度': 'End angle',
  '姿态格式': 'Attitude format', '欧拉角': 'Euler angles', '四元数': 'Quaternion', '四元数顺序': 'Quaternion order', '旋转顺序': 'Rotation order',
  '角度单位': 'Angle unit', '度': 'Degrees', '弧度': 'Radians', '轴映射（如 x,-z,y）': 'Axis mapping (e.g. x,-z,y)',
  '显示世界坐标轴': 'Show world axes', '显示模型坐标轴': 'Show model axes', '预设视角': 'Camera view', '斜视': 'Perspective',
  '正视': 'Front', '俯视': 'Top', '侧视': 'Side', '模型 X 偏置（度）': 'Model X offset (deg)', '模型 Y 偏置（度）': 'Model Y offset (deg)',
  '模型 Z 偏置（度）': 'Model Z offset (deg)', '模型颜色': 'Model color', '多重采样抗锯齿': 'Multisample antialiasing', 'OBJ / STL 模型': 'OBJ / STL model',
  '图像 ID': 'Image ID', '像素格式': 'Pixel format', '自动识别': 'Auto detect', '编码图像': 'Encoded image', '单色位图': 'Monochrome bitmap',
  '单色位图 LSB': 'Monochrome bitmap (LSB)', '灰度 8': '8-bit grayscale', '灰度 16': '16-bit grayscale', '滤镜': 'Filter',
  '原图': 'Original', '灰度': 'Grayscale', '反相': 'Invert', '二值化': 'Threshold', '高斯模糊': 'Gaussian blur', '锐化': 'Sharpen',
  'Sobel 边缘检测': 'Sobel edge detection', '缩放模式': 'Scale mode', '等比适应': 'Fit', '等比填满': 'Fill', '拉伸': 'Stretch',
  '水平镜像': 'Flip horizontally', '垂直镜像': 'Flip vertically', '平滑缩放': 'Smooth scaling', '二值化阈值': 'Threshold level',
  '请先绑定发送命令': 'Bind a command first', '长按': 'Hold', '发送绑定命令': 'Send bound command', '未绑定命令': 'No command bound',
  '读回': 'Readback', '摇杆，方向键移动，空格回中': 'Joystick. Arrow keys move, Space centers.', '灭': 'Off', '中间': 'Middle', '亮': 'On',
  '等待数据': 'Waiting for data', '等待姿态通道': 'Waiting for attitude channels', '世界轴': 'World axes', '模型轴': 'Model axes',
  '请选择 OBJ 或 STL 文件': 'Select an OBJ or STL file', '模型缺少可绘制的几何体': 'Model has no geometry to display',
  'WebGL 不可用 · 内置立方体': 'WebGL unavailable · built-in cube', 'WebGL 不可用 · 立方体预览': 'WebGL unavailable · cube preview',
  '模型载入失败': 'Model load failed', '图像通道': 'Image channel', '等待图像': 'Waiting for image',
  '协议扩展通过 VS Code 扩展 API 注册。已加载：': 'Protocol extensions register through the VS Code extension API. Loaded:',
  '仅内建引擎': 'Built-in engines only', '自定义仪表名称': 'Custom instrument name', '保存并启用': 'Save and enable',
  '信任工作区后可以启用自定义仪表。': 'Trust the workspace to enable custom instruments.',
  '仪表代码在隔离的 iframe 内运行。可使用 output、onSamples(fn) 和 send(params)。发送仅在控件绑定命令后可用。': 'Instrument code runs in an isolated frame. Use output, onSamples(fn), and send(params). Sending requires a bound command.',
  '连接设置': 'Connection settings', '连接': 'Connect', '连接中': 'Connecting', '断开': 'Disconnect', '离线': 'Offline',
  '在线': 'Online', '保存数据': 'Save data', '自动采集中': 'Capturing', '已采集': 'Captured',
  '数据已保存': 'Data saved', '文件': 'File', '视图': 'View',
  '载入采样 CSV': 'Load sample CSV', '导出采样 CSV': 'Export samples', '导出 CSV（含名义时间）': 'Export timed CSV',
  '载入配置 / VOFA+ 1.3': 'Load configuration / VOFA+ 1.3', '导出命令树': 'Export commands', '导出当前标签': 'Export tab',
  '导出窗口布局': 'Export window layout', '导出全部配置': 'Export all settings', '保存画布截图': 'Save dashboard screenshot',
  '工具侧栏': 'Tools', '通道检查器': 'Channel inspector', '串口终端': 'Serial terminal',
  '外观与性能': 'Appearance and performance',
  '协议和控件扩展': 'Protocol and widget extensions', 'VOFA+ 协议文档': 'VOFA+ protocol documentation', '可视点': 'Points',
  '检查器': 'Inspector', '实时通道': 'Live channels', '数据终端': 'Data terminal', '清屏': 'Clear', '发送': 'Send',
  '全选': 'Select all', '全不选': 'Clear all', '在控件设置中编辑 XY 配对': 'Edit XY pairs in widget settings',
  '控件库': 'Instruments', '命令树': 'Commands', '连接与设备': 'Connections', '添加命令': 'Add command',
  '添加组': 'Add group', '串口': 'Serial', '本地端口': 'Local port', '远端端口': 'Remote port', '服务器地址': 'Host',
  '串口号': 'Serial port', '波特率': 'Baud rate', '数据位': 'Data bits', '校验': 'Parity', '停止位': 'Stop bits',
  '流控': 'Flow control', '无': 'None', '硬件': 'Hardware', '软件': 'Software', '刷新': 'Refresh', '文件夹': 'Folder',
  '期望通道数（0 自动）': 'Expected channels (0 auto)', '采样周期（ms）': 'Sample period (ms)', '通道数': 'Channels',
  '保存': 'Save', '取消': 'Cancel', '关闭': 'Close', '名称': 'Name', '注释': 'Comment', '模式': 'Mode',
  '新建标签': 'New tab', '重命名': 'Rename', '复制标签': 'Duplicate tab', '关闭标签': 'Close tab',
  '全屏控件': 'Maximize widget', '设置控件': 'Configure widget', '移除控件': 'Remove widget', '清空采样': 'Clear samples',
  '采样与时间轴': 'Samples and timebase', '缓冲容量 /ch': 'Buffer capacity /ch', '名义 Δt（ms）': 'Nominal Δt (ms)',
  '可视点对齐': 'Window point increment', '显示值': 'Display value', '通道名': 'Channel name', '颜色': 'Color',
  '显示增益': 'Display scale', 'Y 偏置': 'Y offset', 'X 偏置': 'X offset', '小数位': 'Decimals', '可见': 'Visible',
  '文本': 'Text', '换行': 'Line ending', '发送次数': 'Repetitions', '发送间隔（ms）': 'Send interval (ms)',
  '显示 RX': 'Show RX', '显示 TX': 'Show TX', '显示数据帧': 'Show numeric frames', '显示图片帧': 'Show image frames',
  '接收合并（ms）': 'Receive grouping (ms)', '时间戳': 'Timestamps', '编码': 'Encoding', '每页行数': 'Rows per page',
  '保留页数': 'Page history', '文本字号': 'Text size', 'ANSI 颜色': 'ANSI colors', 'Enter 发送': 'Enter to send',
  '主题': 'Theme', '跟随系统': 'Follow VS Code light/dark', '深色': 'Dark', '浅色': 'Light', '界面缩放': 'UI scale',
  '界面字体': 'UI font', '刷新上限（0 随屏幕）': 'FPS limit (0 = display)', '语言': 'Language', '背景图': 'Background image',
  '选择文件': 'Choose file', '清除': 'Clear', '复位外观': 'Restore appearance', '克隆当前主题': 'Clone current theme',
  '编辑颜色': 'Edit colors', '导出主题': 'Export theme', '发送历史': 'Send history', '终端设置': 'Terminal settings',
  '波形图': 'Waveform', '波形': 'Waveform', '按钮': 'Button', '延时按钮': 'Delay button', '切换按钮': 'Toggle button',
  '滑条': 'Slider', '旋钮': 'Dial', '材质旋钮': 'Material dial', '摇杆': 'Joystick', '指示灯': 'LED', '线性仪表': 'Linear gauge',
  '圆形仪表': 'Circular gauge', '三维姿态': '3D attitude', '图片': 'Image', '标题': 'Title'
};

let language = 'zh';
export const $ = selector => document.querySelector(selector);
export const $$ = selector => [...document.querySelectorAll(selector)];
export const t = text => language === 'en' && Object.hasOwn(translations, text) ? translations[text] : text;

export function setLanguage(value) {
  language = value;
  document.documentElement.lang = value === 'en' ? 'en' : 'zh-CN';
  for (const el of $$('[data-t]')) el.textContent = t(el.dataset.t);
  for (const el of $$('[data-placeholder]')) el.placeholder = t(el.dataset.placeholder);
  for (const el of $$('[data-title], [data-label]')) {
    if (el.dataset.title) el.title = t(el.dataset.title);
    if (el.dataset.label) el.setAttribute('aria-label', t(el.dataset.label));
  }
}

export function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

export function button(text, action, className, title) {
  const node = el('button', className, t(text));
  node.type = 'button';
  if (title) node.title = t(title);
  node.addEventListener('click', action);
  return node;
}

export function options(node, entries, current) {
  node.replaceChildren(...entries.map(([value, label]) => new Option(t(label), String(value))));
  node.value = String(current ?? '');
  return node;
}

export function field(spec, value, change, choose) {
  const wrapper = el('label', 'field' + (spec.type === 'textarea' || spec.type === 'channels' ? ' wide' : ''));
  wrapper.append(el('span', '', t(spec.label)));
  let input;
  if (spec.type === 'file') {
    const row = el('div', 'field-actions');
    row.append(button(value?.name || '选择文件', async () => {
      const selected = await choose(spec);
      if (selected) change(selected);
    }), button('×', () => change(null), '', '清除'));
    wrapper.append(row);
    return wrapper;
  }
  if (spec.options || ['select', 'channels'].includes(spec.type)) {
    input = el('select');
    const entries = spec.options ?? [];
    options(input, entries, value);
    if (spec.type === 'channels') {
      input.multiple = true;
      for (const option of input.options) option.selected = Array.isArray(value) && value.some(item => String(item) === option.value);
    }
    input.addEventListener('change', () => {
      const findValue = option => entries.find(([key]) => String(key) === option.value)?.[0];
      change(input.multiple ? [...input.selectedOptions].map(findValue) : findValue(input));
    });
  } else {
    input = el(spec.type === 'textarea' ? 'textarea' : 'input');
    if (spec.type !== 'textarea') input.type = spec.type || 'text';
    if (spec.type === 'number') input.step = spec.step ?? 'any';
    for (const key of ['min', 'max', 'step', 'placeholder']) if (spec[key] !== undefined) input[key] = spec[key];
    if (spec.type === 'checkbox') input.checked = !!value;
    else input.value = value ?? '';
    input.addEventListener('change', () => {
      if (!input.checkValidity()) { input.reportValidity(); return; }
      change(input.type === 'checkbox' ? input.checked : input.type === 'number'
        ? (input.value === '' ? undefined : input.valueAsNumber) : input.value);
    });
  }
  input.setAttribute('aria-label', t(spec.label));
  wrapper.append(input);
  return wrapper;
}

export function form(specs, values, onChange, choose = async () => undefined) {
  const node = el('div', 'form-grid');
  for (const spec of specs) node.append(field(spec, values[spec.key], value => onChange(spec.key, value), choose));
  return node;
}

export function modal(title, body, buttons = []) {
  const dialog = $('#dialog');
  $('#dialogTitle').textContent = t(title);
  $('#dialogBody').replaceChildren(body);
  $('#dialogFoot').replaceChildren(...buttons, button('关闭', () => dialog.close()));
  if (!dialog.open) dialog.showModal();
  return dialog;
}

export function contextMenu(event, items) {
  event.preventDefault();
  const menu = $('#contextMenu');
  menu.replaceChildren(...items.map(([text, fn]) => button(text, () => {
    menu.hidden = true;
    fn();
  })));
  menu.hidden = false;
  const { clientX, clientY } = event;
  menu.style.left = Math.max(3, Math.min(clientX, innerWidth - menu.offsetWidth - 3)) + 'px';
  menu.style.top = Math.max(3, Math.min(clientY, innerHeight - menu.offsetHeight - 3)) + 'px';
}

const ansi = ['#777', '#ff867f', '#77e3a2', '#f1cb73', '#87aaff', '#dfa8ff', '#7ae7e2', '#ededed'];

export function logContent(text, styled) {
  const content = document.createDocumentFragment();
  const pattern = /\x1b\[([\d;]*)([A-Za-z])/g;
  let offset = 0;
  let color = '';
  let bold = false;
  const append = raw => {
    if (!raw) return;
    const span = el('span', '', raw.replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, c => '␛' + c.charCodeAt(0).toString(16).padStart(2, '0')));
    if (styled && color) span.style.color = color;
    if (styled && bold) span.style.fontWeight = '700';
    content.append(span);
  };
  for (const match of text.matchAll(pattern)) {
    append(text.slice(offset, match.index));
    offset = match.index + match[0].length;
    if (match[2] !== 'm') continue;
    for (const part of match[1].split(';').map(Number)) {
      if (part === 0) { color = ''; bold = false; }
      else if (part === 1) bold = true;
      else if (part === 22) bold = false;
      else if (part === 39) color = '';
      else if (part >= 30 && part <= 37) color = ansi[part - 30];
      else if (part >= 90 && part <= 97) color = ansi[part - 90];
    }
  }
  append(text.slice(offset));
  return content;
}
