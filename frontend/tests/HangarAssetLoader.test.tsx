import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarAssetFallback from '../src/hangar/HangarAssetFallback';
import { loadValidatedHangarAsset } from '../src/hangar/hangarAssetLoader';
import { buildGlb } from './hangarGlbBuilder';
import { computeHangarAssetChecksum } from '../src/hangar/assetPolicy';

afterEach(cleanup);

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
    const loader = vi.fn();
    const onFallback = vi.fn();
    const provenance = {
      path: 'assets/hangar/model.glb', source: 'Project design pack', author: 'Hangar team',
      rightsBasis: 'Project-authored', rightsDeclaration: 'Approved declaration', checksum: computeHangarAssetChecksum(bytes), synthetic: true,
      type: 'model', bytes: bytes.length,
    };
    for (const options of [
      { resource: { url: 'https://cdn.example/model.glb', kind: 'model' }, provenance },
      { resource: { path: 'assets/hangar/model.glb', type: 'model' }, provenance: undefined },
      { resource: { path: 'assets/hangar/model.glb', type: 'model' }, provenance: { ...provenance, checksum: 'b'.repeat(64) } },
    ]) {
      loader.mockClear();
      onFallback.mockClear();
      const result = loadValidatedHangarAsset(bytes, { loader, onFallback, ...options } as any);
      expect(result).toEqual({ loaded: false, reason: 'asset_invalid' });
      expect(loader).not.toHaveBeenCalled();
      expect(onFallback).toHaveBeenCalledExactlyOnceWith('asset_invalid');
    }
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
