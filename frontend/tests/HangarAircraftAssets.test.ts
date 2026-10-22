import { describe, expect, it, vi } from 'vitest';
import provenance from '../src/assets/hangar/hangar-provenance.json';
import manifest from '../src/hangar/sceneManifest.json';
import { loadHangarAircraftAsset } from '../src/hangar/HangarCanvasAdapter';
import { computeHangarAssetChecksum } from '../src/hangar/assetPolicy';
import { buildHangarAircraftViewModel } from '../src/hangar/sceneDescriptor';
import { buildGlb } from './hangarGlbBuilder';

const cases = [
  { caseId: 'cp-case-a', name: 'Case A', aircraftCategory: 'narrow-body', status: { assessment: 'ready', review: null, request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'Rules-based' },
  { caseId: 'cp-case-b', name: 'Case B', aircraftCategory: 'regional', status: { assessment: 'ready', review: 'reviewing', request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'AI agent' },
  { caseId: 'cp-case-c', name: 'Case C', aircraftCategory: 'wide-body', status: { assessment: 'ready', review: 'blocked', request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'Simulated' },
];

describe('Hangar aircraft assets', () => {
  it('Hangar aircraft view model projects scoped cases', () => {
    expect(buildHangarAircraftViewModel(cases, ['cp-case-a', 'cp-case-b', 'cp-case-c'])).toEqual([
      { caseId: 'cp-case-a', name: 'Case A', category: 'narrow-body', status: { assessment: 'ready', review: null, request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'Rules-based', objectId: 'SYN-CP-A-AIR-001' },
      { caseId: 'cp-case-b', name: 'Case B', category: 'regional', status: { assessment: 'ready', review: 'reviewing', request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'AI agent', objectId: 'SYN-CP-B-AIR-001' },
      { caseId: 'cp-case-c', name: 'Case C', category: 'wide-body', status: { assessment: 'ready', review: 'blocked', request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'Simulated', objectId: 'SYN-CP-C-AIR-001' },
    ]);
    expect(buildHangarAircraftViewModel(cases, ['cp-case-b'])).toEqual([
      { caseId: 'cp-case-b', name: 'Case B', category: 'regional', status: { assessment: 'ready', review: 'reviewing', request: null, finance: null, legal: null, acceptance: null }, capabilityLabel: 'AI agent', objectId: 'SYN-CP-B-AIR-001' },
    ]);
    expect(buildHangarAircraftViewModel(cases, ['cp-case-x'])).toEqual([]);
    const four = [...cases, { caseId: 'cp-case-x', name: 'Extra', aircraftCategory: 'unknown', status: {}, capabilityLabel: 'Extra' }];
    expect(buildHangarAircraftViewModel(four, ['cp-case-a', 'cp-case-b', 'cp-case-c', 'cp-case-x'])).toHaveLength(3);
  });

  it('Hangar aircraft assets validate before loader', () => {
    const records = provenance.assets as any[];
    for (const [asset, category] of [['aircraft-a', 'narrow-body'], ['aircraft-b', 'regional'], ['aircraft-c', 'wide-body']] as const) {
      const bytes = buildGlb();
      const path = `assets/hangar/${asset}.glb`;
      const record = records.find(r => r.path === path)!;
      const testRecord = { ...record, checksum: computeHangarAssetChecksum(bytes), sizeBytes: bytes.length };
      const loader = vi.fn(() => `${asset}-scene`);
      const fallback = vi.fn();
      const result = loadHangarAircraftAsset(asset, bytes, { loader, onFallback: fallback, aggregateBytes: bytes.length, resource: { path, type: 'model' }, provenance: testRecord });
      expect(result).toEqual({ loaded: true, value: `${asset}-scene` });
      expect(loader).toHaveBeenCalledExactlyOnceWith(bytes);
      expect(fallback).not.toHaveBeenCalled();
      expect(record.synthetic).toBe(true);
      expect(record.illustrativeCategory).toBe(category);
    }

    const bytes = buildGlb();
    const record = records.find(r => r.path === 'assets/hangar/aircraft-a.glb')!;
    const validRecord = { ...record, checksum: computeHangarAssetChecksum(bytes), sizeBytes: bytes.length };
    const malformedProvenance = [
      { ...validRecord, synthetic: false }, { ...validRecord, realAircraftType: true },
      { ...validRecord, brandMarks: true }, { ...validRecord, rightsBasis: 'licensed' },
      { ...validRecord, syntheticMarkerDeclaration: undefined }, { ...validRecord, checksum: '0'.repeat(64) },
    ];
    for (const badRecord of malformedProvenance) {
      const loader = vi.fn(); const fallback = vi.fn();
      const result = loadHangarAircraftAsset('aircraft-a', bytes, { loader, onFallback: fallback, aggregateBytes: bytes.length, resource: { path: 'assets/hangar/aircraft-a.glb', type: 'model' }, provenance: badRecord });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled(); expect(fallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }

    const loader = vi.fn(); const fallback = vi.fn();
    loadHangarAircraftAsset('aircraft-a', bytes, { loader, onFallback: fallback, aggregateBytes: bytes.length, resource: { path: 'assets/hangar/aircraft-a.glb', type: 'model' } });
    expect(loader).not.toHaveBeenCalled(); expect(fallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');

    for (const badBytes of [bytes.slice(0, -1), null, new Uint8Array(5_242_881)]) {
      const badLoader = vi.fn(); const badFallback = vi.fn();
      const result = loadHangarAircraftAsset('aircraft-a', badBytes, { loader: badLoader, onFallback: badFallback, aggregateBytes: badBytes instanceof Uint8Array ? badBytes.length : 0, resource: { path: 'assets/hangar/aircraft-a.glb', type: 'model' }, provenance: validRecord });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(badLoader).not.toHaveBeenCalled(); expect(badFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }

    const externalLoader = vi.fn(); const externalFallback = vi.fn();
    loadHangarAircraftAsset('aircraft-a', bytes, { loader: externalLoader, onFallback: externalFallback, aggregateBytes: bytes.length, resource: { url: 'https://cdn.example/aircraft-a.glb' }, provenance: validRecord });
    expect(externalLoader).not.toHaveBeenCalled(); expect(externalFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');

    expect(manifest.aircraft.models).toHaveLength(3);
  });
});
