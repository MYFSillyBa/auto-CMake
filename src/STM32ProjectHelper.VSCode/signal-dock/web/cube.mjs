import {
  AmbientLight, AxesHelper, Box3, BoxGeometry, DirectionalLight, Euler, Group, Matrix4,
  Mesh, MeshStandardMaterial, PerspectiveCamera, Quaternion, Scene, Vector3, WebGLRenderer,
} from 'three';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { STLLoader } from 'three/addons/loaders/STLLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { decodeBase64 } from './image.mjs';
import { t } from './ui.mjs';

const axes = { x: 0, y: 1, z: 2 };
const orders = new Set(['XYZ', 'XZY', 'YXZ', 'YZX', 'ZXY', 'ZYX']);
const components = ['x', 'y', 'z'];
const axisColors = ['#ff9781', '#a8eb80', '#8fc4ff'];
const worldAxisLength = 1.75;
const modelAxisLength = 1.55;
const cameraPresets = {
  perspective: { position: [4.4, 4.4, 4.4], up: [0, 0, 1] },
  front: { position: [0, 0, 7], up: [0, 1, 0] },
  top: { position: [0, 7, 0.0001], up: [0, 1, 0] },
  side: { position: [7, 0, 0], up: [0, 1, 0] }
};

export function orientation(config, values, channels = [], target = new Quaternion()) {
  const order = orders.has(config.rotationOrder) ? config.rotationOrder : 'XYZ';
  const quaternion = config.rotation === 'quaternion';
  const selected = config.channels?.length ? config.channels : quaternion ? [0, 1, 2, 3] : [0, 1, 2];
  const coordinates = selected.slice(0, quaternion ? 4 : 3).map(index => {
    const channel = channels[index];
    return Number.isFinite(values[index]) ? values[index] * (channel?.scale ?? 1) + (channel?.offset ?? 0) : NaN;
  });
  if (quaternion && config.quaternionOrder === 'wxyz') coordinates.push(coordinates.shift());
  if (coordinates.length < (quaternion ? 4 : 3) || !coordinates.every(Number.isFinite)) return null;

  const tokens = String(config.axisMap ?? 'xyz').toLowerCase().match(/-?[xyz]/g) ?? components;
  const [x, y, z] = components.map((axis, index) => {
    const token = tokens[index] ?? axis;
    return coordinates[axes[token.at(-1)]] * (token.startsWith('-') ? -1 : 1);
  });
  if (quaternion) {
    target.set(x, y, z, coordinates[3]);
    return target.lengthSq() > 1e-16 ? target.normalize() : null;
  }
  const conversion = config.angleUnit === 'rad' ? 1 : Math.PI / 180;
  return target.setFromEuler(new Euler(x * conversion, y * conversion, z * conversion, order));
}

function disposeModel(model) {
  const materials = new Set();
  const geometries = new Set();
  model.traverse(node => {
    if (node.geometry) geometries.add(node.geometry);
    if (Array.isArray(node.material)) node.material.forEach(material => materials.add(material));
    else if (node.material) materials.add(node.material);
  });
  geometries.forEach(geometry => geometry.dispose());
  materials.forEach(material => material.dispose());
}

function defaultModel(color) {
  const colors = [color, '#2375a2', '#6ce6cf', '#25786a', '#a8ddff', '#31556f'];
  return new Mesh(new BoxGeometry(1.5, 1.5, 1.5), colors.map(value =>
    new MeshStandardMaterial({ color: value || '#38bdf8', roughness: 0.6 })));
}

function loadModel(source, color) {
  const bytes = decodeBase64(source.data.replace(/^data:[^,]+,/, ''));
  let model;
  if (/\.stl$/i.test(source.name)) {
    const geometry = new STLLoader().parse(bytes.buffer);
    geometry.computeVertexNormals();
    model = new Mesh(geometry, new MeshStandardMaterial({ color, roughness: 0.62 }));
  } else if (/\.obj$/i.test(source.name)) {
    model = new OBJLoader().parse(new TextDecoder().decode(bytes));
  } else {
    throw new Error(t('请选择 OBJ 或 STL 文件'));
  }
  const bounds = new Box3().setFromObject(model);
  const extent = bounds.getSize(new Vector3());
  const size = Math.max(extent.x, extent.y, extent.z);
  if (!Number.isFinite(size) || size <= 0) {
    disposeModel(model);
    throw new Error(t('模型缺少可绘制的几何体'));
  }
  model.position.copy(bounds.getCenter(new Vector3()).negate());
  const normalized = new Group();
  normalized.add(model);
  normalized.scale.setScalar(2.1 / size);
  return normalized;
}

function fallbackCube(container, config) {
  const stage = document.createElement('div');
  stage.className = 'telemetry-cube-fallback';
  const cube = document.createElement('div');
  cube.className = 'telemetry-css-cube';
  for (const name of ['+Z', '−Z', '+X', '−X', '+Y', '−Y']) {
    const face = document.createElement('span');
    face.textContent = name;
    cube.append(face);
  }
  const caption = document.createElement('span');
  caption.className = 'telemetry-cube-caption';
  stage.append(cube, caption);
  container.append(stage);
  const matrix = new Matrix4();
  const rotation = new Quaternion();
  return {
    update(data) {
      const attitude = orientation(config, data.latest ?? [], data.channels, rotation);
      if (attitude) cube.style.transform = 'matrix3d(' + matrix.makeRotationFromQuaternion(attitude).elements.join(',') + ')';
      caption.textContent = t(config.model?.name ? 'WebGL 不可用 · 内置立方体' : 'WebGL 不可用 · 立方体预览');
    },
    dispose() { container.replaceChildren(); },
  };
}

function mountAxisLabels(container, worldAxes, modelAxes) {
  const overlay = document.createElement('div');
  overlay.className = 'telemetry-axis-labels';
  const groups = [
    { helper: worldAxes, space: 'world', length: worldAxisLength },
    { helper: modelAxes, space: 'model', length: modelAxisLength },
  ];
  for (const group of groups) {
    group.labels = components.map((axis, index) => {
      const label = document.createElement('span');
      label.className = 'telemetry-axis-label';
      label.dataset.axis = axis.toUpperCase();
      label.dataset.space = group.space;
      label.style.setProperty('--axis-color', axisColors[index]);
      label.setAttribute('aria-label', t(group.space === 'world' ? '世界轴' : '模型轴') + ' ' + label.dataset.axis);
      label.hidden = true;
      overlay.append(label);
      return label;
    });
  }
  container.append(overlay);
  const origin = new Vector3();
  const tip = new Vector3();
  const clamp = (value, limit) => Math.max(14, Math.min(limit - 14, value));

  return (camera, width, height) => {
    const both = worldAxes.visible && modelAxes.visible;
    for (const group of groups) {
      const { helper, space, length, labels } = group;
      origin.set(0, 0, 0).applyMatrix4(helper.matrixWorld).project(camera);
      for (const [index, label] of labels.entries()) {
        tip.set(0, 0, 0).setComponent(index, length).applyMatrix4(helper.matrixWorld).project(camera);
        label.hidden = !helper.visible || Math.abs(tip.z) > 1 || Math.abs(tip.x) > 1 || Math.abs(tip.y) > 1;
        if (label.hidden) continue;
        const text = label.dataset.axis + (both && space === 'model' ? 'ₘ' : '');
        if (label.textContent !== text) label.textContent = text;
        const dx = (tip.x - origin.x) * width / 2;
        const dy = (origin.y - tip.y) * height / 2;
        const distance = Math.hypot(dx, dy);
        const ux = distance >= 1 ? dx / distance : 1;
        const uy = distance >= 1 ? dy / distance : 0;
        const side = both && space === 'model' ? 16 : 0;
        const x = clamp((1 + tip.x) * width / 2 + 10 * ux - side * uy, width);
        const y = clamp((1 - tip.y) * height / 2 + 10 * uy + side * ux, height);
        label.style.transform = 'translate(' + Math.round(x) + 'px, ' + Math.round(y) + 'px) translate(-50%, -50%)';
      }
    }
  };
}

export function mountCube(container, config) {
  let renderer;
  try {
    renderer = new WebGLRenderer({ alpha: true, antialias: config.antialias !== false, preserveDrawingBuffer: true });
  } catch {
    return fallbackCube(container, config);
  }
  renderer.setPixelRatio(Math.min(2, globalThis.devicePixelRatio || 1));
  renderer.domElement.className = 'telemetry-cube';
  renderer.domElement.setAttribute('aria-label', t('三维姿态'));
  const caption = document.createElement('span');
  caption.className = 'telemetry-cube-caption';
  container.append(renderer.domElement);

  const scene = new Scene();
  const camera = new PerspectiveCamera(37, 1, 0.1, 100);
  scene.add(new AmbientLight('#b9dbff', 2));
  const mainLight = new DirectionalLight('#ffffff', 2.2);
  mainLight.position.set(4, 6, 3);
  scene.add(mainLight);
  const stage = new Group();
  const axesHelper = new AxesHelper(worldAxisLength);
  const modelAxes = new AxesHelper(modelAxisLength);
  axesHelper.visible = config.showAxes !== false;
  modelAxes.visible = !!config.showModelAxes;
  stage.add(modelAxes);
  scene.add(axesHelper, stage);
  const updateAxisLabels = mountAxisLabels(container, axesHelper, modelAxes);
  container.append(caption);
  let orbit;

  const rotation = new Quaternion();
  let model;
  let modelSource;
  let modelColor;
  let modelError;
  let currentPreset;
  let currentPose;
  let renderId = 0;
  let width = 1;
  let height = 1;
  function schedule() {
    if (!renderId) {
      renderId = requestAnimationFrame(() => {
        renderId = 0;
        renderer.render(scene, camera);
        updateAxisLabels(camera, width, height);
      });
    }
  }

  function updateCamera() {
    const name = Object.hasOwn(cameraPresets, config.viewPreset) ? config.viewPreset : 'perspective';
    if (name === currentPreset) return;
    currentPreset = name;
    const { position, up } = cameraPresets[name];
    orbit?.dispose();
    camera.up.set(...up);
    camera.position.set(...position);
    // OrbitControls captures the camera's up direction when constructed.
    orbit = new OrbitControls(camera, renderer.domElement);
    orbit.enablePan = false;
    orbit.minDistance = 2.7;
    orbit.maxDistance = 25;
    orbit.addEventListener('change', schedule);
    schedule();
  }
  const observer = new ResizeObserver(() => {
    width = Math.max(1, container.clientWidth);
    height = Math.max(1, container.clientHeight);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
    renderer.setSize(width, height, false);
    schedule();
  });
  observer.observe(container);

  function updateModel() {
    const next = config.model?.data ? config.model : null;
    const color = config.color || '#38bdf8';
    if (next === modelSource && color === modelColor) return;
    if (model) {
      stage.remove(model);
      disposeModel(model);
    }
    modelSource = next;
    modelColor = color;
    currentPose = undefined;
    modelError = '';
    try {
      model = next ? loadModel(next, color) : defaultModel(color);
    } catch (error) {
      modelError = t('模型载入失败') + ' · ' + error.message;
      model = defaultModel(color);
    }
    stage.add(model);
    schedule();
  }
  updateModel();
  updateCamera();
  return {
    update(data) {
      updateModel();
      updateCamera();
      const pose = [config.poseX, config.poseY, config.poseZ].map(value => (Number(value) || 0) * Math.PI / 180);
      if (pose.join('/') !== currentPose) {
        currentPose = pose.join('/');
        model.rotation.set(...pose);
        modelAxes.rotation.set(...pose);
        schedule();
      }
      const attitude = orientation(config, data.latest ?? [], data.channels, rotation);
      if (attitude && !stage.quaternion.equals(attitude)) {
        stage.quaternion.copy(attitude);
        schedule();
      }
      const visible = config.showAxes !== false;
      if (axesHelper.visible !== visible) {
        axesHelper.visible = visible;
        schedule();
      }
      if (modelAxes.visible !== !!config.showModelAxes) {
        modelAxes.visible = !!config.showModelAxes;
        schedule();
      }
      const selected = config.channels?.length ? config.channels : config.rotation === 'quaternion' ? [0, 1, 2, 3] : [0, 1, 2];
      const names = selected.map(index => data.channels?.[index]?.name || ('CH' + (index + 1)));
      caption.textContent = modelError || (attitude ? (config.model?.name || '3D') + ' · ' + names.join(' / ') : t('等待姿态通道'));
    },
    dispose() {
      observer.disconnect();
      if (renderId) cancelAnimationFrame(renderId);
      disposeModel(model);
      disposeModel(axesHelper);
      disposeModel(modelAxes);
      orbit.dispose();
      renderer.dispose();
      renderer.forceContextLoss();
      container.replaceChildren();
    },
  };
}
