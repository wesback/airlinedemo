import { HangarReducedMotion, type HangarTransitionTarget } from './reducedMotion';

export const HANGAR_OVERVIEW_RADIUS_MULTIPLIER = 3;
export const HANGAR_AIRCRAFT_RADIUS_MULTIPLIER = 2;
export const HANGAR_MIN_RADIUS_MULTIPLIER = 1.2;
export const HANGAR_MAX_RADIUS_MULTIPLIER = 6;
export const HANGAR_MIN_POLAR_DEGREES = 15;
export const HANGAR_MAX_POLAR_DEGREES = 85;
export const HANGAR_AZIMUTH_STEP_DEGREES = 10;
export const HANGAR_POLAR_STEP_DEGREES = 5;
export const MIN_HANGAR_RADIUS_MULTIPLIER = HANGAR_MIN_RADIUS_MULTIPLIER;
export const MAX_HANGAR_RADIUS_MULTIPLIER = HANGAR_MAX_RADIUS_MULTIPLIER;
export const MIN_HANGAR_POLAR_DEGREES = HANGAR_MIN_POLAR_DEGREES;
export const MAX_HANGAR_POLAR_DEGREES = HANGAR_MAX_POLAR_DEGREES;
export const HANGAR_TARGET_RADIUS_ERROR = 'Camera target radius must be a finite positive number.';

export type HangarCameraState = {
  targetRadius: number; radius: number; azimuthDegrees: number; polarDegrees: number;
  azimuth: number; polarAngle: number; error: string | null;
};
export type HangarCameraPreset = 'overview' | 'aircraft';
export type HangarCameraControllerResult = { state: HangarCameraState; handled: boolean; error: string | null };
export type HangarPointerInput = { azimuthDelta?: number; polarDelta?: number; dx?: number; dy?: number; pointerLocked?: boolean; };

const finitePositive = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value > 0;
export function validateHangarTargetRadius(value: unknown): string | null { return finitePositive(value) ? null : HANGAR_TARGET_RADIUS_ERROR; }
export function clamp(value: number, minimum: number, maximum: number): number { return Math.min(maximum, Math.max(minimum, value)); }
export function wrapHangarAzimuth(value: number): number { return ((value % 360) + 360) % 360; }
export function getHangarCameraPreset(preset: HangarCameraPreset, targetRadius: number): HangarCameraState {
  const valid = !validateHangarTargetRadius(targetRadius);
  const radius = valid ? targetRadius * (preset === 'aircraft' ? 2 : 3) : 0;
  return { targetRadius: valid ? targetRadius : 0, radius, azimuthDegrees: 0, polarDegrees: preset === 'aircraft' ? 35 : 45, azimuth: 0, polarAngle: preset === 'aircraft' ? 35 : 45, error: valid ? null : HANGAR_TARGET_RADIUS_ERROR };
}

export class HangarCameraController {
  private state: HangarCameraState;
  private invalidError: string | null = null;
  private lastTransition: HangarTransitionTarget<HangarCameraState> | null = null;
  constructor(targetRadius: unknown, private readonly motion: HangarReducedMotion = new HangarReducedMotion()) {
    const valid = finitePositive(targetRadius);
    this.state = valid
      ? this.makeState(targetRadius as number, (targetRadius as number) * 3, 0, 45)
      : { targetRadius: 0, radius: 0, azimuthDegrees: 0, polarDegrees: 0, azimuth: 0, polarAngle: 0, error: HANGAR_TARGET_RADIUS_ERROR };
  }
  private makeState(targetRadius: number, radius: number, azimuth: number, polar: number): HangarCameraState {
    const boundedRadius = clamp(radius, targetRadius * HANGAR_MIN_RADIUS_MULTIPLIER, targetRadius * HANGAR_MAX_RADIUS_MULTIPLIER);
    const boundedPolar = clamp(polar, HANGAR_MIN_POLAR_DEGREES, HANGAR_MAX_POLAR_DEGREES);
    const azimuthDegrees = wrapHangarAzimuth(azimuth);
    return { targetRadius, radius: boundedRadius, azimuthDegrees, polarDegrees: boundedPolar, azimuth: azimuthDegrees, polarAngle: boundedPolar, error: null };
  }
  private invalid(): HangarCameraControllerResult { this.invalidError = HANGAR_TARGET_RADIUS_ERROR; return { state: this.getState(), handled: false, error: HANGAR_TARGET_RADIUS_ERROR }; }
  getState(): HangarCameraState { return { ...this.state }; }
  get stateValue(): HangarCameraState { return this.getState(); }
  get visibleError(): string | null { return this.invalidError ?? this.state.error; }
  get transitionDuration(): number { return this.lastTransition?.duration ?? 480; }
  applyCameraTransition(target: HangarCameraState, requestedDuration = 480): HangarTransitionTarget<HangarCameraState> {
    const transition = this.motion?.applyCameraTransition(target, requestedDuration) ?? {
      target, duration: requestedDuration, immediate: requestedDuration === 0,
    };
    this.lastTransition = transition;
    return transition;
  }
  get reducedMotion(): boolean { return this.motion?.reduced ?? false; }
  setTargetRadius(targetRadius: unknown): HangarCameraControllerResult {
    if (!finitePositive(targetRadius)) return this.invalid();
    const next = targetRadius as number;
    const ratio = this.state.radius / this.state.targetRadius;
    this.state = this.makeState(next, next * (finitePositive(ratio) ? ratio : 3), this.state.azimuthDegrees, this.state.polarDegrees);
    this.applyCameraTransition(this.state);
    return { state: this.getState(), handled: true, error: null };
  }
  updateTargetRadius(targetRadius: unknown): HangarCameraControllerResult { return this.setTargetRadius(targetRadius); }
  applyPreset(preset: HangarCameraPreset): HangarCameraControllerResult {
    if (this.state.error || !finitePositive(this.state.targetRadius)) return this.invalid();
    if (preset !== 'overview' && preset !== 'aircraft') return this.invalid();
    const target = this.state.targetRadius;
    this.state = this.makeState(target, target * (preset === 'aircraft' ? 2 : 3), 0, preset === 'aircraft' ? 35 : 45);
    this.applyCameraTransition(this.state, 480);
    return { state: this.getState(), handled: true, error: null };
  }
  overview(): HangarCameraControllerResult { return this.applyPreset('overview'); }
  aircraft(): HangarCameraControllerResult { return this.applyPreset('aircraft'); }
  applyOverviewPreset(): HangarCameraControllerResult { return this.overview(); }
  applyAircraftPreset(): HangarCameraControllerResult { return this.aircraft(); }
  orbit(input: HangarPointerInput | number, polarDelta = 0): HangarCameraControllerResult {
    if (this.state.error || !finitePositive(this.state.targetRadius)) return this.invalid();
    const pointer = typeof input === 'number' ? { azimuthDelta: input, polarDelta } : input;
    if (pointer.pointerLocked === true) return { state: this.getState(), handled: false, error: null };
    if (typeof pointer.azimuthDelta !== 'number' && typeof pointer.dx !== 'number') return this.invalid();
    const azimuthDelta = typeof pointer.azimuthDelta === 'number' ? pointer.azimuthDelta : pointer.dx ?? 0;
    const angleDelta = typeof pointer.polarDelta === 'number' ? pointer.polarDelta : pointer.dy ?? 0;
    if (!Number.isFinite(azimuthDelta) || !Number.isFinite(angleDelta)) return this.invalid();
    this.state = this.makeState(this.state.targetRadius, this.state.radius, this.state.azimuthDegrees + azimuthDelta, this.state.polarDegrees + angleDelta);
    this.applyCameraTransition(this.state, 160);
    return { state: this.getState(), handled: true, error: null };
  }
  pointerOrbit(input: HangarPointerInput | number, polarDelta = 0): HangarCameraControllerResult { return this.orbit(input, polarDelta); }
  key(key: string): HangarCameraControllerResult {
    if (this.state.error || !finitePositive(this.state.targetRadius)) return this.invalid();
    let azimuth = this.state.azimuthDegrees; let polar = this.state.polarDegrees; let radius = this.state.radius;
    if (key === 'ArrowLeft') azimuth -= 10;
    else if (key === 'ArrowRight') azimuth += 10;
    else if (key === 'ArrowUp') polar -= 5;
    else if (key === 'ArrowDown') polar += 5;
    else if (key === '+' || key === '=') radius /= 1.1;
    else if (key === '-' || key === '_') radius *= 1.1;
    else return { state: this.getState(), handled: false, error: null };
    this.state = this.makeState(this.state.targetRadius, radius, azimuth, polar);
    this.applyCameraTransition(this.state, 160);
    return { state: this.getState(), handled: true, error: null };
  }
  handleKey(key: string): HangarCameraControllerResult { return this.key(key); }
  handleKeyboardKey(key: string): HangarCameraControllerResult { return this.key(key); }
}

export function createHangarCameraController(targetRadius: unknown, motion?: HangarReducedMotion): HangarCameraController { return new HangarCameraController(targetRadius, motion); }
export const createCameraController = createHangarCameraController;
export const applyHangarCameraPreset = (preset: HangarCameraPreset, targetRadius: number) => getHangarCameraPreset(preset, targetRadius);
export const clampHangarRadius = (radius: number, targetRadius: number) => clamp(radius, targetRadius * HANGAR_MIN_RADIUS_MULTIPLIER, targetRadius * HANGAR_MAX_RADIUS_MULTIPLIER);
export const clampHangarPolarAngle = (polarDegrees: number) => clamp(polarDegrees, HANGAR_MIN_POLAR_DEGREES, HANGAR_MAX_POLAR_DEGREES);
export const wrapHangarAzimuthDegrees = wrapHangarAzimuth;

export function isHangarCameraKeyTarget(target: EventTarget | null, focusedRegion: EventTarget | null = null): boolean {
  if (!target || typeof Element === 'undefined') return false;
  const element = target instanceof Element ? target : null;
  if (!element) return false;
  if (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.tagName === 'SELECT' || (element instanceof HTMLElement && element.isContentEditable)) return false;
  if (element.closest('[role="panel"], [data-evidence], aside, [aria-label*="evidence" i], [aria-label*="evidence panel" i]')) return false;
  const region = focusedRegion instanceof Element ? focusedRegion : element.closest('[data-hangar-camera-region], [aria-label*="camera" i]');
  if (!(region instanceof Element)) return false;
  if (!region.contains(element)) return false;
  if (!element.closest('[data-hangar-camera-region], [aria-label*="camera" i]')) return false;
  const semanticControl = element.closest('button, a, input, textarea, select, [role="button"], [role="link"], [role="menuitem"]');
  return !semanticControl;
}

export function shouldCaptureHangarCameraKey(event: { key?: unknown; target?: EventTarget|null; currentTarget?: EventTarget|null; pointerLockElement?: unknown }, controllerTarget?: EventTarget|null): boolean {
  const key = typeof event.key === 'string' ? event.key : '';
  if (key === 'Tab') return false;
  if (!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','_'].includes(key)) return false;
  if ('pointerLockElement' in event && event.pointerLockElement) return false;
  const region = event.currentTarget ?? controllerTarget ?? null;
  return isHangarCameraKeyTarget(event.target ?? null, region);
}

export function handleHangarCameraKeydown(controller: HangarCameraController, event: { key?: unknown; target?: EventTarget|null; currentTarget?: EventTarget|null; preventDefault?: () => void; pointerLockElement?: unknown }): HangarCameraControllerResult | null {
  if (!shouldCaptureHangarCameraKey(event)) return null;
  const result = controller.key(String(event.key));
  if (result.handled) event.preventDefault?.();
  return result;
}
export { HangarCameraController as cameraController };
