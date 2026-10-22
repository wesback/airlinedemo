export const HANGAR_ASSET_INVALID = 'asset_invalid';
export const HANGAR_TOTAL_ASSET_LIMIT = 15_728_640;
export const HANGAR_MAX_TOTAL_BYTES = HANGAR_TOTAL_ASSET_LIMIT;
export const HANGAR_MODEL_LIMIT = 5_242_880;
export const HANGAR_MAX_MODEL_BYTES = HANGAR_MODEL_LIMIT;
export const HANGAR_RESOURCE_REJECTION = 'resource_rejected';
export const HANGAR_RESOURCE_POLICY_ERROR = HANGAR_RESOURCE_REJECTION;
export const HANGAR_ASSET_TOTAL_LIMIT = HANGAR_TOTAL_ASSET_LIMIT;
export const HANGAR_ASSET_MODEL_LIMIT = HANGAR_MODEL_LIMIT;
export const HANGAR_ASSET_BUDGET_EXCEEDED = 'asset_budget_exceeded';
export const HANGAR_TOTAL_BUDGET_EXCEEDED = HANGAR_ASSET_BUDGET_EXCEEDED;
export const HANGAR_MODEL_BUDGET_EXCEEDED = HANGAR_ASSET_BUDGET_EXCEEDED;

export type HangarRightsBasis = 'Project-authored' | 'CC0-1.0';
export type HangarAssetRecord = {
  path: string;
  source: string;
  author: string;
  rightsBasis: HangarRightsBasis;
  checksum: string;
  synthetic: boolean;
  syntheticMarker?: boolean;
  sizeBytes?: number;
  rightsDeclaration?: string;
  originalGrantUrl?: string;
  verificationReference?: string;
  bytes?: number;
  size?: number;
  byteLength?: number;
  type?: string;
  kind?: string;
};

const invalidAsset = (): never => { throw new Error(HANGAR_ASSET_INVALID); };
const safeSize = (record: HangarAssetRecord): number => {
  const value = record.bytes ?? record.byteLength ?? record.sizeBytes ?? record.size;
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : 0;
};
const isModel = (record: HangarAssetRecord): boolean =>
  ['model', 'glb', 'gltf', 'mesh'].includes(String(record.kind ?? record.type ?? '').toLowerCase()) || /\.(glb|gltf)$/i.test(record.path);

export function validateHangarAssetRecord(record: unknown): true {
  if (!record || typeof record !== 'object' || Array.isArray(record)) invalidAsset();
  const asset = record as Record<string, unknown>;
  const path = asset.path ?? asset.assetPath ?? asset.relativePath;
  if (typeof path !== 'string' || path.length === 0 || path.startsWith('/') || /^[a-z][a-z0-9+.-]*:/i.test(path) || path.startsWith('//')) invalidAsset();
  for (const field of ['source', 'author']) if (typeof asset[field] !== 'string' || (asset[field] as string).length === 0) invalidAsset();
  const checksum = asset.checksum ?? asset.sha256 ?? asset.sha256Checksum ?? asset.sha256_checksum;
  if (typeof checksum !== 'string' || !/^[a-fA-F0-9]{64}$/.test(checksum)) invalidAsset();
  if (asset.synthetic !== true && asset.syntheticMarker !== true) invalidAsset();
  const actualChecksum = asset.actualChecksum ?? asset.computedChecksum ?? asset.bytesSha256 ?? asset.actualSha256 ?? asset.computedSha256;
  if (typeof actualChecksum === 'string' && actualChecksum.toLowerCase() !== (checksum as string).toLowerCase()) invalidAsset();
  const rights = asset.rightsBasis ?? asset.rights;
  if (rights === 'Project-authored') {
    const declaration = asset.rightsDeclaration ?? asset.declaration;
    if (typeof declaration !== 'string' || declaration.trim().length === 0) invalidAsset();
  } else if (rights === 'CC0-1.0') {
    const grantUrl = asset.originalGrantUrl ?? asset.cc0GrantUrl;
    const verification = asset.verificationReference ?? asset.humanVerificationReference;
    if (typeof grantUrl !== 'string' || grantUrl.trim().length === 0) invalidAsset();
    if (typeof verification !== 'string' || verification.trim().length === 0) invalidAsset();
  } else invalidAsset();
  return true;
}

export function validateHangarAssetBudgets(records: unknown): true {
  if (records && typeof records === 'object' && !Array.isArray(records)) {
    const budget = records as Record<string, unknown>;
    if ('totalBytes' in budget || 'largestModelBytes' in budget || 'modelBytes' in budget) {
      const total = budget.totalBytes ?? budget.bytesTotal ?? budget.aggregateBytes;
      const largest = budget.largestModelBytes ?? budget.modelBytes ?? budget.maxModelBytes;
      if (typeof total === 'number' && (!Number.isSafeInteger(total) || total < 0 || total > HANGAR_TOTAL_ASSET_LIMIT)) invalidAsset();
      if (typeof largest === 'number' && (!Number.isSafeInteger(largest) || largest < 0 || largest > HANGAR_MODEL_LIMIT)) invalidAsset();
      return true;
    }
  }
  if (!Array.isArray(records)) return invalidAsset();
  let total = 0;
  for (const record of records) {
    validateHangarAssetRecord(record);
    const asset = record as HangarAssetRecord;
    const rawSize = asset.bytes ?? asset.byteLength ?? asset.sizeBytes ?? asset.size;
    if (typeof rawSize !== 'number' || !Number.isSafeInteger(rawSize) || rawSize < 0) invalidAsset();
    const size = Number(rawSize);
    if (isModel(asset) && size > HANGAR_MODEL_LIMIT) invalidAsset();
    total += size;
    if (!Number.isSafeInteger(total) || total > HANGAR_TOTAL_ASSET_LIMIT) invalidAsset();
  }
  return true;
}

export function validateHangarAssetManifest(manifest: unknown): true {
  if (!manifest || typeof manifest !== 'object') invalidAsset();
  const records = Array.isArray(manifest) ? manifest : (manifest as { assets?: unknown }).assets;
  if (!Array.isArray(records) || records.length === 0) invalidAsset();
  return validateHangarAssetBudgets(records);
}
export const validateHangarAssetPolicy = validateHangarAssetManifest;
export const validateAssetProvenance = validateHangarAssetRecord;
export const validateAssetBudgets = validateHangarAssetBudgets;
export const validateHangarAssetBundle = validateHangarAssetManifest;
export const validateHangarAssetByteBudgets = validateHangarAssetBudgets;
export const admitHangarAssets = validateHangarAssetManifest;
export const HANGAR_ASSET_LIMITS = { totalBytes: HANGAR_TOTAL_ASSET_LIMIT, modelBytes: HANGAR_MODEL_LIMIT } as const;

export function validateHangarAssetPolicyResult(manifest: unknown): { valid: true } | { valid: false; reason: typeof HANGAR_ASSET_INVALID } {
  try { validateHangarAssetManifest(manifest); return { valid: true }; } catch { return { valid: false, reason: HANGAR_ASSET_INVALID }; }
}
export const validateHangarAssetManifestResult = validateHangarAssetPolicyResult;

export function validateHangarResource(resource: unknown): true {
  if (typeof resource === 'string') {
    const uri = resource.trim();
    if (!uri || uri.startsWith('#') || /^(https?:|\/\/|data:|blob:|[a-z][a-z0-9+.-]*:)/i.test(uri) || uri.includes('://')) {
      throw new Error(HANGAR_RESOURCE_REJECTION);
    }
    return true;
  }
  if (!resource || typeof resource !== 'object' || Array.isArray(resource)) throw new Error(HANGAR_RESOURCE_REJECTION);
  const value = resource as Record<string, unknown>;
  const forbiddenField = Object.keys(value).find((key) => /^(uri|modelUri|modelURL|modelUrl|textureUri|fontUri|cdnUrl|externalUri)$/i.test(key));
  if (forbiddenField !== undefined || value.uri !== undefined) throw new Error(HANGAR_RESOURCE_REJECTION);
  const candidate = value.url ?? value.src ?? value.path ?? value.assetPath ?? value.relativePath ?? value.resourceUrl;
  if (candidate !== undefined && typeof candidate !== 'string') throw new Error(HANGAR_RESOURCE_REJECTION);
  const kind = String(value.kind ?? value.type ?? '').toLowerCase();
  if (candidate === undefined) {
    if (kind === 'embedded-png' || kind === 'embedded-jpeg') return true;
    throw new Error(HANGAR_RESOURCE_REJECTION);
  }
  if (kind === 'font' || kind === 'cdn') throw new Error(HANGAR_RESOURCE_REJECTION);
  if ((kind === 'texture' || kind === 'model') && (value.url !== undefined || value.src !== undefined)) throw new Error(HANGAR_RESOURCE_REJECTION);
  const uri = (candidate as string).trim();
  if (!uri || uri.startsWith('#') || /^(https?:|\/\/|data:|blob:|[a-z][a-z0-9+.-]*:)/i.test(uri) || uri.includes('://')) {
    throw new Error(HANGAR_RESOURCE_REJECTION);
  }
  return true;
}
export const validateHangarResourcePolicy = validateHangarResource;
export const validateHangarResourceUrl = (url: unknown) => validateHangarResource(typeof url === 'string' ? url : url);
export function validateHangarResourceResult(resource: unknown): { valid: true } | { valid: false; reason: typeof HANGAR_RESOURCE_REJECTION } {
  try { validateHangarResource(resource); return { valid: true }; } catch { return { valid: false, reason: HANGAR_RESOURCE_REJECTION }; }
}
export const validateResourcePolicy = validateHangarResource;
