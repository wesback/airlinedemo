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
export const HANGAR_JSON_LIMIT = 1_048_576;
export const HANGAR_MAX_JSON_BYTES = HANGAR_JSON_LIMIT;

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
  syntheticMarkerDeclaration?: string;
  realPerson?: boolean;
  realAircraftType?: boolean;
  brandDeclaration?: string;
  hardHatNode?: string;
  manifestHardHatNode?: string;
  role?: string;
  rack?: boolean;
  avatar?: boolean;
  assetBytes?: Uint8Array;
  data?: unknown;
  bytes?: number;
  size?: number;
  byteLength?: number;
  type?: string;
  kind?: string;
};

const invalidAsset = (): never => { throw new Error(HANGAR_ASSET_INVALID); };
const recordBytes = (record: Record<string, unknown>): Uint8Array | undefined => {
  const value = record.assetBytes ?? record.contentBytes ?? record.data;
  return value instanceof Uint8Array ? value : undefined;
};
const isModel = (record: HangarAssetRecord): boolean =>
  ['model', 'glb', 'gltf', 'mesh'].includes(String(record.kind ?? record.type ?? '').toLowerCase()) || /\.(glb|gltf)$/i.test(record.path);

// A small standards implementation avoids making provenance admission asynchronous.
export function computeHangarAssetChecksum(bytes: Uint8Array): string {
  const rightRotate = (value: number, amount: number) => (value >>> amount) | (value << (32 - amount));
  const constants = [0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2];
  const bitLength = bytes.length * 8;
  const paddedLength = Math.ceil((bitLength + 65) / 512) * 64;
  const padded = new Uint8Array(paddedLength);
  padded.set(bytes);
  padded[bytes.length] = 0x80;
  const view = new DataView(padded.buffer);
  view.setUint32(paddedLength - 8, Math.floor(bitLength / 0x100000000), false);
  view.setUint32(paddedLength - 4, bitLength >>> 0, false);
  const h = [0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19];
  for (let offset = 0; offset < paddedLength; offset += 64) {
    const w = new Uint32Array(64);
    for (let i = 0; i < 16; i++) w[i] = view.getUint32(offset + i * 4, false);
    for (let i = 16; i < 64; i++) {
      const s0 = rightRotate(w[i-15],7) ^ rightRotate(w[i-15],18) ^ (w[i-15] >>> 3);
      const s1 = rightRotate(w[i-2],17) ^ rightRotate(w[i-2],19) ^ (w[i-2] >>> 10);
      w[i] = (w[i-16] + s0 + w[i-7] + s1) >>> 0;
    }
    let [a,b,c,d,e,f,g,hh] = h;
    for (let i = 0; i < 64; i++) {
      const s1 = rightRotate(e,6) ^ rightRotate(e,11) ^ rightRotate(e,25);
      const ch = (e & f) ^ (~e & g);
      const t1 = (hh + s1 + ch + constants[i] + w[i]) >>> 0;
      const s0 = rightRotate(a,2) ^ rightRotate(a,13) ^ rightRotate(a,22);
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (s0 + maj) >>> 0;
      hh = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
    }
    h[0]=(h[0]+a)>>>0; h[1]=(h[1]+b)>>>0; h[2]=(h[2]+c)>>>0; h[3]=(h[3]+d)>>>0; h[4]=(h[4]+e)>>>0; h[5]=(h[5]+f)>>>0; h[6]=(h[6]+g)>>>0; h[7]=(h[7]+hh)>>>0;
  }
  return h.map(value => value.toString(16).padStart(8,'0')).join('');
}

export function validateHangarAssetRecord(record: unknown): true {
  if (!record || typeof record !== 'object' || Array.isArray(record)) invalidAsset();
  const asset = record as Record<string, unknown>;
  const path = asset.path ?? asset.assetPath ?? asset.relativePath;
  if (typeof path !== 'string' || path.length === 0 || path.startsWith('/') || path.includes('\\') || /^[a-z][a-z0-9+.-]*:/i.test(path) || path.startsWith('//') || /<[^>]*>|[<>]/.test(path)) invalidAsset();
  const resourceFields = ['uri','url','src','model','modelUri','modelUrl','texture','textureUri','font','fontUri','cdnUrl','externalUri','extensions','extensionsUsed','extensionsRequired'];
  if (resourceFields.some(field => asset[field] !== undefined)) invalidAsset();
  for (const [key, value] of Object.entries(asset)) {
    if (typeof value === 'string' && /<[^>]*>|[<>]/.test(value)) invalidAsset();
    if (typeof value === 'string' && value.includes('\\')) invalidAsset();
    if ((key.toLowerCase().includes('url') || key.toLowerCase().includes('uri')) && typeof value === 'string'
      && !['originalgranturl','cc0granturl'].includes(key.toLowerCase())) {
      if (/^(?:https?:|data:|blob:|javascript:|\/\/)/i.test(value) || /^[a-z][a-z0-9+.-]*:/i.test(value)) invalidAsset();
    }
  }
  for (const field of ['source', 'author']) if (typeof asset[field] !== 'string' || (asset[field] as string).length === 0) invalidAsset();
  const checksum = asset.checksum ?? asset.sha256 ?? asset.sha256Checksum ?? asset.sha256_checksum;
  if (typeof checksum !== 'string' || !/^[a-fA-F0-9]{64}$/.test(checksum)) invalidAsset();
  if (asset.synthetic !== true) invalidAsset();
  if (asset.syntheticMarker !== undefined && asset.syntheticMarker !== true) invalidAsset();
  // The checksum is always computed from the bytes being admitted; a caller-supplied digest is never trusted.
  const rawBytes = recordBytes(asset);
  if (!rawBytes || computeHangarAssetChecksum(rawBytes) !== (checksum as string).toLowerCase()) invalidAsset();
  const declaredSize = asset.bytes ?? asset.byteLength ?? asset.sizeBytes ?? asset.size;
  if (declaredSize !== undefined && declaredSize !== rawBytes!.length) invalidAsset();
  const identityContext = `${String(asset.role ?? '')} ${String(asset.kind ?? '')} ${String(asset.type ?? '')} ${String(asset.path ?? '')}`.toLowerCase();
  const rackOrAvatar = asset.rack === true || asset.avatar === true || identityContext.includes('rack') || identityContext.includes('avatar');
  if (rackOrAvatar) {
    const declaration = String(asset.syntheticMarkerDeclaration ?? asset.syntheticDeclaration ?? asset.syntheticMarkerLabel ?? '').trim();
    if (!declaration || !/(synthetic|fictional|no real|no brand)/i.test(declaration)) invalidAsset();
    if (asset.realPerson === true || asset.realAircraftType === true || asset.brandDeclaration === 'real' || asset.realPersonLikeness === true || asset.realAircraftTypeDepicted === true || asset.brandMarks === true) invalidAsset();
    if (asset.avatar === true || identityContext.includes('avatar')) {
      const nodeHat = asset.hardHatNode ?? asset.hardhatNode;
      const manifestHat = asset.manifestHardHatNode;
      if (typeof manifestHat !== 'string' || manifestHat.trim().length === 0) invalidAsset();
      if (typeof nodeHat !== 'string' || nodeHat.trim().length === 0 || nodeHat !== manifestHat) invalidAsset();
    }
  }
  const rights = asset.rightsBasis ?? asset.rights;
  if (rights === 'Project-authored') {
    const declaration = asset.rightsDeclaration ?? asset.declaration;
    if (typeof declaration !== 'string' || declaration.trim().length === 0) invalidAsset();
  } else if (rights === 'CC0-1.0') {
    const grantUrl = asset.originalGrantUrl ?? asset.cc0GrantUrl;
    const verification = asset.verificationReference ?? asset.humanVerificationReference;
    if (typeof grantUrl !== 'string' || !/^https:\/\/[^\s]+$/i.test(grantUrl.trim())) invalidAsset();
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
      const json = budget.jsonBytes ?? budget.maxJsonBytes ?? budget.jsonChunkBytes;
      if (typeof total === 'number' && (!Number.isSafeInteger(total) || total < 0 || total > HANGAR_TOTAL_ASSET_LIMIT)) invalidAsset();
      if (typeof largest === 'number' && (!Number.isSafeInteger(largest) || largest < 0 || largest > HANGAR_MODEL_LIMIT)) invalidAsset();
      if (json !== undefined && (!Number.isSafeInteger(json) || (json as number) < 0 || (json as number) > HANGAR_JSON_LIMIT)) invalidAsset();
      return true;
    }
  }
  if (!Array.isArray(records)) return invalidAsset();
  let total = 0;
  for (const record of records) {
    validateHangarAssetRecord(record);
    const asset = record as HangarAssetRecord;
    const size = recordBytes(asset as Record<string, unknown>)?.length ?? invalidAsset();
    if (isModel(asset) && size > HANGAR_MODEL_LIMIT) invalidAsset();
    total += size;
    if (!Number.isSafeInteger(total) || total > HANGAR_TOTAL_ASSET_LIMIT) invalidAsset();
  }
  return true;
}

export function validateHangarAssetManifest(manifest: unknown): true {
  if (!manifest || typeof manifest !== 'object') invalidAsset();
  const value = manifest as Record<string, unknown>;
  if ('totalBytes' in value || 'largestModelBytes' in value || 'modelBytes' in value || 'aggregateBytes' in value) return validateHangarAssetBudgets(value);
  const records = Array.isArray(manifest) ? manifest : value.assets;
  if (!Array.isArray(records) || records.length === 0) invalidAsset();
  if (Array.isArray(value.resources)) {
    for (const resource of value.resources) { try { validateHangarResource(resource); } catch { invalidAsset(); } }
  }
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
  const reject = (): never => { throw new Error(HANGAR_RESOURCE_REJECTION); };
  const validateUri = (value: unknown): void => {
    if (typeof value !== 'string') reject();
    const uri = (value as string).trim();
    if (!uri || uri.includes('\\') || uri.includes('://') || uri.startsWith('//') || uri.startsWith('/') || uri.startsWith('#')) reject();
    if (/^[a-z][a-z0-9+.-]*:/i.test(uri) || /^(https?|data|blob|javascript|file):/i.test(uri)) reject();
    if (/\p{Cc}/u.test(uri) || /<[^>]*>|[<>]/u.test(uri)) reject();
  };
  if (typeof resource === 'string') { validateUri(resource); return true; }
  if (!resource || typeof resource !== 'object' || Array.isArray(resource)) reject();
  const value = resource as Record<string, unknown>;
  for (const key of Object.keys(value)) {
    if (/^(uri|modelUri|modelURL|modelUrl|textureUri|fontUri|cdnUrl|externalUri|extensions|extensionsUsed|extensionsRequired)$/i.test(key)) reject();
    const child = value[key];
    if (typeof child === 'string' && /<[^>]*>|[<>]/u.test(child)) reject();
  }
  const candidate = value.url ?? value.src ?? value.path ?? value.assetPath ?? value.relativePath ?? value.resourceUrl;
  if (candidate !== undefined) validateUri(candidate);
  else {
    const kind = String(value.kind ?? value.type ?? value.mimeType ?? '').toLowerCase();
    if (value.embedded === true && (kind === 'embedded-png' || kind === 'embedded-jpeg' || kind === 'image/png' || kind === 'image/jpeg')) return true;
    reject();
  }
  const kind = String(value.kind ?? value.type ?? '').toLowerCase();
  if (kind === 'font' || kind === 'cdn') reject();
  return true;
}
export const validateHangarResourcePolicy = validateHangarResource;
export const validateHangarResourceUrl = (url: unknown) => validateHangarResource(typeof url === 'string' ? url : url);
export function validateHangarResourceResult(resource: unknown): { valid: true } | { valid: false; reason: typeof HANGAR_RESOURCE_REJECTION } {
  try { validateHangarResource(resource); return { valid: true }; } catch { return { valid: false, reason: HANGAR_RESOURCE_REJECTION }; }
}
export const validateResourcePolicy = validateHangarResource;
