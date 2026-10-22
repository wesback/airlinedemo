import { HANGAR_ASSET_INVALID, validateHangarGlbResult, type HangarGlbLimits } from './glbValidator';
import { validateHangarResource, validateHangarAssetRecord, HANGAR_JSON_LIMIT, HANGAR_MODEL_LIMIT, HANGAR_TOTAL_ASSET_LIMIT } from './assetPolicy';

export type HangarAssetFailure = typeof HANGAR_ASSET_INVALID;

export type HangarAssetLoadOptions<T> = {
  /** The renderer's loader. It is only called with bytes that passed validation. */
  loader: (bytes: Uint8Array) => T;
  /** Called exactly once, with the reason, when the bytes are rejected. */
  onFallback: (reason: HangarAssetFailure) => void;
  /** Tighter GLB limits; they never raise the model or JSON caps. */
  limits?: HangarGlbLimits;
  /** Where the asset is served from: a relative same-origin path. */
  resource: unknown;
  /** The asset's provenance record; its checksum is verified against `bytes`. */
  provenance: unknown;
  /** Bytes of every Hangar asset in the scene, this one included. */
  aggregateBytes: number;
};

const capped = (values: unknown[], cap: number): number => {
  let effective = cap;
  for (const value of values) {
    if (value !== undefined) {
      if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) return -1;
      effective = Math.min(effective, value);
    }
  }
  return effective;
};

/**
 * The admission boundary for every Hangar asset: structure, model and JSON caps, aggregate budget, resource origin and
 * byte-verified provenance are all checked before the loader runs. A rejected asset never reaches the loader and is
 * reported once.
 */
export function loadValidatedHangarAsset<T>(
  bytes: unknown,
  { loader, onFallback, limits, resource, provenance, aggregateBytes }: HangarAssetLoadOptions<T>,
): { loaded: true; value: T } | { loaded: false; reason: HangarAssetFailure } {
  const reject = () => { onFallback(HANGAR_ASSET_INVALID); return { loaded: false, reason: HANGAR_ASSET_INVALID } as const; };
  if (!(bytes instanceof Uint8Array)) return reject();
  const maxTotalBytes = capped([limits?.maxTotalBytes, limits?.totalLimitBytes, limits?.maxTotal, limits?.totalLimit, limits?.maxTotalLength, limits?.totalLimitBytes], HANGAR_MODEL_LIMIT);
  const maxJsonBytes = capped([limits?.maxJsonBytes, limits?.jsonLimitBytes, limits?.maxJson, limits?.jsonLimit, limits?.maxJsonLength, limits?.maxJsonChunkBytes], HANGAR_JSON_LIMIT);
  if (maxTotalBytes < 0 || maxJsonBytes < 0) return reject();
  const glbLimits = { maxTotalBytes, maxJsonBytes };
  if (!validateHangarGlbResult(bytes, glbLimits).valid) return reject();
  if (!Number.isSafeInteger(aggregateBytes) || aggregateBytes < bytes.length || aggregateBytes > HANGAR_TOTAL_ASSET_LIMIT) return reject();
  try {
    validateHangarResource(resource);
    if (!provenance || typeof provenance !== 'object') return reject();
    validateHangarAssetRecord({ ...(provenance as object), assetBytes: bytes });
  } catch { return reject(); }
  return { loaded: true, value: loader(bytes) };
}
