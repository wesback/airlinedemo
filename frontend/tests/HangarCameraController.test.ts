import { describe, expect, it } from 'vitest';
import { fireEvent } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import {
  HangarCameraController,
  HANGAR_TARGET_RADIUS_ERROR,
  createHangarCameraController,
  handleHangarCameraKeydown,
  shouldCaptureHangarCameraKey,
} from '../src/hangar/cameraController';

describe('HangarCameraController', () => {
  it('HangarCameraController applies bounded presets and orbit', () => {
    const camera = createHangarCameraController(10);
    expect(camera.overview().state).toMatchObject({ radius: 30, polarDegrees: 45, azimuthDegrees: 0 });
    expect(camera.aircraft().state).toMatchObject({ radius: 20, polarDegrees: 35, azimuthDegrees: 0 });

    camera.orbit(370, 90);
    expect(camera.getState()).toMatchObject({ azimuthDegrees: 10, polarDegrees: 85 });
    camera.orbit(-380, -90);
    expect(camera.getState()).toMatchObject({ azimuthDegrees: 350, polarDegrees: 15 });
    camera.orbit({ pointerLocked: true, azimuthDelta: 90, polarDelta: 90 });
    expect(camera.getState()).toMatchObject({ azimuthDegrees: 350, polarDegrees: 15 });

    for (const bad of [undefined, null, 0, -10, Number.NaN, Number.POSITIVE_INFINITY]) {
      const before = camera.getState();
      expect(camera.setTargetRadius(bad)).toMatchObject({ state: before, handled: false, error: HANGAR_TARGET_RADIUS_ERROR });
      expect(camera.getState()).toEqual(before);
      expect(camera.visibleError).toBe(HANGAR_TARGET_RADIUS_ERROR);
    }
    expect(camera.pointerOrbit({ azimuthDelta: 1000, polarDelta: 1000 }).state).toMatchObject({ radius: 20, polarDegrees: 85 });

    for (let index = 0; index < 20; index += 1) camera.key('-');
    expect(camera.getState().radius).toBe(60);
    for (let index = 0; index < 20; index += 1) camera.key('+');
    expect(camera.getState().radius).toBe(12);
  });

  it('HangarCameraController keyboard deltas', () => {
    const camera = new HangarCameraController(10);
    camera.overview();
    camera.key('ArrowLeft');
    expect(camera.getState()).toMatchObject({ azimuthDegrees: 350, polarDegrees: 45 });
    camera.key('ArrowRight'); camera.key('ArrowUp');
    expect(camera.getState()).toMatchObject({ azimuthDegrees: 0, polarDegrees: 40 });
    camera.key('ArrowDown'); camera.key('+');
    expect(camera.getState()).toMatchObject({ radius: 30 / 1.1, polarDegrees: 45 });
    camera.key('-');
    expect(camera.getState().radius).toBeCloseTo(30);

    for (const invalid of ['w', 'a', 's', 'd', 'Escape', 'Tab', 'Home']) {
      const before = camera.getState();
      expect(camera.key(invalid).handled).toBe(false);
      expect(camera.getState()).toEqual(before);
    }
    camera.pointerOrbit({ pointerLocked: true, azimuthDelta: 45, polarDelta: 45 });
    expect(camera.getState().azimuthDegrees).toBe(0);
  });

  it('HangarCameraController never traps Tab or captures outside controls', async () => {
    document.body.innerHTML = `
      <button id="outside">Outside</button>
      <input id="text" aria-label="Search">
      <aside id="evidence" aria-label="Evidence panel"><button id="evidence-control">Review</button></aside>
      <section id="camera" data-hangar-camera-region aria-label="Camera controls" tabindex="0"><button id="camera-control">Preset</button></section>`;
    const camera = createHangarCameraController(10);
    camera.overview();
    const region = document.getElementById('camera')!;
    region.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    expect(shouldCaptureHangarCameraKey({ key: 'Tab', target: region, currentTarget: region })).toBe(false);

    const user = userEvent.setup();
    region.focus();
    await user.tab();
    expect(document.activeElement?.id).toBe('camera-control');

    for (const id of ['outside', 'text', 'evidence', 'evidence-control', 'camera-control']) {
      const target = document.getElementById(id)!;
      const before = camera.getState();
      const event = new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true, cancelable: true });
      Object.defineProperty(event, 'target', { value: target });
      expect(handleHangarCameraKeydown(camera, { key: 'ArrowLeft', target, currentTarget: id === 'outside' ? null : region })).toBeNull();
      expect(camera.getState()).toEqual(before);
    }
    fireEvent.keyDown(region, { key: 'ArrowRight' });
    expect(shouldCaptureHangarCameraKey({ key: 'ArrowRight', target: region, currentTarget: region })).toBe(true);
    const result = handleHangarCameraKeydown(camera, { key: 'ArrowRight', target: region, currentTarget: region, preventDefault() {} });
    expect(result?.handled).toBe(true);
    expect(camera.getState().azimuthDegrees).toBe(10);
  });
});
