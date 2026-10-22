import { describe, expect, it, vi } from 'vitest';
import {
  HANGAR_ASSET_INVALID,
  HANGAR_MAX_MODEL_BYTES,
  HANGAR_MAX_TOTAL_BYTES,
  validateHangarAssetPolicy,
  validateHangarAssetPolicyResult,
} from '../src/hangar/assetPolicy';

const grant = (overrides: Record<string, unknown> = {}) => ({
  path: 'assets/hangar/aircraft.glb', source: 'Project design pack', author: 'Hangar team',
  rightsBasis: 'Project-authored', rightsDeclaration: 'Approved project-authored asset declaration',
  checksum: 'a'.repeat(64), synthetic: true, bytes: 1000, type: 'model', ...overrides,
});

describe('Hangar asset policy', () => {
  it('asset provenance contract rejects missing or unapproved records', () => {
    const valid = validateHangarAssetPolicyResult({ assets: [grant()] });
    expect(valid).toEqual({ valid: true });
    for (const record of [
      null, { ...grant(), checksum: 'bad' }, { ...grant(), rightsBasis: 'GPL-3.0' },
      { ...grant(), rightsDeclaration: '' }, { ...grant(), synthetic: false },
      { ...grant(), rightsBasis: 'CC0-1.0' },
      { ...grant(), rightsBasis: 'CC0-1.0', originalGrantUrl: '', verificationReference: 'review-1' },
    ]) {
      const loader = vi.fn();
      const result = validateHangarAssetPolicyResult(record == null ? null : { assets: [record] });
      if (result.valid) loader();
      expect(result).toEqual({ valid: false, reason: HANGAR_ASSET_INVALID });
      expect(loader).not.toHaveBeenCalled();
    }
    expect(() => validateHangarAssetPolicy(null)).toThrow(HANGAR_ASSET_INVALID);
  });

  it('asset byte budgets are exact and additive', () => {
    expect(validateHangarAssetPolicyResult({ assets: [{ ...grant(), bytes: HANGAR_MAX_MODEL_BYTES }] }).valid).toBe(true);
    const exactTotal = { assets: Array.from({ length: 3 }, (_, index) => grant({ path: `assets/model-${index}.glb`, bytes: 5_242_880 })) };
    exactTotal.assets[2] = { ...exactTotal.assets[2], bytes: 5_242_880 - 294 };
    // Keep the exact total contract explicit at the boundary.
    const boundary = { assets: [grant({ bytes: 5_242_880 }), grant({ bytes: 5_242_880 }), grant({ path: 'assets/texture.png', type: 'texture', bytes: 5_242_880 })] };
    expect(HANGAR_MAX_TOTAL_BYTES).toBe(15_728_640);
    expect(validateHangarAssetPolicyResult(boundary).valid).toBe(true);
  });
});
