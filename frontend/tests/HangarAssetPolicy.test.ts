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
  checksum: 'a'.repeat(64), actualChecksum: 'a'.repeat(64), synthetic: true, bytes: 1000, type: 'model', ...overrides,
});

describe('Hangar asset policy', () => {
  it('rejects missing, unsupported, or mismatched asset provenance', () => {
    const valid = validateHangarAssetPolicyResult({ assets: [grant()] });
    expect(valid).toEqual({ valid: true });
    for (const record of [
      null, { ...grant(), checksum: 'bad' }, { ...grant(), actualChecksum: 'b'.repeat(64) }, { ...grant(), rightsBasis: 'licensed' }, { ...grant(), rightsBasis: 'GPL-3.0' },
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

  it('enforces exact per-model, JSON, and aggregate asset budgets', () => {
    expect(validateHangarAssetPolicyResult({ assets: [{ ...grant(), bytes: HANGAR_MAX_MODEL_BYTES }] }).valid).toBe(true);
    expect(validateHangarAssetPolicyResult({ assets: [{ ...grant(), bytes: HANGAR_MAX_MODEL_BYTES + 1 }] }).valid).toBe(false);
    const boundary = { assets: [grant({ bytes: 5_242_880 }), grant({ bytes: 5_242_880 }), grant({ path: 'assets/texture.png', type: 'texture', bytes: 5_242_880 })] };
    expect(HANGAR_MAX_TOTAL_BYTES).toBe(15_728_640);
    expect(validateHangarAssetPolicyResult(boundary).valid).toBe(true);
    expect(validateHangarAssetPolicyResult({ assets: [grant({ bytes: HANGAR_MAX_MODEL_BYTES }), grant({ bytes: HANGAR_MAX_MODEL_BYTES }), grant({ path: 'assets/texture.png', type: 'texture', bytes: 5_242_881 })] }).valid).toBe(false);
    expect(validateHangarAssetPolicyResult({ totalBytes: HANGAR_MAX_TOTAL_BYTES, largestModelBytes: HANGAR_MAX_MODEL_BYTES, jsonBytes: 1_048_576 }).valid).toBe(true);
    expect(validateHangarAssetPolicyResult({ totalBytes: HANGAR_MAX_TOTAL_BYTES + 1 }).valid).toBe(false);
  });
});
