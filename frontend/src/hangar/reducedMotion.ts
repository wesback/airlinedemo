export const HANGAR_REDUCED_MOTION_STORAGE_KEY = 'hangar.reducedMotionOverride';
export type HangarReducedMotionOverride = boolean | 'reduce' | 'no-preference' | null;
export type HangarReducedMotionStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
export type HangarTransitionTarget<T> = { target: T; duration: number; immediate: boolean };

const getBrowserStorage = (): HangarReducedMotionStorage | null => {
  try { return typeof sessionStorage !== 'undefined' ? sessionStorage : null; } catch { return null; }
};

export function getHangarSystemReducedMotion(matchMedia?: (query: string) => { matches: boolean }): boolean {
  if (matchMedia) return matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }
  return false;
}

export function normalizeHangarReducedMotionOverride(value: unknown): HangarReducedMotionOverride {
  if (value === true || value === 'true' || value === 'reduce') return true;
  if (value === false || value === 'false' || value === 'no-preference') return false;
  return null;
}

export function getHangarReducedMotionOverride(storage?: HangarReducedMotionStorage | null): HangarReducedMotionOverride {
  const value = storage === undefined ? getBrowserStorage() : storage;
  if (!value) return null;
  try { return normalizeHangarReducedMotionOverride(value.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)); }
  catch { return null; }
}

export function setHangarReducedMotionOverride(override: HangarReducedMotionOverride, storage?: HangarReducedMotionStorage | null): HangarReducedMotionOverride {
  const value = storage === undefined ? getBrowserStorage() : storage;
  const normalized = normalizeHangarReducedMotionOverride(override);
  try {
    if (!value || normalized === null) value?.removeItem(HANGAR_REDUCED_MOTION_STORAGE_KEY);
    else value.setItem(HANGAR_REDUCED_MOTION_STORAGE_KEY, normalized ? 'reduce' : 'no-preference');
  } catch { /* A restricted browser still has an effective in-memory result through the controller. */ }
  return normalized;
}

export function clearHangarReducedMotionOverride(storage?: HangarReducedMotionStorage | null): null {
  const value = storage === undefined ? getBrowserStorage() : storage;
  try { value?.removeItem(HANGAR_REDUCED_MOTION_STORAGE_KEY); } catch { /* Storage is optional. */ }
  return null;
}

export function shouldReduceHangarMotion(
  matchMedia?: (query: string) => { matches: boolean },
  storage?: HangarReducedMotionStorage | null,
): boolean {
  const override = getHangarReducedMotionOverride(storage);
  if (override === true || override === 'reduce') return true;
  if (override === false || override === 'no-preference') return false;
  return getHangarSystemReducedMotion(matchMedia);
}

export function getHangarMotionDuration(requestedDuration: unknown, reducedMotion: boolean): number {
  const duration = typeof requestedDuration === 'number' && Number.isFinite(requestedDuration)
    ? Math.max(0, requestedDuration) : 0;
  return reducedMotion ? 0 : duration;
}

export function applyHangarTransition<T>(target: T, requestedDuration: unknown, reducedMotion: boolean): HangarTransitionTarget<T> {
  const duration = getHangarMotionDuration(requestedDuration, reducedMotion);
  return { target, duration, immediate: duration === 0 };
}

export class HangarReducedMotion {
  private override: HangarReducedMotionOverride = null;
  constructor(private readonly matchMedia?: (query: string) => { matches: boolean }, private readonly storage?: HangarReducedMotionStorage | null) {
    this.override = this.storage === null ? null : getHangarReducedMotionOverride(storage);
  }
  get systemPreference(): boolean { return getHangarSystemReducedMotion(this.matchMedia); }
  get overrideValue(): HangarReducedMotionOverride { return this.storage === null ? this.override : getHangarReducedMotionOverride(this.storage); }
  get reduced(): boolean {
    const override = this.overrideValue;
    if (override === true || override === 'reduce') return true;
    if (override === false || override === 'no-preference') return false;
    return this.systemPreference;
  }
  setOverride(override: HangarReducedMotionOverride): HangarReducedMotionOverride {
    this.override = setHangarReducedMotionOverride(override, this.storage);
    return this.override;
  }
  clearOverride(): null { this.override = clearHangarReducedMotionOverride(this.storage); return null; }
  enable(): boolean { return this.setOverride(true) === true; }
  disable(): boolean { return this.setOverride(false) === false; }
  getDuration(requestedDuration: unknown): number { return getHangarMotionDuration(requestedDuration, this.reduced); }
  applyTransition<T>(target: T, requestedDuration: unknown): HangarTransitionTarget<T> { return applyHangarTransition(target, requestedDuration, this.reduced); }
  applyCameraTransition<T>(target: T, requestedDuration: unknown): HangarTransitionTarget<T> { return this.applyTransition(target, requestedDuration); }
  applyAvatarTransition<T>(target: T, requestedDuration: unknown): HangarTransitionTarget<T> { return this.applyTransition(target, requestedDuration); }
}

export const isHangarReducedMotionActive = shouldReduceHangarMotion;
export const getHangarReducedMotion = shouldReduceHangarMotion;
export const resolveHangarMotionDuration = getHangarMotionDuration;
export const scheduleHangarTransition = applyHangarTransition;
export const createHangarReducedMotion = (matchMedia?: (query: string) => { matches: boolean }, storage?: HangarReducedMotionStorage | null) => new HangarReducedMotion(matchMedia, storage);
export { HangarReducedMotion as HangarReducedMotionController };
