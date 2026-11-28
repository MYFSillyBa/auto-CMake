const assert = require('node:assert/strict');
const { test } = require('node:test');
const config = require('../src/config.cjs');

const bluetooth = { path: 'COM17', pnpId: 'BTHENUM\\0001' };
const usb = { path: 'COM20', pnpId: 'USB\\VID_0483&PID_5740\\ABC', vendorId: '0483', productId: '5740' };
const other = { path: 'COM21', pnpId: 'USB\\VID_10C4&PID_EA60\\DEF', vendorId: '10c4', productId: 'ea60' };

test('automatically selects the sole USB serial device among Bluetooth ports', () => {
  assert.equal(config.selectSerialPort([bluetooth, usb], {}).path, 'COM20');
});

test('remembers hardware identity after its Windows port number changes', () => {
  const moved = { ...usb, path: 'COM23' };
  assert.equal(config.selectSerialPort([other, moved], { path: 'COM20', portId: usb.pnpId }).path, 'COM23');
});

test('retains an explicitly selected device and does not guess among multiple new devices', () => {
  assert.equal(config.selectSerialPort([usb, other], { path: 'COM21' }).path, 'COM21');
  assert.equal(config.selectSerialPort([usb, other, bluetooth], {}), undefined);
});
