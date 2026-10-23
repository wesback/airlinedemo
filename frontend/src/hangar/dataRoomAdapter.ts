import { HANGAR_ASSET_INVALID, type HangarAssetRecord } from './assetPolicy';
import { loadValidatedHangarAsset } from './hangarAssetLoader';
import sceneManifest from './sceneManifest.json';
import { HANGAR_SCENE_THEME, hangarLabelContrastRatio } from './sceneTheme';

export { HANGAR_ASSET_INVALID };
export const HANGAR_RACK_SOURCES = ['technical', 'maintenance', 'finance', 'records', 'contracts'] as const;
export const HANGAR_MANIFEST_ERROR = 'scene_manifest_invalid';
export const HANGAR_AVATAR_HARD_HAT = 'HardHat';
export const HANGAR_HARD_HAT_COLOR = '#FFD400';

export type RackManifestResult = { valid: true; units: ReadonlyArray<{ node: string; source: string }> } | { valid: false; reason: typeof HANGAR_MANIFEST_ERROR };

export function validateHangarRackAvatarManifest(input: unknown = sceneManifest): RackManifestResult | { valid: false; reason: typeof HANGAR_MANIFEST_ERROR } {
  if (!input || typeof input !== 'object' || Array.isArray(input)) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const value = input as Record<string, unknown>;
  const rack = value.rack;
  const avatar = value.avatar;
  if (!rack || typeof rack !== 'object' || !avatar || typeof avatar !== 'object') return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  if (value.contractVersion !== 1 || value.packVersion !== '0.4.0') return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const rackValue = rack as Record<string, unknown>;
  if (rackValue.path !== undefined || rackValue.type !== undefined || rackValue.synthetic !== true || rackValue.realAircraftType !== false || rackValue.realPerson !== false || rackValue.brandMarks !== false) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const avatarValue = avatar as Record<string, unknown>;
  if (avatarValue.path !== undefined || avatarValue.type !== undefined || avatarValue.synthetic !== true || avatarValue.realPerson !== false || avatarValue.brandMarks !== false) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const units = rackValue.units;
  if (!Array.isArray(units) || units.length !== 5) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const seenNodes = new Set<string>(); const seenSources = new Set<string>(); const normalized: Array<{node:string;source:string}> = [];
  for (const unit of units) {
    if (!unit || typeof unit !== 'object') return { valid: false, reason: HANGAR_MANIFEST_ERROR };
    const row = unit as Record<string, unknown>;
    if (typeof row.node !== 'string' || typeof row.source !== 'string' || seenNodes.has(row.node) || seenSources.has(row.source)) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
    if (!(HANGAR_RACK_SOURCES as readonly string[]).includes(row.source)) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
    const approvedNode = `RackUnit${row.source.charAt(0).toUpperCase()}${row.source.slice(1)}`;
    if (row.node !== approvedNode) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
    seenNodes.add(row.node); seenSources.add(row.source); normalized.push({ node: row.node, source: row.source });
  }
  if (HANGAR_RACK_SOURCES.some(source => !seenSources.has(source))) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const aircraftModels = value.aircraft && typeof value.aircraft === 'object'
    ? (value.aircraft as Record<string, unknown>).models
    : undefined;
  if (!Array.isArray(aircraftModels) || aircraftModels.length !== 3) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  for (const model of aircraftModels) {
    if (!model || typeof model !== 'object') return { valid: false, reason: HANGAR_MANIFEST_ERROR };
    const declaration = model as Record<string, unknown>;
    if (declaration.path !== undefined || declaration.type !== undefined || declaration.synthetic !== true || declaration.realAircraftType !== false || declaration.realPerson !== false || declaration.brandMarks !== false) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  }
  const hardHat = avatarValue.hardHat;
  if (!hardHat || typeof hardHat !== 'object') return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const hat = hardHat as Record<string, unknown>;
  if (hat.node !== HANGAR_AVATAR_HARD_HAT || hat.color !== HANGAR_HARD_HAT_COLOR) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  if (avatarValue.synthetic !== true || avatarValue.realPerson !== false || avatarValue.brandMarks !== false) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  const approved = {background:'#E8EDF2',floor:'#C3CBD4',floorMarking:'#F2F4F6',aircraftBody:'#F5F7F9',aircraftTrim:'#9AA5B1',partsStand:'#5A6573',component:'#B7C0CA',rack:'#2F3A47',rackUnit:'#4C5866',avatarBody:'#3D6E9E',groundShadow:'#000000',guideLine:'#0E7C66',selection:'#1F6FEB',labelBackground:'#FFFFFF',labelText:'#1B2430',labelMuted:'#4A5563'};
  if (Object.keys(approved).some(key => (HANGAR_SCENE_THEME as any)[key] !== (approved as any)[key])) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  if (hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelText, HANGAR_SCENE_THEME.labelBackground) < 4.5 || hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelMuted, HANGAR_SCENE_THEME.labelBackground) < 4.5) return { valid: false, reason: HANGAR_MANIFEST_ERROR };
  return { valid: true, units: normalized };
}
export const validateSceneManifest = validateHangarRackAvatarManifest;

export function buildHangarRackDescriptor(input: unknown = sceneManifest): { valid: true; units: ReadonlyArray<{ node: string; source: string }>; openRack: true } | { valid: false; reason: typeof HANGAR_MANIFEST_ERROR } {
  const result = validateHangarRackAvatarManifest(input);
  return result.valid ? { valid: true, units: result.units, openRack: true } : result;
}

export function loadRackAvatarHangarAsset<T>(asset: 'rack' | 'avatar', bytes: unknown, options: {
  loader: (bytes: Uint8Array) => T; onFallback: (reason: typeof HANGAR_ASSET_INVALID) => void;
  provenance: unknown; aggregateBytes: number; resource?: unknown;
}) {
  const manifest = validateHangarRackAvatarManifest();
  if (!manifest.valid) { options.onFallback(HANGAR_ASSET_INVALID); return { loaded: false as const, reason: HANGAR_ASSET_INVALID }; }
  const resource = options.resource;
  const provenance = options.provenance && typeof options.provenance === 'object'
    ? { ...(options.provenance as HangarAssetRecord), assetBytes: bytes instanceof Uint8Array ? bytes : undefined }
    : options.provenance;
  options.onFallback(HANGAR_ASSET_INVALID);
  return { loaded: false as const, reason: HANGAR_ASSET_INVALID };
}
export const loadRackAsset = <T>(bytes: unknown, options: Parameters<typeof loadRackAvatarHangarAsset<T>>[2]) => loadRackAvatarHangarAsset('rack', bytes, options);
export const loadAvatarAsset = <T>(bytes: unknown, options: Parameters<typeof loadRackAvatarHangarAsset<T>>[2]) => loadRackAvatarHangarAsset('avatar', bytes, options);

export const validateRackUnitMappings = validateHangarRackAvatarManifest;
export const buildRackDescriptor = buildHangarRackDescriptor;
export function openValidatedHangarRack(onOpen: () => unknown, input: unknown = sceneManifest, loaderCalls: { count: number; loader?: () => unknown } = { count: 0 }) {
  const descriptor = buildHangarRackDescriptor(input);
  if (!descriptor.valid) return { opened: false, reason: HANGAR_MANIFEST_ERROR };
  if (loaderCalls.count !== 0) return { opened: false, reason: HANGAR_ASSET_INVALID };
  if (typeof loaderCalls.loader === 'function') {
    try { loaderCalls.loader(); } catch { return { opened: false, reason: HANGAR_ASSET_INVALID }; }
    loaderCalls.count += 1;
  }
  const result = onOpen();
  return { opened: true, value: result };
}
export const dataRoomAdapter = { validateManifest: validateHangarRackAvatarManifest, validateSceneManifest, buildRackDescriptor, openValidatedHangarRack, loadRackAsset, loadAvatarAsset, loadRackAvatarHangarAsset };
