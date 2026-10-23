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

export type HangarReducedMotionMediaQuery = { matches: boolean; addEventListener?: (type: 'change', listener: () => void) => void; removeEventListener?: (type: 'change', listener: () => void) => void };
export type HangarReducedMotionListener = (reduced: boolean) => void;

export class HangarReducedMotion {
  private override: HangarReducedMotionOverride = null;
  private readonly listeners = new Set<HangarReducedMotionListener>();
  private readonly mediaQuery: HangarReducedMotionMediaQuery | null;
  private readonly mediaChangeListener: (() => void) | null;
  constructor(private readonly matchMedia?: (query: string) => HangarReducedMotionMediaQuery, private readonly storage?: HangarReducedMotionStorage | null) {
    this.override = this.storage === null ? null : getHangarReducedMotionOverride(storage);
    const queryMedia = this.matchMedia ?? (typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia.bind(window) : null);
    this.mediaQuery = queryMedia ? queryMedia('(prefers-reduced-motion: reduce)') : null;
    this.mediaChangeListener = this.mediaQuery?.addEventListener ? () => this.notify() : null;
    if (this.mediaQuery?.addEventListener && this.mediaChangeListener) this.mediaQuery.addEventListener('change', this.mediaChangeListener);
  }
  subscribe(listener: HangarReducedMotionListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }
  private notify(): void { const reduced = this.reduced; for (const listener of this.listeners) listener(reduced); }
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
    this.notify();
    return this.override;
  }
  clearOverride(): null { this.override = clearHangarReducedMotionOverride(this.storage); this.notify(); return null; }
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
export class HangarAvatarController<T extends { x?: number; z?: number }> {
  private position: T;
  private lastTransition: HangarTransitionTarget<T> | null = null;
  constructor(initialPosition: T, private readonly motion: HangarReducedMotion = new HangarReducedMotion()) {
    this.position = initialPosition;
  }
  getState(): T { return { ...this.position }; }
  get reducedMotion(): boolean { return this.motion.reduced; }
  get transitionDuration(): number { return this.lastTransition?.duration ?? 240; }
  move(target: T, requestedDuration = 240): HangarTransitionTarget<T> {
    const transition = this.motion.applyAvatarTransition(target, requestedDuration);
    this.position = transition.target;
    this.lastTransition = transition;
    return transition;
  }
  setTarget(target: T, requestedDuration = 240): HangarTransitionTarget<T> { return this.move(target, requestedDuration); }
  setAvatarTarget(target: T, requestedDuration = 240): HangarTransitionTarget<T> { return this.move(target, requestedDuration); }
  setOverride(override: HangarReducedMotionOverride): HangarReducedMotionOverride { return this.motion.setOverride(override); }
  clearOverride(): null { return this.motion.clearOverride(); }
}

export const createHangarAvatarController = <T extends { x?: number; z?: number }>(initialPosition: T, motion?: HangarReducedMotion) =>
  new HangarAvatarController(initialPosition, motion);
export { HangarReducedMotion as HangarReducedMotionController };
