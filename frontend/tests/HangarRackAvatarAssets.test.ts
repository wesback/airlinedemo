import { describe, expect, it, vi } from 'vitest';
import provenanceManifest from '../src/assets/hangar/hangar-provenance.json';
import { computeHangarAssetChecksum } from '../src/hangar/assetPolicy';
import { loadRackAvatarHangarAsset } from '../src/hangar/dataRoomAdapter';
import { buildGlb } from './hangarGlbBuilder';

const rackBytes = buildGlb();
const avatarBytes = buildGlb();
const records = provenanceManifest.assets as unknown as Array<Record<string, unknown>>;
const rackRecord = { ...records.find(record => record.path === 'assets/hangar/rack.glb')!, checksum: computeHangarAssetChecksum(rackBytes), sizeBytes: rackBytes.length };
const avatarRecord = { ...records.find(record => record.path === 'assets/hangar/avatar.glb')!, checksum: computeHangarAssetChecksum(avatarBytes), sizeBytes: avatarBytes.length };

describe('Hangar rack and avatar assets', () => {
  it('Hangar rack and avatar assets enforce shared validation', () => {
    for (const [asset, bytes, record] of [
      ['rack', rackBytes, rackRecord], ['avatar', avatarBytes, avatarRecord],
    ] as const) {
      const loader = vi.fn(() => `${asset}-scene`);
      const onFallback = vi.fn();
      const result = loadRackAvatarHangarAsset(asset, bytes, {
        loader, onFallback, provenance: record, aggregateBytes: bytes.length,
      });
      expect(result).toEqual({ loaded: true, value: `${asset}-scene` });
      expect(loader).toHaveBeenCalledExactlyOnceWith(bytes);
      expect(onFallback).not.toHaveBeenCalled();
    }

    const rejected: Array<unknown> = [
      rackBytes.slice(0, -1),
      avatarBytes.slice(0, 12),
      new Uint8Array(5_242_881),
    ];
    for (const bad of rejected) {
      const loader = vi.fn(); const onFallback = vi.fn();
      const result = loadRackAvatarHangarAsset('rack', bad, { loader, onFallback, provenance: rackRecord, aggregateBytes: bad instanceof Uint8Array ? bad.length : 0 });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled(); expect(onFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }

    const malformedProvenance = [
      { ...rackRecord, checksum: '0'.repeat(64) },
      { ...rackRecord, synthetic: false },
      { ...rackRecord, syntheticMarkerDeclaration: undefined },
      { ...rackRecord, rightsBasis: 'licensed' },
      { ...rackRecord, rightsDeclaration: '' },
      { ...rackRecord, brandMarks: true },
      { ...avatarRecord, realPerson: true },
      { ...avatarRecord, brandMarks: true },
      { ...avatarRecord, hardHatNode: 'OtherHat' },
    ];
    for (const record of malformedProvenance) {
      const recordPath = String((record as Record<string, unknown>).path ?? 'assets/hangar/rack.glb');
      const bytes = recordPath === 'assets/hangar/avatar.glb' ? avatarBytes : rackBytes;
      const loader = vi.fn(); const onFallback = vi.fn();
      const result = loadRackAvatarHangarAsset(recordPath === 'assets/hangar/avatar.glb' ? 'avatar' : 'rack', bytes, { loader, onFallback, provenance: record, aggregateBytes: bytes.length });
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled(); expect(onFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }

    const loader = vi.fn(); const onFallback = vi.fn();
    loadRackAvatarHangarAsset('avatar', avatarBytes, {
      loader, onFallback, provenance: avatarRecord,
      resource: { url: 'https://cdn.example/avatar.glb' }, aggregateBytes: avatarBytes.length,
    });
    expect(loader).not.toHaveBeenCalled(); expect(onFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');

    expect(computeHangarAssetChecksum(rackBytes)).toBe(rackRecord.checksum);
    expect(computeHangarAssetChecksum(avatarBytes)).toBe(avatarRecord.checksum);
    expect(buildGlb()).toBeInstanceOf(Uint8Array);
  });
});
