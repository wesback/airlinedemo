import { HANGAR_ASSET_INVALID, validateHangarGlbResult, type HangarGlbLimits } from './glbValidator';

export type HangarAssetFailure = typeof HANGAR_ASSET_INVALID;

export type HangarAssetLoadOptions<T> = {
  /** The renderer's loader. It is only called with bytes that passed validation. */
  loader: (bytes: Uint8Array) => T;
  /** Called exactly once, with the reason, when the bytes are rejected. */
  onFallback: (reason: HangarAssetFailure) => void;
  limits?: HangarGlbLimits;
};

/** Validates the bytes, then calls the loader once; invalid bytes never reach the loader and are reported once. */
export function loadValidatedHangarAsset<T>(
  bytes: unknown,
  { loader, onFallback, limits }: HangarAssetLoadOptions<T>,
): { loaded: true; value: T } | { loaded: false; reason: HangarAssetFailure } {
  const result = validateHangarGlbResult(bytes, limits);
  if (!result.valid) {
    onFallback(result.reason);
    return { loaded: false, reason: result.reason };
  }
  return { loaded: true, value: loader(bytes as Uint8Array) };
}
