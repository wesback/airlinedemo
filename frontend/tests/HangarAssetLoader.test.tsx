import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarAssetFallback from '../src/hangar/HangarAssetFallback';
import { loadValidatedHangarAsset } from '../src/hangar/hangarAssetLoader';
import { buildGlb } from './hangarGlbBuilder';

afterEach(cleanup);

describe('Hangar asset load boundary', () => {
  it('GLB_load_gate_orders_validation_loader_and_fallback', () => {
    const order: string[] = [];
    const loader = vi.fn(() => { order.push('loader'); return 'scene'; });
    const onFallback = vi.fn(() => { order.push('fallback'); });

    const valid = loadValidatedHangarAsset(buildGlb(), { loader, onFallback });
    expect(valid).toEqual({ loaded: true, value: 'scene' });
    expect(loader).toHaveBeenCalledTimes(1);
    expect(onFallback).toHaveBeenCalledTimes(0);

    loader.mockClear();
    const malformed = buildGlb().slice(0, -1);
    const invalid = loadValidatedHangarAsset(malformed, { loader, onFallback });
    expect(invalid).toEqual({ loaded: false, reason: 'asset_invalid' });
    expect(loader).toHaveBeenCalledTimes(0);
    expect(onFallback).toHaveBeenCalledTimes(1);
    expect(onFallback).toHaveBeenCalledWith('asset_invalid');
    expect(order).toEqual(['loader', 'fallback']);
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
