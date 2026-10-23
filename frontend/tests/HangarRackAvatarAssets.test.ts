import { describe, expect, it, vi } from 'vitest';
import manifest from '../src/hangar/sceneManifest.json';
import { loadRackAvatarHangarAsset } from '../src/hangar/dataRoomAdapter';
import { validateHangarRackAvatarManifest } from '../src/hangar/dataRoomAdapter';

describe('Hangar rack and avatar assets', () => {
  it('rack and avatar are code-built synthetic declarations with no resources', () => {
    expect(manifest.rack).not.toHaveProperty('path');
    expect(manifest.rack).not.toHaveProperty('type');
    expect(manifest.rack.synthetic).toBe(true);
    expect(manifest.rack.realPerson).toBe(false);
    expect(manifest.rack.realAircraftType).toBe(false);
    expect(manifest.rack.brandMarks).toBe(false);
    expect(manifest.avatar).not.toHaveProperty('path');
    expect(manifest.avatar).not.toHaveProperty('type');
    expect(manifest.avatar.synthetic).toBe(true);
    expect(manifest.avatar.realPerson).toBe(false);
    expect(manifest.avatar.brandMarks).toBe(false);
  });

  it('zero-model rack or avatar requests reject before loader invocation', () => {
    expect(validateHangarRackAvatarManifest().valid).toBe(true);
    for (const asset of ['rack', 'avatar'] as const) {
      const loader = vi.fn(); const fallback = vi.fn();
      const result = loadRackAvatarHangarAsset(asset, new Uint8Array([1]), { loader, onFallback: fallback, provenance: {}, aggregateBytes: 1 });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled();
      expect(fallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }
  });
});
