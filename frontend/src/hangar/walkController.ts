import { HangarReducedMotion } from './reducedMotion';

export const HANGAR_WALK_STEP = 0.5;
export const HANGAR_AVATAR_RADIUS = 0.3;
export type HangarWalkPosition = { x: number; z: number };
export type HangarPoint = { x: number; z: number };
export type HangarFootprint = HangarPoint & { width?: number; depth?: number; height?: number; minX?: number; maxX?: number; minZ?: number; maxZ?: number; radius?: number };

export type HangarWall = { x1: number; z1: number; x2: number; z2: number } | [HangarPoint, HangarPoint];
export type HangarWalkConfig = {
  walkablePolygon?: HangarPoint[];
  floorPolygon?: HangarPoint[];
  walkableFloor?: HangarPoint[];
  walkable?: HangarPoint[];
  cameraAzimuthDegrees?: number;
  aircraftFootprints?: HangarFootprint[];
  rackFootprints?: HangarFootprint[];
  footprints?: HangarFootprint[];
  walls?: HangarWall[];
  avatarRadius?: number;
  azimuthDegrees?: number;
  followCamera?: boolean;
  camera?: { orbit?: (input: unknown) => unknown; applyCameraTransition?: (target: unknown, duration?: number) => unknown; getState?: () => unknown };
  onFollowCamera?: (position: HangarWalkPosition) => unknown;
};
export type HangarWalkResult = { position: HangarWalkPosition; state: HangarWalkPosition; handled: boolean; accepted: boolean; refusedStep: boolean; followCameraUpdated: boolean; transition: { target: HangarWalkPosition; duration: number; immediate: boolean }; error: string | null };

const round = (value: number) => Math.round(value * 1e12) / 1e12;
const distance = (a: HangarPoint, b: HangarPoint) => Math.hypot(a.x - b.x, a.z - b.z);

export function getHangarCameraRelativeStep(key: string, azimuthDegrees = 0, distanceUnits = HANGAR_WALK_STEP): HangarWalkPosition | null {
  if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(key)) return null;
  const angle = (Number.isFinite(azimuthDegrees) ? azimuthDegrees : 0) * Math.PI / 180;
  const forward = { x: Math.sin(angle), z: -Math.cos(angle) };
  const right = { x: Math.cos(angle), z: Math.sin(angle) };
  let x = 0;
  let z = 0;
  if (key === 'ArrowUp') { x = forward.x; z = forward.z; }
  else if (key === 'ArrowDown') { x = -forward.x; z = -forward.z; }
  else if (key === 'ArrowLeft') { x = -right.x; z = -right.z; }
  else { x = right.x; z = right.z; }
  const rx = round(x * distanceUnits); const rz = round(z * distanceUnits); return { x: Object.is(rx, -0) ? 0 : rx, z: Object.is(rz, -0) ? 0 : rz };
}

export function pointInHangarPolygon(point: HangarPoint, polygon: HangarPoint[]): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const a = polygon[i]; const b = polygon[j];
    if (((a.z > point.z) !== (b.z > point.z)) && point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z || 1e-12) + a.x) inside = !inside;
  }
  return inside;
}
export function distancePointToSegment(point: HangarPoint, a: HangarPoint, b: HangarPoint): number {
  const dx = b.x - a.x; const dz = b.z - a.z;
  const lengthSquared = dx * dx + dz * dz;
  if (!lengthSquared) return distance(point, a);
  const t = Math.max(0, Math.min(1, ((point.x - a.x) * dx + (point.z - a.z) * dz) / lengthSquared));
  return distance(point, { x: a.x + t * dx, z: a.z + t * dz });
}
export function distanceSegments(a1: HangarPoint, a2: HangarPoint, b1: HangarPoint, b2: HangarPoint): number {
  return Math.min(distancePointToSegment(a1, b1, b2), distancePointToSegment(a2, b1, b2), distancePointToSegment(b1, a1, a2), distancePointToSegment(b2, a1, a2));
}

function footprintRect(footprint: HangarFootprint): { minX: number; maxX: number; minZ: number; maxZ: number } | null {
  if (typeof footprint.minX === 'number' && typeof footprint.maxX === 'number' && typeof footprint.minZ === 'number' && typeof footprint.maxZ === 'number') return { minX: footprint.minX, maxX: footprint.maxX, minZ: footprint.minZ, maxZ: footprint.maxZ };
  if (typeof footprint.width === 'number' && typeof footprint.depth === 'number') return { minX: footprint.x - footprint.width / 2, maxX: footprint.x + footprint.width / 2, minZ: footprint.z - footprint.depth / 2, maxZ: footprint.z + footprint.depth / 2 };
  if (typeof footprint.width === 'number' && typeof footprint.height === 'number') return { minX: footprint.x - footprint.width / 2, maxX: footprint.x + footprint.width / 2, minZ: footprint.z - footprint.height / 2, maxZ: footprint.z + footprint.height / 2 };
  return null;
}
export function pointInHangarFootprint(point: HangarPoint, footprint: HangarFootprint, radius = HANGAR_AVATAR_RADIUS): boolean {
  const rect = footprintRect(footprint);
  if (rect) return point.x >= rect.minX - radius && point.x <= rect.maxX + radius && point.z >= rect.minZ - radius && point.z <= rect.maxZ + radius;
  const footprintRadius = typeof (footprint as HangarFootprint & { radius?: number }).radius === 'number' ? (footprint as HangarFootprint & { radius?: number }).radius! : 0;
  return distance(point, footprint) <= footprintRadius + radius;
}

export function isHangarWalkStepAllowed(position: HangarWalkPosition, target: HangarWalkPosition, config: HangarWalkConfig = {}): boolean {
  const radius = Number.isFinite(config.avatarRadius) ? config.avatarRadius! : HANGAR_AVATAR_RADIUS;
  const polygon = config.walkablePolygon ?? config.floorPolygon ?? config.walkableFloor ?? config.walkable;
  if (!polygon || polygon.length < 3 || !pointInHangarPolygon(target, polygon)) return false;
  for (let i = 0; i < polygon.length; i++) {
    const a = polygon[i]; const b = polygon[(i + 1) % polygon.length];
    if (distancePointToSegment(target, a, b) <= radius - 1e-12) return false;
    if (distanceSegments(position, target, a, b) <= radius - 1e-12) return false;
  }
  const footprints = [...(config.aircraftFootprints ?? []), ...(config.rackFootprints ?? []), ...(config.footprints ?? [])];
  for (const footprint of footprints) {
    if (pointInHangarFootprint(target, footprint, radius)) return false;
    const rect = footprintRect(footprint);
    if (rect) {
      const corners = [
        {x:rect.minX-radius,z:rect.minZ-radius},{x:rect.maxX+radius,z:rect.minZ-radius},
        {x:rect.maxX+radius,z:rect.maxZ+radius},{x:rect.minX-radius,z:rect.maxZ+radius},
      ];
      for (let i = 0; i < corners.length; i++) {
        if (distanceSegments(position, target, corners[i], corners[(i + 1) % corners.length]) <= 1e-12) return false;
      }
    }
  }
  for (const wall of config.walls ?? []) {
    const a = Array.isArray(wall) ? wall[0] : { x: wall.x1, z: wall.z1 }; const b = Array.isArray(wall) ? wall[1] : { x: wall.x2, z: wall.z2 };
    if (distanceSegments(position, target, a, b) <= radius) return false;
  }
  return true;
}

export class HangarWalkController {
  private position: HangarWalkPosition;
  private azimuth: number;
  constructor(initialPosition: HangarWalkPosition | HangarWalkConfig = { x: 0, z: 0 }, private readonly config: HangarWalkConfig = {}, private readonly motion = new HangarReducedMotion()) {
    if ('x' in initialPosition && typeof initialPosition.x === 'number') this.position = { x: initialPosition.x, z: initialPosition.z };
    else { this.position = { x: 0, z: 0 }; this.config = initialPosition as HangarWalkConfig; }
    this.azimuth = Number.isFinite(this.config.azimuthDegrees) ? this.config.azimuthDegrees! : (Number.isFinite(this.config.cameraAzimuthDegrees) ? this.config.cameraAzimuthDegrees! : 0);
  }
  getState(): HangarWalkPosition { return { ...this.position }; }
  get positionValue(): HangarWalkPosition { return this.getState(); }
  get reducedMotion(): boolean { return this.motion.reduced; }
  get azimuthDegrees(): number { return this.azimuth; }
  setAzimuthDegrees(value: number): void { if (Number.isFinite(value)) this.azimuth = value; }
  step(key: string, repeat = false): HangarWalkResult {
    const delta = getHangarCameraRelativeStep(key, this.azimuth);
    if (!delta || repeat) return this.result(this.position, false, false, 240);
    const target = { x: round(this.position.x + delta.x), z: round(this.position.z + delta.z) };
    if (!isHangarWalkStepAllowed(this.position, target, this.config)) return this.result(this.position, false, true, 240);
    this.position = target;
    let followCameraUpdated = false;
    if (this.config.followCamera !== false) {
      if (this.config.onFollowCamera) this.config.onFollowCamera({ ...target });
      if (this.config.camera?.orbit && typeof this.config.camera.orbit === 'function') this.config.camera.orbit({ azimuthDelta: 0, polarDelta: 0 });
      if (this.config.camera?.applyCameraTransition && typeof this.config.camera.applyCameraTransition === 'function') this.config.camera.applyCameraTransition(this.config.camera.getState?.() ?? {}, 160);
      followCameraUpdated = true;
    }
    return this.result(target, true, false, this.motion.getDuration(240));
  }
  key(key: string, repeat = false): HangarWalkResult { return this.step(key, repeat); }
  handleKey(key: string, repeat = false): HangarWalkResult { return this.step(key, repeat); }
  private result(position: HangarWalkPosition, handled: boolean, refusedStep: boolean, requestedDuration: number): HangarWalkResult {
    const duration = this.motion.getDuration(requestedDuration);
    return { position: { ...position }, state: { ...position }, handled, accepted: handled, refusedStep, followCameraUpdated: handled && this.config.followCamera !== false, transition: { target: { ...position }, duration, immediate: duration === 0 }, error: null };
  }
}

export function createHangarWalkController(initialPositionOrConfig: HangarWalkPosition | HangarWalkConfig = { x: 0, z: 0 }, config: HangarWalkConfig = {}, motion?: HangarReducedMotion): HangarWalkController { return new HangarWalkController(initialPositionOrConfig, config, motion); }
export function walkHangarAvatar(position: HangarWalkPosition, key: string, azimuthDegrees = 0, config: HangarWalkConfig = {}): HangarWalkResult { const controller = new HangarWalkController(position, config); return controller.step(key); }
export function shouldCaptureHangarWalkKey(event: { key?: unknown; repeat?: unknown; target?: EventTarget|null; currentTarget?: EventTarget|null; pointerLockElement?: unknown }, region?: EventTarget|null): boolean {
  const key = typeof event.key === 'string' ? event.key : '';
  if (key === 'Tab' || !['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(key)) return false;
  if (event.pointerLockElement || ('pointerLocked' in event && (event as {pointerLocked?:unknown}).pointerLocked === true)) return false;
  if (!event.target || typeof Element === 'undefined') return false;
  const element = event.target instanceof Element ? event.target : null;
  if (!element || element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.tagName === 'SELECT' || (element instanceof HTMLElement && element.isContentEditable)) return false;
  const focusedRegion = event.currentTarget ?? region ?? null;
  const candidate = focusedRegion instanceof Element ? focusedRegion : element.closest('[data-hangar-walk-region], [aria-label*="walk" i]');
  const regionElement = candidate instanceof Element && candidate.matches('[data-hangar-walk-region], [aria-label*="walk" i]') ? candidate : null;
  return regionElement !== null && regionElement.contains(element) && !element.closest('button, a, input, textarea, select, [role="button"], [role="link"]');
}
export function handleHangarWalkKeydown(controller: HangarWalkController, event: { key?: unknown; repeat?: unknown; target?: EventTarget|null; currentTarget?: EventTarget|null; pointerLockElement?: unknown; preventDefault?: () => void }): HangarWalkResult | null {
  if (!shouldCaptureHangarWalkKey(event)) return null;
  const result = controller.step(String(event.key), event.repeat === true);
  if (result.handled) event.preventDefault?.();
  return result;
}

export const HANGAR_WALK_DISTANCE = HANGAR_WALK_STEP;
export const calculateHangarWalkStep = getHangarCameraRelativeStep;
export const resolveHangarWalkStep = getHangarCameraRelativeStep;
export const isHangarWalkStepValid = isHangarWalkStepAllowed;
