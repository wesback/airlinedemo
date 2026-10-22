import { createElement, type ReactNode } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  HANGAR_REDUCED_MOTION_STORAGE_KEY,
  HangarReducedMotion,
  createHangarAvatarController,
  applyHangarTransition,
  getHangarReducedMotionOverride,
  getHangarSystemReducedMotion,
  shouldReduceHangarMotion,
} from '../src/hangar/reducedMotion';
import { createHangarCameraController } from '../src/hangar/cameraController';
import { createHangarSceneMotionControllers, loadHangarScene } from '../src/hangar/entryController';

vi.mock('@react-three/fiber', async () => {
  const React = await import('react');
  return {
    Canvas: ({ children, camera }: { children?: unknown; camera?: { position?: number[] } }) => React.createElement(
      'div',
      { 'data-hangar-rendered-camera-position': camera?.position?.join(',') ?? '' },
      children as ReactNode,
    ),
  };
});

const memoryStorage = () => {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
};

afterEach(() => { cleanup(); sessionStorage.clear(); });

describe('HangarReducedMotion', () => {
  it('HangarReducedMotion applies system preference', () => {
    const storage = memoryStorage();
    const motion = new HangarReducedMotion(() => ({ matches: true }), storage);

    expect(getHangarSystemReducedMotion(() => ({ matches: true }))).toBe(true);
    expect(shouldReduceHangarMotion(() => ({ matches: true }), storage)).toBe(true);
    expect(motion.getDuration(480)).toBe(0);

    const cameraTransitionResult = motion.applyTransition({ radius: 20, azimuthDegrees: 10 }, 480);
    const avatarTransitionResult = motion.applyTransition({ x: 4, z: -2 }, 240);
    expect(cameraTransitionResult).toEqual({ target: { radius: 20, azimuthDegrees: 10 }, duration: 0, immediate: true });
    expect(avatarTransitionResult).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    expect(applyHangarTransition('final', 999, true).duration).toBe(0);

    const camera = createHangarCameraController(10, motion);
    const cameraTransition = camera.applyCameraTransition(camera.overview().state, 480);
    expect(cameraTransition).toEqual({ target: camera.getState(), duration: 0, immediate: true });
    expect(camera.overview().state.radius).toBe(30);
    expect(camera.transitionDuration).toBe(0);
    expect(motion.applyAvatarTransition({ x: 4, z: -2 }, 240)).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    const avatar = createHangarAvatarController({ x: 0, z: 0 }, motion);
    expect(avatar.move({ x: 4, z: -2 }, 240)).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    expect(avatar.getState()).toEqual({ x: 4, z: -2 });
    expect(avatar.transitionDuration).toBe(0);
  });

  it('HangarReducedMotion renders camera and avatar targets immediately for system preference', async () => {
    const storage = memoryStorage();
    const motion = new HangarReducedMotion(() => ({ matches: true }), storage);
    const controllers = createHangarSceneMotionControllers(motion);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    expect(document.querySelector('[data-hangar-rendered-camera-position]')?.getAttribute('data-hangar-rendered-camera-position')).toBe('12,3,18');
    expect(document.querySelector('[data-hangar-avatar-mesh-position]')?.getAttribute('data-hangar-avatar-mesh-position')).toBe('0,0');
    expect(controllers.camera.transitionDuration).toBe(0);
    expect(controllers.avatar.transitionDuration).toBe(0);

    const normalMotion = new HangarReducedMotion(() => ({ matches: false }), memoryStorage());
    const normalControllers = createHangarSceneMotionControllers(normalMotion);
    const NormalScene = await loadHangarScene(normalControllers);
    render(createElement(NormalScene));
    expect(normalControllers.camera.transitionDuration).toBe(480);
    expect(normalControllers.avatar.transitionDuration).toBe(240);
  });

  it('HangarReducedMotion session override controls rendered transitions in both directions', async () => {
    const storage = memoryStorage();
    let systemReduce = false;
    const motion = new HangarReducedMotion(() => ({ matches: systemReduce }), storage);
    const controllers = createHangarSceneMotionControllers(motion);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    fireEvent.click(screen.getByRole('button', { name: 'Reduce motion' }));
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBe('reduce');
    expect(controllers.camera.transitionDuration).toBe(0);
    expect(controllers.avatar.transitionDuration).toBe(0);

    storage.removeItem(HANGAR_REDUCED_MOTION_STORAGE_KEY);
    systemReduce = true;
    motion.setOverride(false);
    fireEvent.click(screen.getByRole('button', { name: 'Allow motion' }));
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBe('no-preference');
    expect(controllers.camera.transitionDuration).toBe(480);
    expect(controllers.avatar.transitionDuration).toBe(240);
  });

  it('HangarReducedMotion connects avatar and camera targets to the rendered scene', async () => {
    const storage = memoryStorage();
    const motion = new HangarReducedMotion(() => ({ matches: true }), storage);
    const controllers = createHangarSceneMotionControllers();
    controllers.motion.setOverride(true);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    expect(screen.getByText('Move avatar').closest('[data-hangar-avatar-position]')?.getAttribute('data-hangar-avatar-position')).toBe('0,0');
    expect(screen.getByText('Move avatar').closest('[data-hangar-avatar-duration]')?.getAttribute('data-hangar-avatar-duration')).toBe('0');

    fireEvent.click(screen.getByRole('button', { name: 'Move avatar' }));
    expect(screen.getByText('Move avatar').closest('[data-hangar-avatar-position]')?.getAttribute('data-hangar-avatar-position')).toBe('4,-2');
    expect(document.querySelector('[data-hangar-avatar-mesh-position]')?.getAttribute('data-hangar-avatar-mesh-position')).toBe('4,-2');
    expect(controllers.avatar.getState()).toEqual({ x: 4, z: -2 });
    expect(controllers.avatar.transitionDuration).toBe(0);

    expect(document.querySelector('[data-hangar-rendered-camera-position]')?.getAttribute('data-hangar-rendered-camera-position')).toBe('12,3,18');
    fireEvent.click(screen.getByRole('button', { name: 'Aircraft' }));
    expect(controllers.camera.getState().radius).toBe(20);
    expect(controllers.camera.transitionDuration).toBe(0);
    expect(screen.getByText('Move avatar').closest('[data-hangar-camera-position]')?.getAttribute('data-hangar-camera-position')).toBe('20,0,35');
    expect(document.querySelector('[data-hangar-rendered-camera-position]')?.getAttribute('data-hangar-rendered-camera-position')).toBe('8,3,12');
  });

  it('HangarReducedMotion session override', () => {
    const storage = memoryStorage();
    let systemReduce = false;
    const motion = new HangarReducedMotion(() => ({ matches: systemReduce }), storage);

    expect(motion.reduced).toBe(false);
    const updates: boolean[] = [];
    const unsubscribe = motion.subscribe((active) => updates.push(active));
    expect(motion.setOverride(true)).toBe(true);
    expect(updates).toEqual([true]);
    expect(getHangarReducedMotionOverride(storage)).toBe(true);
    expect(motion.reduced).toBe(true);
    expect(motion.applyTransition('camera-final', 600).duration).toBe(0);
    expect(motion.applyTransition('avatar-final', 300).duration).toBe(0);

    systemReduce = true;
    expect(motion.setOverride(false)).toBe(false);
    expect(updates).toEqual([true, false]);
    expect(motion.reduced).toBe(false);
    expect(motion.getDuration(600)).toBe(600);
    expect(motion.getDuration(300)).toBe(300);
    const normalCamera = createHangarCameraController(10, motion);
    expect(normalCamera.applyCameraTransition(normalCamera.overview().state, 600).duration).toBe(600);
    expect(normalCamera.applyCameraTransition(normalCamera.aircraft().state, 300).duration).toBe(300);

    motion.clearOverride();
    expect(motion.overrideValue).toBeNull();
    expect(motion.reduced).toBe(true);
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBeNull();

    const controllerWithReduction = createHangarCameraController(10, motion);
    expect(controllerWithReduction.applyCameraTransition(controllerWithReduction.overview().state, 480)).toEqual({ target: controllerWithReduction.getState(), duration: 0, immediate: true });

    systemReduce = false;
    motion.setOverride(true);
    const controllerWithOverride = createHangarCameraController(10, motion);
    expect(controllerWithOverride.applyCameraTransition(controllerWithOverride.overview().state, 600).duration).toBe(0);
    expect(controllerWithOverride.applyCameraTransition(controllerWithOverride.aircraft().state, 300)).toEqual({ target: controllerWithOverride.getState(), duration: 0, immediate: true });
    unsubscribe();
  });
});
