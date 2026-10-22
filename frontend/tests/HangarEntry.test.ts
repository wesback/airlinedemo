import { createElement, type ComponentType } from 'react';

const hangarModuleLoads = vi.hoisted(() => ({ threeFiber: 0 }));
vi.mock('@react-three/fiber', () => {
  hangarModuleLoads.threeFiber += 1;
  return { Canvas: () => null };
});
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';
import {
  HANGAR_3D_ACTIONS,
  HANGAR_NON_3D_ACTIONS,
  getHangarEntryDecision,
  getHangarFallbackReason,
  getHangarNon3dActions,
  selectHangarEntry,
  loadHangarScene,
} from '../src/hangar/entryController';

const HangarApp = App as ComponentType<HangarEntryAppProps>;

describe('HangarEntry', () => {
  it('HangarEntry rejects unsupported browser and missing WebGL', () => {
    expect(getHangarEntryDecision({ browserSupported: false, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
    expect(getHangarEntryDecision({ browserSupported: true, webgl2: null }).fallbackReason).toBe('webgl_unavailable');
    expect(getHangarEntryDecision({ browserSupported: false, webgl2: null })).toMatchObject({ mode: 'fallback', fallbackReason: 'unsupported_browser' });
    expect(getHangarEntryDecision({ browserSupported: true, webgl2: { webgl2: true }, webgl2Probe: null }).fallbackReason).toBe('webgl_unavailable');
  });

  it('HangarEntry contract eligibility cases', async () => {
    const loader = vi.fn();
    await expect(selectHangarEntry({ browserSupported: true, contractVersion: 1, webgl2: { webgl2: true } }, loader)).resolves.toMatchObject({ mode: '3d', loaderCalled: true });
    expect(loader).toHaveBeenCalledTimes(1);

    const unsupported = await selectHangarEntry({ browserSupported: false, contractVersion: 1, webgl2: { webgl2: true } }, loader);
    expect(unsupported).toMatchObject({ fallbackReason: 'unsupported_browser', mode: 'fallback' });

    const contractCases: Array<{ contractVersion: unknown; browserEligibility: unknown; probe: unknown }> = [
      { contractVersion: null, browserEligibility: true, probe: { webgl2: true } },
      { contractVersion: 2, browserEligibility: true, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: false, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: true, probe: null },
    ];
    for (const entry of contractCases) {
      const calls = vi.fn();
      const decision = await selectHangarEntry({ contractVersion: entry.contractVersion, browserSupported: entry.browserEligibility, webgl2: entry.probe as never }, calls);
      expect(decision.mode).toBe('fallback');
      expect(calls).not.toHaveBeenCalled();
    }
  });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

  it('HangarEntry lazy loads only after selection', async () => {
    const modelRequest = vi.fn();
    hangarModuleLoads.threeFiber = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (/\.(?:glb|gltf)(?:\?|$)/i.test(url)) modelRequest();
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/health') return json({ status: 'ok' });
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'token' });
      if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard' && method === 'GET') return json({ allowedDecisions: [], capabilities: [{ id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false, openItems: 0, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null }], queue: [], activity: [] });
      if (url === '/api/cases/case-001/technical-reviews' && method === 'GET') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness' && method === 'GET') return json({ monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2, sentence: 'Review is ready.' });
      throw new Error(`Unexpected request: ${method} ${url}`);
    }));

    function LoadedScene() { return null; }
    const loader = vi.fn(async () => LoadedScene);
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);

    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(modelRequest).not.toHaveBeenCalled();

    cleanup();
    const fallbackLoader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: fallbackLoader,
    } as HangarEntryAppProps));
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(fallbackLoader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);
  });

  it('HangarEntry default loader returns a renderable scene component', async () => {
    const Scene = await loadHangarScene();
    expect(typeof Scene).toBe('function');
  });

  it('HangarEntry non-3D parity', () => {
    expect(HANGAR_NON_3D_ACTIONS).toEqual(['selection', 'status', 'evidence', 'journey', 'review']);
    expect(HANGAR_3D_ACTIONS).toEqual(HANGAR_NON_3D_ACTIONS);
    expect(getHangarNon3dActions()).toEqual(HANGAR_3D_ACTIONS);
    expect(getHangarFallbackReason({ browserSupported: false, webgl2: { webgl2: true } })).toBe('unsupported_browser');
  });

  it('hangar UI rejects fallback entry before mounting a canvas', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/health') return json({ status: 'ok' });
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'token' });
      if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard' && method === 'GET') return json({ allowedDecisions: [], capabilities: [{ id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false, openItems: 0, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null }], queue: [], activity: [] });
      if (url === '/api/cases/case-001/technical-reviews' && method === 'GET') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness' && method === 'GET') return json({ monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2, sentence: 'Review is ready.' });
      throw new Error(`Unexpected request: ${method} ${url}`);
    }));

    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: vi.fn(),
    } as HangarEntryAppProps));
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));

    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    const names = ['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review'];
    for (const name of names) expect(screen.getByRole('button', { name })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Select case' }));
    expect(await screen.findByText('Case selected.')).toBeTruthy();
    for (const [name, message] of [
      ['View status', 'Case status selected.'],
      ['View evidence', 'Case evidence selected.'],
      ['Follow journey', 'Case journey selected.'],
      ['Open review', 'Technical review selected.'],
    ]) {
      fireEvent.click(screen.getByRole('button', { name }));
      expect(await screen.findByText(message)).toBeTruthy();
    }
    fireEvent.click(screen.getByRole('button', { name: 'Open review' }));
    expect(document.activeElement?.id).toBe('cap-technical-review');

    cleanup();
    const nullLoader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: null,
      hangarLazyLoader: nullLoader,
    } as HangarEntryAppProps));
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(nullLoader).not.toHaveBeenCalled();
  });

  it('supported versions load once and contract fallback renders no canvas with a non-3D link', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/health') return json({ status: 'ok' });
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'token' });
      if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard' && method === 'GET') return json({ allowedDecisions: [], capabilities: [{ id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false, openItems: 0, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null }], queue: [], activity: [] });
      if (url === '/api/cases/case-001/technical-reviews' && method === 'GET') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness' && method === 'GET') return json({ monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2, sentence: 'Review is ready.' });
      throw new Error(`Unexpected request: ${method} ${url}`);
    }));
    const loader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(document.querySelectorAll('canvas')).toHaveLength(1);

    cleanup();
    render(createElement(HangarApp, {
      hangarContractVersion: 2,
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    // A fresh mount verifies contract rejection with the same production entry path.
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText(/Unsupported hangar contract version 2/);
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(loader).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('link', { name: 'Use the supported contract version in the non-3D list.' }).getAttribute('href')).toBe('#non-3d-list');
  });

  it('supported versions pass eligibility and unsupported versions reject', () => {
    for (const version of [120, 121, 139, 140]) {
      expect(getHangarEntryDecision({ browser: { name: 'Chrome', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
      expect(getHangarEntryDecision({ browser: { name: 'Edge', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
    }
    expect(getHangarEntryDecision({ browser: { name: 'Firefox', version: 140 }, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
  });
});
