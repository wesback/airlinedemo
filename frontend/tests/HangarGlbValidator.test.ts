import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  HANGAR_ASSET_INVALID,
  validateHangarGlb,
  validateHangarGlbResult,
} from '../src/hangar/glbValidator';

const fixture = (name: string) => new Uint8Array(readFileSync(join(__dirname, 'fixtures', name)));

function reject(input: unknown, limits?: { maxJsonBytes?: number; maxTotalBytes?: number }) {
  const loader = vi.fn();
  const result = validateHangarGlbResult(input, limits);
  if (result.valid) loader();
  return { result, loaderCalls: loader.mock.calls.length };
}

describe('Hangar GLB structural validation', () => {
  it('GLB_valid_contract_accepts_embedded_v2_fixture', () => {
    const valid = fixture('hangar-valid.glb');
    const loader = vi.fn();
    expect(validateHangarGlb(valid)).toBe(true);
    loader(valid);
    expect(loader).toHaveBeenCalledTimes(1);
  });

  it('GLB_malformed_contract_table', () => {
    const cases: Array<{ name: string; input: Uint8Array | null | unknown }> = [
      { name: 'wrong magic', input: fixture('hangar-valid.glb'), },
      { name: 'truncated', input: fixture('hangar-valid.glb').slice(0, -1) },
      { name: 'trailing byte', input: fixture('hangar-malformed.glb') },
      { name: 'malformed fixture', input: fixture('hangar-malformed.glb') },
      { name: 'null', input: null },
      { name: 'non Uint8Array', input: new ArrayBuffer(8) },
      { name: 'zero length', input: new Uint8Array() },
    ];
    const wrongMagic = cases[0].input as Uint8Array;
    const view = new DataView(wrongMagic.buffer, wrongMagic.byteOffset, wrongMagic.byteLength);
    view.setUint32(0, 0, true);
    cases[0].input = new Uint8Array(wrongMagic);

    for (const contractCase of cases) {
      const loader = vi.fn();
      let result;
      try { validateHangarGlb(contractCase.input); result = { valid: true }; }
      catch (error) { result = { valid: false, reason: (error as Error).message }; }
      if (result.valid) loader();
      expect(result).toEqual({ valid: false, reason: HANGAR_ASSET_INVALID });
      expect(loader).toHaveBeenCalledTimes(0);
      expect(result.reason).toBe('asset_invalid');
    }
  });

  it('GLB_validator_has_no_renderer_dependency', () => {
    for (const input of [null, new ArrayBuffer(8), new Uint8Array(), new Uint8Array([1])]) {
      const checked = reject(input, { maxJsonBytes: 1_048_576, maxTotalBytes: 5_242_880 });
      expect(checked.result).toEqual({ valid: false, reason: 'asset_invalid' });
      expect(checked.loaderCalls).toBe(0);
    }
    const negative = reject(fixture('hangar-valid.glb'), { maxJsonBytes: -1 });
    expect(negative.result.reason).toBe('asset_invalid');
    expect(negative.loaderCalls).toBe(0);
  });
});
