import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarLocalDiagnostics, { buildHangarLocalDiagnostics, HANGAR_LOCAL_FALLBACK_CODES } from '../src/hangar/localDiagnostics';

afterEach(cleanup);

describe('HangarLocalDiagnostics', () => {
  it('HangarLocalDiagnostics displays NFR-006 fields', () => {
    expect(HANGAR_LOCAL_FALLBACK_CODES).toEqual(['unsupported_browser', 'webgl_unavailable', 'asset_invalid', 'asset_load_failed', 'render_failed', 'refresh_failed']);
    render(<HangarLocalDiagnostics presenter role="presenter" sceneLoadDurationMs={125} webgl2Available browserSupported fallbackReason={null} />);
    fireEvent.click(screen.getByRole('button', { name: 'Open local diagnostics' }));
    const panel = screen.getByRole('region', { name: 'Local diagnostics panel' }) || screen.getByLabelText('Local diagnostics panel');
    const terms = Array.from(panel.querySelectorAll('dt')).map((node) => node.textContent);
    expect(terms).toEqual(['sceneLoadDurationMs', 'webgl2Available', 'browserSupported', 'fallbackReason']);
    expect(Array.from(panel.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['125', 'true', 'true', 'null']);

    for (const fallbackReason of HANGAR_LOCAL_FALLBACK_CODES) {
      const data = buildHangarLocalDiagnostics({ presenter: true, sceneLoadDurationMs: 1, webgl2Available: true, browserSupported: false, fallbackReason });
      expect(data?.fallbackReason).toBe(fallbackReason);
    }
    expect(buildHangarLocalDiagnostics({ presenter: true, sceneLoadDurationMs: 1, webgl2Available: true, browserSupported: true, fallbackReason: 'unknown' })).toBeNull();
    expect(buildHangarLocalDiagnostics({ presenter: false, sceneLoadDurationMs: 1, webgl2Available: true, browserSupported: true, fallbackReason: null })).toBeNull();
  });

  it('HangarLocalDiagnostics sends zero transport calls', () => {
    const transport = vi.fn();
    const opened = render(<HangarLocalDiagnostics presenter clockStart={100} clockEnd={142} webgl2Available browserSupported fallbackReason={null} transport={transport} outcome="success" />);
    expect(transport).not.toHaveBeenCalled();
    opened.unmount();
    for (const outcome of ['unsupported_browser', 'webgl_unavailable', 'asset_invalid', 'asset_load_failed', 'render_failed', 'refresh_failed']) {
      const transportCalls = vi.fn();
      render(<HangarLocalDiagnostics presenter={true} open={true} clockStart={100} clockEnd={142} webgl2Available={false} browserSupported={false} fallbackReason={outcome} transport={transportCalls} outcome={outcome} />);
      expect(transportCalls).not.toHaveBeenCalled();
      cleanup();
    }
    expect(transport).not.toHaveBeenCalled();
  });
});
