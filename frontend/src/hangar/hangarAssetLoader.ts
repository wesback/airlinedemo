import { HANGAR_ASSET_INVALID, validateHangarGlbResult, type HangarGlbLimits } from './glbValidator';
import { validateHangarResource, validateHangarAssetRecord, validateHangarAssetBudgets, HANGAR_JSON_LIMIT, HANGAR_MODEL_LIMIT } from './assetPolicy';

export type HangarAssetFailure = typeof HANGAR_ASSET_INVALID;

export type HangarAssetLoadOptions<T> = {
  /** The renderer's loader. It is only called with bytes that passed validation. */
  loader: (bytes: Uint8Array) => T;
  /** Called exactly once, with the reason, when the bytes are rejected. */
  onFallback: (reason: HangarAssetFailure) => void;
  limits?: HangarGlbLimits;
  resource: unknown;
  provenance: unknown;
};

/** Validates the bytes, then calls the loader once; invalid bytes never reach the loader and are reported once. */
export function loadValidatedHangarAsset<T>(
  bytes: unknown,
  { loader, onFallback, limits, resource, provenance }: HangarAssetLoadOptions<T>,
): { loaded: true; value: T } | { loaded: false; reason: HangarAssetFailure } {
  const result = validateHangarGlbResult(bytes, limits);
  if (result.valid) {
    if (!(bytes instanceof Uint8Array)) {
      onFallback(HANGAR_ASSET_INVALID);
      return { loaded: false, reason: HANGAR_ASSET_INVALID };
    }
    try { validateHangarResource(resource); } catch { onFallback(HANGAR_ASSET_INVALID); return { loaded: false, reason: HANGAR_ASSET_INVALID }; }
    try {
      const tiedProvenance = provenance && typeof provenance === 'object'
        ? { ...(provenance as object), assetBytes: bytes, bytes: bytes.length }
        : provenance;
      validateHangarAssetRecord(tiedProvenance);
      validateHangarAssetBudgets([tiedProvenance]);
      const glbBudget = validateHangarGlbResult(bytes, { maxTotalBytes: HANGAR_MODEL_LIMIT, maxJsonBytes: HANGAR_JSON_LIMIT });
      if (!glbBudget.valid) throw new Error(HANGAR_ASSET_INVALID);
    } catch { onFallback(HANGAR_ASSET_INVALID); return { loaded: false, reason: HANGAR_ASSET_INVALID }; }
  }
  if (!result.valid) {
    onFallback(result.reason);
    return { loaded: false, reason: result.reason };
  }
  return { loaded: true, value: loader(bytes as Uint8Array) };
}
