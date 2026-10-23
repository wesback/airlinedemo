import { createElement, useEffect, type ComponentType } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';
import HangarLocalDiagnostics, { buildHangarLocalDiagnostics, HANGAR_LOCAL_FALLBACK_CODES } from '../src/hangar/localDiagnostics';
import { HangarCanvasAdapter, createHangarOwnedPerspectiveCamera } from '../src/hangar/HangarCanvasAdapter';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';

const HangarApp = App as ComponentType<HangarEntryAppProps>;

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
    const contextCanvas = document.createElement('canvas');
    const mountCanvas = vi.fn();
    function ProductionScene(props: { onCreated?: (state: unknown) => unknown }) {
      useEffect(() => { mountCanvas(); props.onCreated?.({ gl: { domElement: contextCanvas } }); }, []);
      return createElement('canvas', { 'aria-label': '3D hangar scene' });
    }
    const loader = vi.fn(() => ProductionScene);
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: loader }));
    await signInAsReviewer();
    mutationCalls.length = 0;
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(mountCanvas).toHaveBeenCalledTimes(1));

    fireEvent(contextCanvas, new Event('webglcontextlost'));
    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
    expect(screen.queryByLabelText('Hangar 3D scene')).toBeNull();
    expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 7; Labels: Technical reviewer; Rules-based');
    expect(mutationCalls).toEqual([]);
    expect(modelRequests).not.toHaveBeenCalled();

    fireEvent(contextCanvas, new Event('webglcontextrestored'));
    expect(mountCanvas).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText('Hangar 3D scene')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(mountCanvas).toHaveBeenCalledTimes(2));
    expect(mutationCalls).toEqual([]);
    expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 7; Labels: Technical reviewer; Rules-based');
  });

  it('Hangar diagnostics exact load duration and fallback null', async () => {
    stubCaseApi();
    const clock = vi.fn()
      .mockReturnValueOnce(1000)
      .mockReturnValueOnce(3400);
    function ProductionScene(props: { onCreated?: (state: unknown) => unknown }) {
      useEffect(() => { props.onCreated?.({ gl: { domElement: document.createElement('canvas') } }); }, []);
      return createElement('canvas', { 'aria-label': '3D hangar scene' });
    }
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => ProductionScene, hangarMonotonicClock: clock }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByLabelText('Hangar 3D scene');
    expect(screen.getByLabelText('Open local diagnostics')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Open local diagnostics'));
    const panel = screen.getByRole('region', { name: 'Local diagnostics panel' });
    expect(Array.from(panel.querySelectorAll('dt')).map((node) => node.textContent)).toEqual(['sceneLoadDurationMs', 'webgl2Available', 'browserSupported', 'fallbackReason']);
    expect(Array.from(panel.querySelectorAll('dd')).map((node) => node.textContent)).toEqual(['2400', 'true', 'true', 'null']);

    expect(buildHangarLocalDiagnostics({ presenter: true, clockStart: 1000, now: vi.fn(() => 3400), webgl2Available: false, browserSupported: true, fallbackReason: 'render_failed' })).toMatchObject({ sceneLoadDurationMs: null });
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
