import { describe, expect, it, vi } from 'vitest';
import {
  HANGAR_3D_ACTIONS,
  HANGAR_NON_3D_ACTIONS,
  getHangarEntryDecision,
  getHangarFallbackReason,
  getHangarNon3dActions,
  selectHangarEntry,
} from '../src/hangar/entryController';

describe('HangarEntry', () => {
  it('HangarEntry rejects unsupported browser and missing WebGL', () => {
    expect(getHangarEntryDecision({ browserSupported: false, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
    expect(getHangarEntryDecision({ browserSupported: true, webgl2: null }).fallbackReason).toBe('webgl_unavailable');
    expect(getHangarEntryDecision({ browserSupported: false, webgl2: null })).toMatchObject({ mode: 'fallback', fallbackReason: 'unsupported_browser' });
  });

  it('HangarEntry contract eligibility cases', async () => {
    const loader = vi.fn();
    await expect(selectHangarEntry({ browserSupported: true, contractVersion: 1, webgl2: { webgl2: true } }, loader)).resolves.toMatchObject({ mode: '3d', loaderCalled: true });
    expect(loader).toHaveBeenCalledTimes(1);

    const unsupported = await selectHangarEntry({ browserSupported: false, contractVersion: 1, webgl2: { webgl2: true } }, loader);
    expect(unsupported).toMatchObject({ fallbackReason: 'unsupported_browser', mode: 'fallback' });

    const contractCases: Array<{ contractVersion: unknown; browserEligibility: unknown; probe: unknown }> = [
      { contractVersion: 2, browserEligibility: true, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: false, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: true, probe: null },
    ];
    for (const entry of contractCases) {
      const calls = vi.fn();
      const decision = await selectHangarEntry({ contractVersion: entry.contractVersion, browserSupported: entry.browserEligibility, webgl2: entry.probe as never }, calls);
      expect(decision.mode).toBe('fallback');
      expect(calls).not.toHaveBeenCalled();
    }
  });

  it('HangarEntry lazy loads only after selection', async () => {
    const loader = vi.fn(async () => ({ scene: true }));
    const modelRequest = vi.fn();
    const regular = await selectHangarEntry({ browserSupported: true, webgl2: { webgl2: true } });
    expect(regular.mode).toBe('3d');
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();
    await selectHangarEntry({ browserSupported: false, webgl2: { webgl2: true } }, loader);
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();
  });

  it('HangarEntry non-3D parity', () => {
    expect(HANGAR_NON_3D_ACTIONS).toEqual(['selection', 'status', 'evidence', 'journey', 'review']);
    expect(HANGAR_3D_ACTIONS).toEqual(HANGAR_NON_3D_ACTIONS);
    expect(getHangarNon3dActions()).toEqual(HANGAR_3D_ACTIONS);
    expect(getHangarFallbackReason({ browserSupported: false, webgl2: { webgl2: true } })).toBe('unsupported_browser');
  });

  it('supported versions pass eligibility and unsupported versions reject', () => {
    for (const version of [120, 121, 139, 140]) {
      expect(getHangarEntryDecision({ browser: { name: 'Chrome', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
      expect(getHangarEntryDecision({ browser: { name: 'Edge', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
    }
    expect(getHangarEntryDecision({ browser: { name: 'Firefox', version: 140 }, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
  });
});
