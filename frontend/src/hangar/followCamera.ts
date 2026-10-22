import { HangarReducedMotion, type HangarTransitionTarget } from './reducedMotion';

export const HANGAR_FOLLOW_MIN_RADIUS = 4;
export const HANGAR_FOLLOW_MAX_RADIUS = 12;
export const HANGAR_FOLLOW_MIN_POLAR_DEGREES = 15;
export const HANGAR_FOLLOW_MAX_POLAR_DEGREES = 85;
export const HANGAR_FOLLOW_DEFAULT_RADIUS = 6;
export const HANGAR_FOLLOW_DEFAULT_POLAR_DEGREES = 55;
export const HANGAR_FOLLOW_MIN_BOX_RADIUS = 2;
export const HANGAR_FOLLOW_EASE_MS = 400;

export type HangarVector3 = { x: number; y: number; z: number };
export type HangarCameraBox = { min?: HangarVector3; max?: HangarVector3; minX?: number; maxX?: number; minY?: number; maxY?: number; minZ?: number; maxZ?: number };
export type HangarFollowPreset = 'overview' | 'aircraft';
type NormalizedCameraBox = { min: HangarVector3; max: HangarVector3 };
export type HangarFollowCameraState = {
  follow: boolean; target: HangarVector3; avatar: HangarVector3; position: HangarVector3;
  radius: number; azimuthDegrees: number; polarDegrees: number;
};
export type HangarFollowClock = () => number;
export type HangarFollowCameraOptions = { box?: HangarCameraBox | null; clock?: HangarFollowClock; reducedMotion?: HangarReducedMotion; follow?: boolean };

const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
export const clamp = (v: number, min: number, max: number) => Math.max(min, Math.min(max, v));
export const wrapHangarFollowAzimuth = (v: number) => ((v % 360) + 360) % 360;
export const hangarAvatarTarget = (avatar: HangarVector3): HangarVector3 => ({ x: avatar.x, y: avatar.y + 1, z: avatar.z });

export class HangarFollowCamera {
  private state: HangarFollowCameraState;
  private box: NormalizedCameraBox | null;
  private clock: HangarFollowClock;
  private motion: HangarReducedMotion;
  private transition: { from: HangarVector3; to: HangarVector3; start: number } | null = null;

  constructor(avatar: HangarVector3 = { x: 0, y: 0, z: 0 }, options: HangarFollowCameraOptions = {}) {
    const supplied = options.box;
    this.box = supplied ? supplied.min && supplied.max
      ? { min: supplied.min, max: supplied.max }
      : supplied.minX !== undefined && supplied.maxX !== undefined
        ? { min: { x: supplied.minX, y: supplied.minY ?? -Infinity, z: supplied.minZ ?? -Infinity }, max: { x: supplied.maxX, y: supplied.maxY ?? Infinity, z: supplied.maxZ ?? Infinity } }
        : null
      : null;
    this.clock = options.clock ?? (() => Date.now());
    this.motion = options.reducedMotion ?? new HangarReducedMotion();
    const target = hangarAvatarTarget(avatar);
    this.state = { follow: options.follow !== false, target: { ...target }, avatar: { ...avatar }, position: { x: 0, y: 0, z: 0 }, radius: 6, azimuthDegrees: 0, polarDegrees: 55 };
    this.state.position = this.computePosition(this.state);
    this.state.position = this.applyBoxClamp(this.state.position, this.state.target);
  }
  getState(): HangarFollowCameraState { return { ...this.state, target: { ...this.state.target }, avatar: { ...this.state.avatar }, position: { ...this.state.position } }; }
  get followEnabled(): boolean { return this.state.follow; }
  get following(): boolean { return this.state.follow; }
  setFollow(enabled: boolean): HangarFollowCameraState { return enabled ? this.turnOn() : this.turnOff(); }
  toggleFollow(): HangarFollowCameraState { return this.state.follow ? this.turnOff() : this.turnOn(); }
  setFollowEnabled(enabled: boolean): HangarFollowCameraState { return this.setFollow(enabled); }
  turnOff(): HangarFollowCameraState { this.state.follow = false; this.transition = null; return this.getState(); }
  turnOn(): HangarFollowCameraState {
    this.state.follow = true;
    this.state.radius = HANGAR_FOLLOW_DEFAULT_RADIUS;
    this.state.polarDegrees = HANGAR_FOLLOW_DEFAULT_POLAR_DEGREES;
    this.state.target = hangarAvatarTarget(this.state.avatar);
    this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target);
    return this.getState();
  }
  private computePosition(state: HangarFollowCameraState): HangarVector3 {
    const azimuth = state.azimuthDegrees * Math.PI / 180;
    const polar = state.polarDegrees * Math.PI / 180;
    const horizontal = state.radius * Math.sin(polar);
    return { x: state.target.x + horizontal * Math.sin(azimuth), y: state.target.y + state.radius * Math.cos(polar), z: state.target.z + horizontal * Math.cos(azimuth) };
  }
  private insideBox(position: HangarVector3): boolean {
    if (!this.box) return true;
    return position.x >= this.box.min.x && position.x <= this.box.max.x && position.y >= this.box.min.y && position.y <= this.box.max.y && position.z >= this.box.min.z && position.z <= this.box.max.z;
  }
  private applyBoxClamp(position: HangarVector3, target: HangarVector3): HangarVector3 {
    if (this.insideBox(position)) return { ...position };
    const view = { x: position.x - target.x, y: position.y - target.y, z: position.z - target.z };
    const length = Math.hypot(view.x, view.y, view.z) || 1;
    const currentRadius = this.state.radius;
    for (let radius = currentRadius; radius >= HANGAR_FOLLOW_MIN_BOX_RADIUS; radius -= 0.05) {
      const candidate = { x: target.x + view.x / length * radius, y: target.y + view.y / length * radius, z: target.z + view.z / length * radius };
      if (this.insideBox(candidate)) {
        this.state.radius = clamp(radius, HANGAR_FOLLOW_MIN_BOX_RADIUS, HANGAR_FOLLOW_MAX_RADIUS);
        return candidate;
      }
    }
    return { x: target.x + view.x / length * HANGAR_FOLLOW_MIN_BOX_RADIUS, y: target.y + view.y / length * HANGAR_FOLLOW_MIN_BOX_RADIUS, z: target.z + view.z / length * HANGAR_FOLLOW_MIN_BOX_RADIUS };
  }
  overview(): HangarFollowCameraState { this.turnOff(); this.state.radius = 30; this.state.polarDegrees = 45; this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target); return this.getState(); }
  aircraft(): HangarFollowCameraState { this.turnOff(); this.state.radius = 20; this.state.polarDegrees = 35; this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target); return this.getState(); }
  applyPreset(_preset: 'overview' | 'aircraft'): HangarFollowCameraState { return _preset === 'overview' ? this.overview() : this.aircraft(); }
  key(key: string): HangarFollowCameraState {
    let azimuth = this.state.azimuthDegrees;
    let polar = this.state.polarDegrees;
    let radius = this.state.radius;
    if (key === 'ArrowLeft') azimuth -= 10;
    else if (key === 'ArrowRight') azimuth += 10;
    else if (key === 'ArrowUp') polar -= 5;
    else if (key === 'ArrowDown') polar += 5;
    else if (key === '+' || key === '=') radius /= 1.1;
    else if (key === '-' || key === '_') radius *= 1.1;
    else return this.getState();
    this.state.azimuthDegrees = wrapHangarFollowAzimuth(azimuth);
    this.state.polarDegrees = clamp(polar, HANGAR_FOLLOW_MIN_POLAR_DEGREES, HANGAR_FOLLOW_MAX_POLAR_DEGREES);
    this.state.radius = clamp(radius, HANGAR_FOLLOW_MIN_RADIUS, HANGAR_FOLLOW_MAX_RADIUS);
    this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target);
    return this.getState();
  }
  orbit(input: { azimuthDelta?: number; polarDelta?: number } | number = 0, polarDelta = 0): HangarFollowCameraState {
    const azimuth = typeof input === 'number' ? input : finite(input.azimuthDelta) ? input.azimuthDelta! : 0;
    const polar = typeof input === 'number' ? polarDelta : finite(input.polarDelta) ? input.polarDelta! : 0;
    if (!Number.isFinite(azimuth) || !Number.isFinite(polar)) return this.getState();
    this.state.azimuthDegrees = wrapHangarFollowAzimuth(this.state.azimuthDegrees + azimuth);
    this.state.polarDegrees = clamp(this.state.polarDegrees + polar, HANGAR_FOLLOW_MIN_POLAR_DEGREES, HANGAR_FOLLOW_MAX_POLAR_DEGREES);
    this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target);
    return this.getState();
  }
  pointerOrbit(input: { azimuthDelta?: number; polarDelta?: number } | number = 0, polarDelta = 0): HangarFollowCameraState { return this.orbit(input, polarDelta); }
  applyAvatarStep(avatar: HangarVector3 | null, accepted = true): HangarTransitionTarget<HangarFollowCameraState> {
    if (!accepted || !this.state.follow || !finite(avatar?.x) || !finite(avatar?.y) || !finite(avatar?.z)) return { target: this.getState(), duration: 0, immediate: true };
    const nextAvatar = { x: avatar!.x, y: avatar!.y, z: avatar!.z };
    this.state.avatar = { ...nextAvatar };
    const to = hangarAvatarTarget(nextAvatar);
    if (this.motion.reduced) { this.state.target = { ...to }; this.transition = null; this.state.position = this.applyBoxClamp(this.computePosition(this.state), to); return { target: this.getState(), duration: 0, immediate: true }; }
    this.transition = { from: { ...this.state.target }, to: { ...to }, start: this.clock() };
    const duration = this.motion.getDuration(HANGAR_FOLLOW_EASE_MS);
    this.update();
    return { target: this.getState(), duration, immediate: duration === 0 };
  }
  avatarStep(avatar: HangarVector3 | null, accepted = true): HangarTransitionTarget<HangarFollowCameraState> { return this.applyAvatarStep(avatar, accepted); }
  stepAvatar(avatar: HangarVector3 | null, accepted = true): HangarTransitionTarget<HangarFollowCameraState> { return this.applyAvatarStep(avatar, accepted); }
  update(now = this.clock()): HangarFollowCameraState {
    if (!this.transition) return this.getState();
    const elapsed = now - this.transition.start;
    const amount = clamp(elapsed / HANGAR_FOLLOW_EASE_MS, 0, 1);
    const eased = amount * amount * (3 - 2 * amount);
    this.state.target = { x: this.transition.from.x + (this.transition.to.x - this.transition.from.x) * eased, y: this.transition.from.y + (this.transition.to.y - this.transition.from.y) * eased, z: this.transition.from.z + (this.transition.to.z - this.transition.from.z) * eased };
    if (amount >= 1) { this.state.target = { ...this.transition.to }; this.transition = null; }
    this.state.position = this.applyBoxClamp(this.computePosition(this.state), this.state.target);
    return this.getState();
  }
  tick(now?: number): HangarFollowCameraState { return this.update(now); }
}

export const FollowCameraController = HangarFollowCamera;
export const FollowAvatarCamera = HangarFollowCamera;
export function createHangarFollowCamera(avatar?: HangarVector3, options?: HangarFollowCameraOptions): HangarFollowCamera { return new HangarFollowCamera(avatar, options); }
export const createFollowCameraController = createHangarFollowCamera;
export const hangarAvatarCameraTarget = hangarAvatarTarget;
export const clampHangarFollowRadius = (radius: number) => clamp(radius, HANGAR_FOLLOW_MIN_RADIUS, HANGAR_FOLLOW_MAX_RADIUS);
export const clampHangarFollowPolar = (polar: number) => clamp(polar, HANGAR_FOLLOW_MIN_POLAR_DEGREES, HANGAR_FOLLOW_MAX_POLAR_DEGREES);
