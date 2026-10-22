import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarAssetFallback from '../src/hangar/HangarAssetFallback';
import { validateHangarGlbResult } from '../src/hangar/glbValidator';
import { loadValidatedHangarAsset, type HangarAssetLoadOptions } from '../src/hangar/hangarAssetLoader';
import { assembleGlb, binChunk, buildGlb, jsonChunk, validDocument } from './hangarGlbBuilder';
import { computeHangarAssetChecksum } from '../src/hangar/assetPolicy';

afterEach(cleanup);

const provenanceFor = (bytes: Uint8Array) => ({
  path: 'assets/hangar/model.glb', source: 'Project design pack', author: 'Hangar team',
  rightsBasis: 'Project-authored', rightsDeclaration: 'Approved declaration', checksum: computeHangarAssetChecksum(bytes), synthetic: true,
  type: 'model',
});

/** A valid GLB of exactly `total` bytes: the BIN chunk takes up the rest. */
function glbOfSize(total: number): Uint8Array {
  const json = (binLength: number) => jsonChunk(validDocument({ buffers: [{ byteLength: binLength }] }));
  const binLength = total - 28 - json(1_000_000).data.length;
  const glb = assembleGlb([json(binLength), binChunk(binLength)]);
  expect(glb.length).toBe(total);
  return glb;
}

/** A valid GLB whose JSON chunk is exactly `length` bytes (a multiple of 4). */
function glbWithJsonChunk(length: number): Uint8Array {
  const base = JSON.stringify(validDocument({ extras: { pad: '' } })).length;
  const chunk = jsonChunk(validDocument({ extras: { pad: 'x'.repeat(length - base) } }));
  expect(chunk.data.length).toBe(length);
  return assembleGlb([chunk, binChunk()]);
}

describe('Hangar asset load boundary', () => {
  it('GLB_load_gate_orders_validation_loader_and_fallback', () => {
    const order: string[] = [];
    const loader = vi.fn(() => { order.push('loader'); return 'scene'; });
    const onFallback = vi.fn(() => { order.push('fallback'); });

    const bytes = buildGlb();
    const loaderOptions = {
      loader,
      onFallback,
      resource: { path: 'assets/hangar/model.glb', type: 'model' },
      provenance: {
        path: 'assets/hangar/model.glb', source: 'Project design pack', author: 'Hangar team',
        rightsBasis: 'Project-authored', rightsDeclaration: 'Approved declaration', checksum: computeHangarAssetChecksum(bytes), synthetic: true,
        type: 'model', bytes: bytes.length,
      },
      aggregateBytes: bytes.length,
    };
    const valid = loadValidatedHangarAsset(bytes, loaderOptions);
    expect(valid).toEqual({ loaded: true, value: 'scene' });
    expect(loader).toHaveBeenCalledTimes(1);
    expect(onFallback).toHaveBeenCalledTimes(0);

    loader.mockClear();
    const malformed = buildGlb().slice(0, -1);
    const invalid = loadValidatedHangarAsset(malformed, loaderOptions);
    expect(invalid).toEqual({ loaded: false, reason: 'asset_invalid' });
    expect(loader).toHaveBeenCalledTimes(0);
    expect(onFallback).toHaveBeenCalledTimes(1);
    expect(onFallback).toHaveBeenCalledWith('asset_invalid');
    expect(order).toEqual(['loader', 'fallback']);
  });

  it('rejects missing provenance, external resources, mismatched checksums, and oversize models before loader use', () => {
    const bytes = buildGlb();
    const sameOrigin = { path: 'assets/hangar/model.glb', type: 'model' };
    const admit = (input: Uint8Array, overrides: Record<string, unknown> = {}) => {
      const loader = vi.fn(() => 'scene');
      const onFallback = vi.fn();
      const result = loadValidatedHangarAsset(input, {
        loader, onFallback, resource: sameOrigin, provenance: provenanceFor(input), aggregateBytes: input.length, ...overrides,
      } as HangarAssetLoadOptions<string>);
      return { result, loader, onFallback };
    };
    const accepted = (run: ReturnType<typeof admit>) => {
      expect(run.result).toEqual({ loaded: true, value: 'scene' });
      expect(run.loader).toHaveBeenCalledTimes(1);
      expect(run.onFallback).not.toHaveBeenCalled();
    };
    const rejected = (run: ReturnType<typeof admit>) => {
      expect(run.result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(run.loader).not.toHaveBeenCalled();
      expect(run.onFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    };

    accepted(admit(bytes));
    rejected(admit(bytes, { provenance: { ...provenanceFor(bytes), checksum: 'b'.repeat(64) } }));
    // A stale checksum with a matching caller-supplied digest: the bytes decide, not the metadata.
    const other = buildGlb(validDocument({ extras: { variant: 2 } }));
    rejected(admit(other, { provenance: { ...provenanceFor(bytes), actualChecksum: provenanceFor(bytes).checksum } }));
    rejected(admit(bytes, { resource: { url: 'https://cdn.example/model.glb', kind: 'model' } }));
    rejected(admit(bytes, { provenance: undefined }));
    rejected(admit(bytes, { provenance: { ...provenanceFor(bytes), rightsBasis: 'licensed' } }));

    accepted(admit(bytes, { aggregateBytes: 15_728_640 }));
    rejected(admit(bytes, { aggregateBytes: 15_728_641 }));

    const atModelLimit = glbOfSize(5_242_880);
    accepted(admit(atModelLimit));
    // 5,242,884 is the smallest well-formed GLB above the cap (chunks are 4-byte aligned); it is valid under a raised cap.
    const overModelLimit = glbOfSize(5_242_884);
    expect(validateHangarGlbResult(overModelLimit, { maxTotalBytes: 5_242_884 })).toEqual({ valid: true });
    rejected(admit(overModelLimit, { aggregateBytes: 5_242_884 }));
    rejected(admit(overModelLimit, { aggregateBytes: 5_242_884, limits: { maxTotalBytes: 6_000_000 } }));

    accepted(admit(glbWithJsonChunk(1_048_576)));
    const overJsonLimit = glbWithJsonChunk(1_048_580);
    expect(validateHangarGlbResult(overJsonLimit, { maxJsonBytes: 1_048_580 })).toEqual({ valid: true });
    rejected(admit(overJsonLimit));
  });

  it('GLB_invalid_asset_renders_visible_fallback', () => {
    const { rerender } = render(<HangarAssetFallback reason={null} />);
    expect(screen.queryByRole('alert')).toBeNull();

    rerender(<HangarAssetFallback reason="asset_invalid" />);
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toBe('Unable to display this hangar asset (asset_invalid).');
    expect(alert.innerHTML).toBe('Unable to display this hangar asset (asset_invalid).');
  });
});
