import { createElement, useEffect, type ComponentType } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarLocalDiagnostics, { buildHangarLocalDiagnostics, HANGAR_LOCAL_FALLBACK_CODES } from '../src/hangar/localDiagnostics';
import { HangarCanvasAdapter, createHangarOwnedPerspectiveCamera } from '../src/hangar/HangarCanvasAdapter';
import { renderHangarMode, stubCaseApi } from './hangarAppHarness';
import { loadHangarScene } from '../src/hangar/entryController';

const productionCanvasHarness = vi.hoisted(() => ({ mounts: vi.fn(), canvases: [] as HTMLCanvasElement[] }));
vi.mock('@react-three/fiber', async () => {
  const React = await import('react');
  return {
    Canvas: (props: any) => React.createElement(React.Fragment, null,
      React.createElement('canvas', { 'aria-label': '3D hangar scene' }),
      React.createElement(ProductionCanvasCreated, props)),
    useThree: (selector: (state: { invalidate: () => void }) => unknown) => selector({ invalidate: vi.fn() }),
    useFrame: () => undefined,
  };
  function ProductionCanvasCreated(props: any) {
    React.useEffect(() => {
      productionCanvasHarness.mounts();
      const element = document.createElement('canvas');
      productionCanvasHarness.canvases.push(element);
      props.onCreated?.({ gl: { domElement: element } });
    }, []);
    return null;
  }
});


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

  it('Hangar context loss falls back once without remount', async () => {
    const modelRequests = vi.fn();
    stubCaseApi(modelRequests);
    const mutationCalls: string[] = [];
    const baseFetch = vi.mocked(fetch);
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (method !== 'GET') mutationCalls.push(`${method} ${url}`);
      return baseFetch(input, init);
    }));
    productionCanvasHarness.mounts.mockClear();
    productionCanvasHarness.canvases.length = 0;
    const mountCanvas = productionCanvasHarness.mounts;
    const loader = vi.fn(async () => loadHangarScene());
    renderHangarMode({ hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: loader });
    mutationCalls.length = 0;
    await waitFor(() => expect(mountCanvas).toHaveBeenCalledTimes(1));
    const contextCanvas = productionCanvasHarness.canvases[0];
    expect(contextCanvas).toBeInstanceOf(HTMLCanvasElement);

    fireEvent(contextCanvas, new Event('webglcontextlost'));
    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
    expect(screen.queryByLabelText('Hangar 3D scene')).toBeNull();
    expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 7; Labels: Technical reviewer; Rules-based');
    fireEvent.click(screen.getByLabelText('Open local diagnostics'));
    expect(Array.from(screen.getByRole('region', { name: 'Local diagnostics panel' }).querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['null', 'true', 'true', 'render_failed']);
    expect(mutationCalls).toEqual([]);
    expect(modelRequests).not.toHaveBeenCalled();

    fireEvent(contextCanvas, new Event('webglcontextrestored'));
    expect(mountCanvas).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText('Hangar 3D scene')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Reload hangar' }));
    await waitFor(() => expect(mountCanvas).toHaveBeenCalledTimes(2));
    expect(productionCanvasHarness.canvases).toHaveLength(2);
    expect(productionCanvasHarness.canvases[1]).not.toBe(productionCanvasHarness.canvases[0]);
    expect(mutationCalls).toEqual([]);
    expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 7; Labels: Technical reviewer; Rules-based');
  });

  it('Hangar diagnostics exact load duration and fallback null', async () => {
    stubCaseApi();
    const clock = vi.fn()
      .mockReturnValueOnce(1000)
      .mockReturnValueOnce(3400);
    productionCanvasHarness.canvases.length = 0;
    productionCanvasHarness.mounts.mockClear();
    const successView = renderHangarMode({ hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => loadHangarScene(), hangarMonotonicClock: clock });
    await screen.findByLabelText('Hangar 3D scene');
    expect(screen.getByLabelText('Open local diagnostics')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Open local diagnostics'));
    const panel = screen.getByRole('region', { name: 'Local diagnostics panel' });
    expect(Array.from(panel.querySelectorAll('dt')).map((node) => node.textContent)).toEqual(['sceneLoadDurationMs', 'webgl2Available', 'browserSupported', 'fallbackReason']);
    expect(Array.from(panel.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['2400', 'true', 'true', 'null']);

    expect(buildHangarLocalDiagnostics({ presenter: true, clockStart: 1000, now: vi.fn(() => 3400), webgl2Available: false, browserSupported: true, fallbackReason: 'render_failed' })).toEqual({ sceneLoadDurationMs: null, webgl2Available: false, browserSupported: true, fallbackReason: 'render_failed' });

    successView.unmount();
    const fallbackClock = vi.fn(() => 1000);
    const fallbackView = renderHangarMode({ hangarBrowserSupported: false, hangarWebGl2Probe: { webgl2: false }, hangarLazyLoader: () => loadHangarScene(), hangarMonotonicClock: fallbackClock });
    await screen.findByRole('alert');
    fireEvent.click(screen.getByLabelText('Open local diagnostics'));
    const fallbackPanel = screen.getByRole('region', { name: 'Local diagnostics panel' });
    expect(Array.from(fallbackPanel.querySelectorAll('dt')).map((node) => node.textContent)).toEqual(['sceneLoadDurationMs', 'webgl2Available', 'browserSupported', 'fallbackReason']);
    expect(Array.from(fallbackPanel.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['null', 'false', 'false', 'unsupported_browser']);
    fallbackView.unmount();
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
