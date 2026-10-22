import { afterEach, describe, expect, it } from 'vitest';
import {
  HANGAR_REDUCED_MOTION_STORAGE_KEY,
  HangarReducedMotion,
  applyHangarTransition,
  getHangarReducedMotionOverride,
  getHangarSystemReducedMotion,
  shouldReduceHangarMotion,
} from '../src/hangar/reducedMotion';

const memoryStorage = () => {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
};

afterEach(() => sessionStorage.clear());

describe('HangarReducedMotion', () => {
  it('HangarReducedMotion applies system preference', () => {
    const storage = memoryStorage();
    const motion = new HangarReducedMotion(() => ({ matches: true }), storage);

    expect(getHangarSystemReducedMotion(() => ({ matches: true }))).toBe(true);
    expect(shouldReduceHangarMotion(() => ({ matches: true }), storage)).toBe(true);
    expect(motion.getDuration(480)).toBe(0);

    const camera = motion.applyTransition({ radius: 20, azimuthDegrees: 10 }, 480);
    const avatar = motion.applyTransition({ x: 4, z: -2 }, 240);
    expect(camera).toEqual({ target: { radius: 20, azimuthDegrees: 10 }, duration: 0, immediate: true });
    expect(avatar).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    expect(applyHangarTransition('final', 999, true).duration).toBe(0);
  });

  it('HangarReducedMotion session override', () => {
    const storage = memoryStorage();
    let systemReduce = false;
    const motion = new HangarReducedMotion(() => ({ matches: systemReduce }), storage);

    expect(motion.reduced).toBe(false);
    expect(motion.setOverride(true)).toBe(true);
    expect(getHangarReducedMotionOverride(storage)).toBe(true);
    expect(motion.reduced).toBe(true);
    expect(motion.applyTransition('camera-final', 600).duration).toBe(0);
    expect(motion.applyTransition('avatar-final', 300).duration).toBe(0);

    systemReduce = true;
    expect(motion.setOverride(false)).toBe(false);
    expect(motion.reduced).toBe(false);
    expect(motion.getDuration(600)).toBe(600);
    expect(motion.getDuration(300)).toBe(300);

    motion.clearOverride();
    expect(motion.overrideValue).toBeNull();
    expect(motion.reduced).toBe(true);
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBeNull();
  });
});
