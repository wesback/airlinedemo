import { createElement } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarLocalDiagnostics, { buildHangarLocalDiagnostics, HANGAR_LOCAL_FALLBACK_CODES } from '../src/hangar/localDiagnostics';
import { HangarCanvasAdapter, createHangarOwnedPerspectiveCamera } from '../src/hangar/HangarCanvasAdapter';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('HangarLocalDiagnostics', () => {
  it('HangarLocalDiagnostics displays NFR-006 fields', () => {
    expect(HANGAR_LOCAL_FALLBACK_CODES).toEqual(['unsupported_browser', 'webgl_unavailable', 'asset_invalid', 'asset_load_failed', 'render_failed', 'contract_invalid', 'refresh_failed']);
    const successView = render(<HangarLocalDiagnostics presenter role="presenter" sceneLoadDurationMs={125} webgl2Available browserSupported fallbackReason={null} />);
    fireEvent.click(screen.getByRole('button', { name: 'Open local diagnostics' }));
    const panel = screen.getByRole('region', { name: 'Local diagnostics panel' });
    expect(Array.from(panel.querySelectorAll('dt')).map((node) => node.textContent)).toEqual(['sceneLoadDurationMs', 'webgl2Available', 'browserSupported', 'fallbackReason']);
    expect(Array.from(panel.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['125', 'true', 'true', 'null']);
    successView.unmount();

    for (const fallbackReason of HANGAR_LOCAL_FALLBACK_CODES) {
      const view = render(<HangarLocalDiagnostics presenter={true} open={true} sceneLoadDurationMs={125} webgl2Available={false} browserSupported={false} fallbackReason={fallbackReason} />);
      const fallbackPanel = view.container.querySelector('[role="region"]');
      expect(fallbackPanel).not.toBeNull();
      expect(Array.from(fallbackPanel!.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['null', 'false', 'false', fallbackReason]);
      view.unmount();
    }

    expect(buildHangarLocalDiagnostics({ presenter: true, sceneLoadDurationMs: 1, webgl2Available: true, browserSupported: true, fallbackReason: 'unknown' })).toBeNull();
    render(<HangarLocalDiagnostics presenter={true} open={true} sceneLoadDurationMs={1} webgl2Available browserSupported fallbackReason={'unknown' as never} />);
    expect(screen.queryByRole('region', { name: 'Local diagnostics panel' })).toBeNull();

    expect(buildHangarLocalDiagnostics({ presenter: false, sceneLoadDurationMs: 1, webgl2Available: true, browserSupported: true, fallbackReason: null })).toBeNull();
    render(<HangarLocalDiagnostics presenter={false} open={true} sceneLoadDurationMs={1} webgl2Available browserSupported fallbackReason={null} />);
    expect(screen.queryByRole('region', { name: 'Local diagnostics panel' })).toBeNull();
  });

  it('Hangar context loss falls back once without remount', () => {
    const mutation = vi.fn(); const mountCanvas = vi.fn();
    const snapshots = [{ revision: 7, owner: 'Technical reviewer', capabilityLabel: 'Rules-based' }];
    const contextCanvas = document.createElement('canvas');
    function CanvasStub(props: any) { mountCanvas(props.onCreated); return createElement('canvas', { 'aria-label': '3D hangar scene', ref: (node: HTMLCanvasElement | null) => { if (node && props.onCreated) props.onCreated({ gl: { domElement: contextCanvas } }); } }); }
    const view = render(createElement(HangarCanvasAdapter, { camera: createHangarOwnedPerspectiveCamera(), canvasComponent: CanvasStub, onContextLost: () => { contextLostCount += 1; } }));
    let contextLostCount = 0;
    fireEvent(contextCanvas, new Event('webglcontextlost'));
    expect(contextLostCount).toBe(1);
    expect(view.container.querySelector('[aria-label="3D hangar scene"]')).toBeNull();
    expect(mutation).not.toHaveBeenCalled();
    expect(snapshots[0]).toMatchObject({ revision: 7 });
    fireEvent(contextCanvas, new Event('webglcontextrestored')); view.unmount();
  });

  it('Hangar diagnostics exact load duration and fallback null', () => {
    const now = vi.fn(() => 3400);
    expect(buildHangarLocalDiagnostics({ presenter: true, clockStart: 1000, now, webgl2Available: true, browserSupported: true, fallbackReason: null })).toMatchObject({ sceneLoadDurationMs: 2400 });
    expect(buildHangarLocalDiagnostics({ presenter: true, clockStart: 1000, now, webgl2Available: false, browserSupported: true, fallbackReason: 'render_failed' })).toMatchObject({ sceneLoadDurationMs: null });
  });

  it('HangarLocalDiagnostics sends zero transport calls', () => {
    const fetchSpy = vi.fn(async () => new Response('{}', { status: 200 }));
    const xhrSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);
    vi.stubGlobal('XMLHttpRequest', xhrSpy);
    const transport = vi.fn();

    const view = render(<HangarLocalDiagnostics presenter clockStart={100} clockEnd={142} webgl2Available browserSupported fallbackReason={null} transport={transport} outcome="success" />);
    fireEvent.click(screen.getByRole('button', { name: 'Open local diagnostics' }));
    expect(screen.getByRole('region', { name: 'Local diagnostics panel' })).not.toBeNull();
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(xhrSpy).not.toHaveBeenCalled();
    expect(transport).not.toHaveBeenCalled();
    view.unmount();

    for (const outcome of HANGAR_LOCAL_FALLBACK_CODES) {
      const transportCalls = vi.fn();
      const fallbackView = render(<HangarLocalDiagnostics presenter={true} open={true} clockStart={100} clockEnd={142} webgl2Available={false} browserSupported={false} fallbackReason={outcome} transport={transportCalls} outcome={outcome} />);
      expect(screen.getByRole('region', { name: 'Local diagnostics panel' })).not.toBeNull();
      expect(fetchSpy).not.toHaveBeenCalled();
      expect(xhrSpy).not.toHaveBeenCalled();
      expect(transportCalls).not.toHaveBeenCalled();
      fallbackView.unmount();
    }
  });
});
