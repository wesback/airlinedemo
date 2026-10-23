import { describe, expect, it, vi } from 'vitest';
import provenance from '../src/assets/hangar/hangar-provenance.json';
import manifest from '../src/hangar/sceneManifest.json';
import { loadHangarAircraftAsset } from '../src/hangar/HangarCanvasAdapter';
import { buildHangarAircraftViewModel, validateHangarAircraftDescriptor } from '../src/hangar/sceneDescriptor';

const cases = [
  { caseId: 'cp-case-a', name: 'Case A', aircraftCategory: 'narrow-body', status: {}, capabilityLabel: 'Rules-based' },
  { caseId: 'cp-case-b', name: 'Case B', aircraftCategory: 'regional', status: {}, capabilityLabel: 'AI agent' },
  { caseId: 'cp-case-c', name: 'Case C', aircraftCategory: 'wide-body', status: {}, capabilityLabel: 'Simulated' },
];

describe('Hangar aircraft assets', () => {
  it('version zero asset declarations', () => {
    expect(provenance.assets).toEqual([]);
    expect(manifest.aircraft.models).toHaveLength(3);
    expect(validateHangarAircraftDescriptor(manifest).valid).toBe(true);
    for (const model of manifest.aircraft.models) {
      expect(model).not.toHaveProperty('path');
      expect(model).not.toHaveProperty('type');
      expect(model.synthetic).toBe(true);
      expect(model.realAircraftType).toBe(false);
      expect(model.brandMarks).toBe(false);
    }

    const invalid = JSON.parse(JSON.stringify(manifest));
    invalid.aircraft.models[0].path = 'assets/hangar/aircraft-a.glb';
    const loader = vi.fn(); const fallback = vi.fn();
    expect(validateHangarAircraftDescriptor(invalid).valid).toBe(false);
    const result = loadHangarAircraftAsset('aircraft-a', new Uint8Array([1]), {
      loader, onFallback: fallback, aggregateBytes: 1, manifest: invalid,
    });
    expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
    expect(loader).not.toHaveBeenCalled();
    expect(fallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
  });

  it('Hangar aircraft view model projects scoped cases', () => {
    expect(buildHangarAircraftViewModel(cases, ['cp-case-a', 'cp-case-b', 'cp-case-c'])).toHaveLength(3);
    expect(buildHangarAircraftViewModel(cases, ['cp-case-x'])).toEqual([]);
  });

  it('zero-model adapter rejects before loader invocation', () => {
    const loader = vi.fn();
    const fallback = vi.fn();
    const result = loadHangarAircraftAsset('aircraft-a', new Uint8Array([1]), { loader, onFallback: fallback, aggregateBytes: 1 });
    expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
    expect(loader).not.toHaveBeenCalled();
    expect(fallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
  });
});
