// @ts-ignore -- Vitest loads node:fs at runtime; this package intentionally has no Node declaration dependency.
import { readFileSync, readdirSync } from 'node:fs';
import { createElement } from 'react';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  HANGAR_ASSET_INVALID,
  HANGAR_MAX_TOTAL_BYTES,
  computeHangarAssetChecksum,
  validateHangarAssetPolicy,
  validateHangarAssetPolicyResult,
  validateHangarAssetRecord,
  validateHangarResourceResult,
} from '../src/hangar/assetPolicy';
import HangarAssetFallback from '../src/hangar/HangarAssetFallback';
import { loadHangarAircraftAsset } from '../src/hangar/HangarCanvasAdapter';
import sceneManifest from '../src/hangar/sceneManifest.json';

const cloneManifest = () => JSON.parse(JSON.stringify(sceneManifest));
afterEach(cleanup);

// Uint8Array.from keeps the bytes in this realm; jsdom's TextEncoder returns a Node-realm array.
const bytesOf = (text: string) => Uint8Array.from(new TextEncoder().encode(text));
const assetBytes = bytesOf('synthetic hangar asset');
const otherBytes = bytesOf('different hangar asset');

const grant = (overrides: Record<string, unknown> = {}) => ({
  path: 'assets/hangar/synthetic-a.bin', source: 'Project design pack', author: 'Hangar team',
  rightsBasis: 'Project-authored', rightsDeclaration: 'Approved project-authored asset declaration',
  checksum: computeHangarAssetChecksum(assetBytes), assetBytes, synthetic: true, type: 'model', ...overrides,
});
const cc0 = (overrides: Record<string, unknown> = {}) => grant({
  rightsBasis: 'CC0-1.0', rightsDeclaration: undefined, originalGrantUrl: 'https://example.org/cc0-grant', verificationReference: 'review-1', ...overrides,
});
const check = (record: unknown) => validateHangarAssetPolicyResult(record === null ? null : { assets: [record] });
const INVALID = { valid: false, reason: HANGAR_ASSET_INVALID };

describe('Hangar asset policy', () => {
  it('rejects missing, unsupported, or mismatched asset provenance', () => {
    // The digest is SHA-256 of the bytes themselves (FIPS 180-2 test vector).
    expect(computeHangarAssetChecksum(bytesOf('abc')))
      .toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');

    expect(check(grant())).toEqual({ valid: true });
    expect(check(cc0())).toEqual({ valid: true });
    for (const record of [
      null,
      grant({ source: undefined }), grant({ author: '' }), grant({ synthetic: undefined }), grant({ rightsDeclaration: '' }),
      cc0({ originalGrantUrl: undefined }), cc0({ verificationReference: '' }),
      grant({ rightsBasis: 'licensed' }),
      grant({ checksum: computeHangarAssetChecksum(otherBytes) }),
      grant({ assetBytes: undefined }),
      grant({ assetBytes: undefined, actualChecksum: computeHangarAssetChecksum(assetBytes) }),
      // A stale declared checksum with a matching caller-supplied digest, but different bytes.
      grant({ assetBytes: otherBytes, actualChecksum: computeHangarAssetChecksum(assetBytes) }),
    ]) {
      expect(check(record)).toEqual(INVALID);
    }
    expect(() => validateHangarAssetPolicy(null)).toThrow(HANGAR_ASSET_INVALID);
    // The record validator itself refuses a record without bytes, even when a caller-supplied digest matches.
    expect(() => validateHangarAssetRecord(grant({ assetBytes: undefined, actualChecksum: computeHangarAssetChecksum(assetBytes) })))
      .toThrow(HANGAR_ASSET_INVALID);
    expect(validateHangarAssetRecord(grant())).toBe(true);
  });

  it('rejects external resource URIs and embedded script content', () => {
    for (const resource of [
      { path: 'assets/hangar/model.glb', type: 'model' },
      { embedded: true, kind: 'image/png' },
      { embedded: true, kind: 'image/jpeg' },
    ]) {
      expect(validateHangarResourceResult(resource)).toEqual({ valid: true });
    }
    for (const resource of [
      'https://cdn.example/texture.png',
      { uri: 'assets/model.bin' },
      { path: 'assets/font.woff2', kind: 'font' },
      { embedded: true, kind: 'application/octet-stream' },
      { markup: '<script>alert(1)</script>' },
      { extensionsUsed: ['KHR_draco_mesh_compression'] },
      { path: 'assets/hangar/model.glb', url: 'https://cdn.example/model.glb' },
    ]) {
      expect(validateHangarResourceResult(resource)).toEqual({ valid: false, reason: 'resource_rejected' });
    }
  });

  it('hangar release asset declarations contain no GLB or texture records', () => {
    const dir = 'src/assets/hangar';
    const files = readdirSync(dir);
    expect(files.filter((file: string) => /\.(glb|gltf|ktx2|basis)$/i.test(file))).toEqual([]);
    const provenance = JSON.parse(readFileSync(`${dir}/hangar-provenance.json`, 'utf8'));
    expect(provenance.assets).toEqual([]);
    const manifest = JSON.parse(readFileSync('src/hangar/sceneManifest.json', 'utf8'));
    for (const model of manifest.aircraft.models) {
      expect(model.path).toBeUndefined(); expect(model.type).toBeUndefined();
      expect(model.synthetic).toBe(true); expect(model.realAircraftType).toBe(false); expect(model.realPerson).toBe(false); expect(model.brandMarks).toBe(false);
    }

    const invalidManifests = [cloneManifest(), cloneManifest(), cloneManifest()];
    invalidManifests[0].aircraft.models[0].path = 'assets/hangar/aircraft-a.glb';
    delete invalidManifests[1].aircraft.models[0].synthetic;
    invalidManifests[2].aircraft.models[0].realPerson = true;
    for (const invalidManifest of invalidManifests) {
      const loader = vi.fn(); let reason: 'asset_invalid' | null = null;
      const result = loadHangarAircraftAsset('aircraft-a', new Uint8Array([1]), {
        loader, onFallback: value => { reason = value; }, aggregateBytes: 1, manifest: invalidManifest,
      });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled();
      render(createElement(HangarAssetFallback, { reason }));
      expect(screen.getByRole('alert').textContent).toBe('Unable to display this hangar asset (asset_invalid).');
      cleanup();
    }
  });

  it('rejects real identity and missing synthetic rack or avatar markers', () => {
    const rack = (overrides: Record<string, unknown> = {}) => grant({ path: 'assets/hangar/rack.bin', rack: true, syntheticMarkerDeclaration: 'Synthetic rack; no brand', ...overrides });
    const avatar = (overrides: Record<string, unknown> = {}) => grant({
      path: 'assets/hangar/avatar.bin', avatar: true, syntheticMarkerDeclaration: 'Synthetic avatar; no real person',
      hardHatNode: 'HardHat', manifestHardHatNode: 'HardHat', ...overrides,
    });
    expect(check(rack())).toEqual({ valid: true });
    expect(check(avatar())).toEqual({ valid: true });
    for (const record of [
      rack({ realPerson: true }), rack({ realAircraftType: true }), rack({ brandMarks: true }),
      avatar({ realPerson: true }), avatar({ realAircraftType: true }), avatar({ brandMarks: true }),
      rack({ syntheticMarkerDeclaration: undefined }), avatar({ syntheticMarkerDeclaration: '' }),
      avatar({ hardHatNode: undefined }), avatar({ hardHatNode: 'OtherNode' }),
    ]) {
      expect(check(record)).toEqual(INVALID);
    }
  });

  it('enforces exact per-model, JSON, and aggregate asset budgets', () => {
    expect(HANGAR_MAX_TOTAL_BYTES).toBe(15_728_640);
    const within = { totalBytes: 1, largestModelBytes: 1, jsonBytes: 1 };
    for (const [budget, expected] of [
      [{ ...within, largestModelBytes: 5_242_880 }, { valid: true }],
      [{ ...within, largestModelBytes: 5_242_881 }, INVALID],
      [{ ...within, jsonBytes: 1_048_576 }, { valid: true }],
      [{ ...within, jsonBytes: 1_048_577 }, INVALID],
      [{ ...within, totalBytes: 15_728_640 }, { valid: true }],
      [{ ...within, totalBytes: 15_728_641 }, INVALID],
    ] as const) {
      expect(validateHangarAssetPolicyResult(budget)).toEqual(expected);
    }
    // A record's size is the length of its bytes; a declared size that disagrees is rejected.
    expect(check(grant({ bytes: assetBytes.length }))).toEqual({ valid: true });
    expect(check(grant({ bytes: assetBytes.length - 1 }))).toEqual(INVALID);
  });
});
