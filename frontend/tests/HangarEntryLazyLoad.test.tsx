// Regular mode loads no Hangar module. This file imports nothing from src/hangar itself, so the counting mocks below see
// exactly what App loads: a static import of the entry controller or of @react-three/fiber anywhere under App counts
// before the first test runs.
const hangarModuleLoads = vi.hoisted(() => ({ entryController: 0, threeFiber: 0 }));
vi.mock('../src/hangar/entryController', async (importOriginal) => {
  hangarModuleLoads.entryController += 1;
  return await importOriginal();
});
vi.mock('@react-three/fiber', () => {
  hangarModuleLoads.threeFiber += 1;
  return { Canvas: () => null };
});
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createElement, type ComponentType } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';

const HangarApp = App as ComponentType<HangarEntryAppProps>;
const loadsAtStartup = { ...hangarModuleLoads };

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function stubApi(modelRequest: () => void) {
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
}

async function signIn() {
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
  fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Revision: 7');
}

describe('HangarEntry lazy loading', () => {
  it('HangarEntry lazy loads only after selection', async () => {
    // Importing App loaded no Hangar module.
    expect(loadsAtStartup).toEqual({ entryController: 0, threeFiber: 0 });

    const modelRequest = vi.fn();
    stubApi(modelRequest);
    function LoadedScene() { return null; }
    const loader = vi.fn(async () => LoadedScene);
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: loader }));
    await signIn();

    // Regular mode, signed in, with the Hangar entry on screen: still no Hangar module, loader call or model request.
    expect(screen.getByRole('button', { name: 'Enter Hangar' })).toBeTruthy();
    expect(hangarModuleLoads).toEqual({ entryController: 0, threeFiber: 0 });
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();

    // Selecting the Hangar loads the entry controller once and calls the lazy loader once.
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(hangarModuleLoads.entryController).toBe(1);
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();

    // Fallback-only entry never calls the loader.
    cleanup();
    const fallbackLoader = vi.fn();
    render(createElement(HangarApp, { hangarBrowserSupported: false, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: fallbackLoader }));
    await signIn();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(fallbackLoader).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();
  });
});
