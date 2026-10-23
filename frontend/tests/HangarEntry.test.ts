import { createElement, type ComponentType } from 'react';

vi.mock('@react-three/fiber', () => ({ Canvas: () => null }));
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';
import { confirmHangarRackMenuEntry } from '../src/hangar/rackMenu';
import userEvent from '@testing-library/user-event';
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
  it('Hangar App composes walk region and follow camera', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return createElement('canvas', { 'aria-label': '3D hangar scene' }); } return Scene; },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    const walk = screen.getByRole('region', { name: 'Walk region' });
    expect((walk as HTMLElement).tabIndex).toBe(0);
    const avatarBefore = screen.getByText(/^Avatar: /).textContent;
    const cameraBefore = screen.getByText(/^Camera pose$/).textContent;
    walk.focus();
    fireEvent.keyDown(walk, { key: 'ArrowUp' });
    expect(screen.getByText(/^Avatar: /).textContent).not.toBe(avatarBefore);
    const follow = screen.getByRole('button', { name: 'Follow avatar: on' });
    fireEvent.click(follow);
    expect(screen.getByRole('button', { name: 'Follow avatar: off' })).toBeTruthy();
    const avatarOff = screen.getByText(/^Avatar: /).textContent;
    fireEvent.keyDown(walk, { key: 'w' });
    expect(screen.getByText(/^Avatar: /).textContent).toBe(avatarOff);
    expect(screen.getByText(/^Camera pose$/).textContent).toBeDefined();
    expect(cameraBefore).not.toBeNull();
    expect(selection).not.toHaveBeenCalled();
  });

  it('HangarEntry rejects unsupported browser and missing WebGL', () => {
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: false, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: true, webgl2: null }).fallbackReason).toBe('webgl_unavailable');
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: false, webgl2: null })).toMatchObject({ mode: 'fallback', fallbackReason: 'unsupported_browser' });
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: true, webgl2: { webgl2: true }, webgl2Probe: null }).fallbackReason).toBe('webgl_unavailable');
  });

  it('HangarEntry contract eligibility cases', async () => {
    const loader = vi.fn();
    await expect(selectHangarEntry({ browserSupported: true, contractVersion: 1, webgl2: { webgl2: true } }, loader)).resolves.toMatchObject({ mode: '3d', loaderCalled: true });
    expect(loader).toHaveBeenCalledTimes(1);

    const unsupported = await selectHangarEntry({ browserSupported: false, contractVersion: 1, webgl2: { webgl2: true } }, loader);
    expect(unsupported).toMatchObject({ fallbackReason: 'unsupported_browser', mode: 'fallback' });

    const contractCases: Array<{ contractVersion: unknown; browserEligibility: unknown; probe: unknown }> = [
      { contractVersion: undefined, browserEligibility: true, probe: { webgl2: true } },
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

  it('Hangar App composes semantic controls evidence journey and presets', async () => {
    stubCaseApi();
    const evidenceOriginal = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/v1/cases/cp-case-a/scene' && (init?.method ?? 'GET') === 'GET') {
        const envelope = await evidenceOriginal(input, init).then((response) => response.json());
        envelope.objects[0].evidenceIds = ['EV-A-001'];
        envelope.documentRefs = [{ evidenceRefId: 'EV-A-001', caseId: 'cp-case-a', documentId: 'DOC-A-001', documentVersion: 3, basisId: 'BASIS-A', basisVersion: 2, displayLabel: 'Airframe evidence', processingState: 'accepted', usableAsEvidence: true }];
        envelope.evidence = [{ evidenceRefId: 'EV-A-001', caseId: 'cp-case-a', documentId: 'DOC-A-001', documentVersion: 3, basisId: 'BASIS-A', basisVersion: 2, witnessId: 'WIT-A-001', page: 4, displayLabel: 'Airframe witness' }];
        return new Response(JSON.stringify(envelope), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return evidenceOriginal(input, init);
    });
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return createElement("canvas", { "aria-label": "3D hangar scene" }); } return Scene; },
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });

    expect(screen.getByLabelText('Hangar scene objects').querySelectorAll('button')).toHaveLength(12);
    for (const name of ['Air One', 'Air Two', 'Air Three']) expect(screen.getByRole('button', { name: `View ${name}` })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Overview' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Move avatar' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Aircraft' })).toBeNull();

    const objectButtons = screen.getByLabelText('Hangar scene objects').querySelectorAll('button');
    fireEvent.click(objectButtons[0]);
    expect(screen.getByRole('region', { name: 'Selection SYN-CP-A-AIR-001' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'SYN-CP-A-AIR-001' })).toBeTruthy();
    expect(screen.getByText('DOC-A-001 v3 / BASIS-A v2')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Page witness 4' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Case A guided journey' })).toBeTruthy();
  });

  it('Hangar App compose reset and error fallback', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    stubCaseApi();
    const original = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/v1/cases/cp-case-c/scene' && (init?.method ?? 'GET') === 'GET') {
        const envelope = await original(input, init).then((response) => response.json());
        envelope.case.permission = true;
        envelope.canResetBlockedVariant = true;
        envelope.allowedActions = ['reset'];
        return new Response(JSON.stringify(envelope), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return original(input, init);
    });
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return createElement("canvas", { "aria-label": "3D hangar scene" }); } return Scene; },
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'View Air Three' });
    fireEvent.click(screen.getByRole('button', { name: 'View Air Three' }));
    expect(screen.getAllByRole('button', { name: 'Reset blocked variant' }).length).toBeGreaterThanOrEqual(1);

    for (const status of [400, 403, 404, 409, 422]) {
      const before = vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('reset-blocked-variant')).length;
      const latest = vi.mocked(fetch).getMockImplementation()!;
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === '/api/v1/cases/cp-case-c/reset-blocked-variant') return new Response(JSON.stringify({ error: 'rejected' }), { status, headers: { 'Content-Type': 'application/json' } });
        return latest(input, init);
      });
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      fireEvent.click(screen.getAllByRole('button', { name: 'Reset blocked variant' }).at(-1)!);
      await screen.findByText(`Reset rejected (${status}).`);
      expect(vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('reset-blocked-variant')).length).toBe(before + 1);
      cleanup();
      stubCaseApi();
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === '/api/v1/cases/cp-case-c/scene') {
          const envelope = await original(input, init).then((response) => response.json());
          envelope.case.permission = true; envelope.canResetBlockedVariant = true; envelope.allowedActions = ['reset'];
          return new Response(JSON.stringify(envelope), { status: 200, headers: { 'Content-Type': 'application/json' } });
        }
        if (url === '/api/v1/cases/cp-case-c/reset-blocked-variant') return new Response(JSON.stringify({ error: 'rejected' }), { status, headers: { 'Content-Type': 'application/json' } });
        return original(input, init);
      });
      render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => { function Scene() { return createElement("canvas", { "aria-label": "3D hangar scene" }); } return Scene; } }));
      await signInAsReviewer();
      fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
      await screen.findByRole('button', { name: 'View Air Three' });
      fireEvent.click(screen.getByRole('button', { name: 'View Air Three' }));
    }

    cleanup();
    stubCaseApi();
    const deniedOriginal = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/v1/cases/cp-case-c/scene') {
        const envelope = await deniedOriginal(input, init).then((response) => response.json());
        envelope.case.permission = false; envelope.canResetBlockedVariant = false; envelope.allowedActions = [];
        return new Response(JSON.stringify(envelope), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return deniedOriginal(input, init);
    });
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => { function Scene() { return createElement("canvas", { "aria-label": "3D hangar scene" }); } return Scene; } }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'View Air Three' });
    fireEvent.click(screen.getByRole('button', { name: 'View Air Three' }));
    expect(screen.queryByRole('button', { name: 'Reset blocked variant' })).toBeNull();
    expect(vi.mocked(fetch).mock.calls.filter(([url, init]) => String(url).includes('reset-blocked-variant') && (init?.method ?? 'GET') === 'POST')).toHaveLength(0);

    cleanup();
    stubCaseApi();
    const roleOriginal = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/auth/users') return new Response(JSON.stringify([{ id: 'operator-1', displayName: 'Morgan Operator', role: 'aircraft-operator' }]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/auth/sign-in') return new Response(JSON.stringify({ id: 'operator-1', displayName: 'Morgan Operator', role: 'aircraft-operator', caseScopes: ['case-001'], csrfToken: 'token' }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/v1/cases/cp-case-c/scene' && (init?.method ?? 'GET') === 'GET') {
        const envelope = await roleOriginal(input, init).then((response) => response.json());
        envelope.case.permission = true; envelope.canResetBlockedVariant = true; envelope.allowedActions = ['reset'];
        return new Response(JSON.stringify(envelope), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return roleOriginal(input, init);
    });
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => { function Scene() { return createElement("canvas", { "aria-label": "3D hangar scene" }); } return Scene; } }));
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByRole('radio', { name: 'Morgan Operator (aircraft-operator)' });
    fireEvent.click(screen.getByRole('radio', { name: 'Morgan Operator (aircraft-operator)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Revision: 7');
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'View Air Three' });
    fireEvent.click(screen.getByRole('button', { name: 'View Air Three' }));
    expect(screen.queryByRole('button', { name: 'Reset blocked variant' })).toBeNull();
    expect(vi.mocked(fetch).mock.calls.filter(([url, init]) => String(url).includes('reset-blocked-variant') && (init?.method ?? 'GET') === 'POST')).toHaveLength(0);

    cleanup();
    stubCaseApi();
    function Throws(): never { throw new Error('canvas render failed'); }
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: () => { function Scene() { return createElement(Throws); } return Scene; } }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('alert', { name: 'Hangar fallback' });
    expect(screen.getByText(/Last labelled server state: Rules-based, revision 7/)).toBeTruthy();
    expect(vi.mocked(fetch).mock.calls.filter(([url, init]) => String(url).includes('reset-blocked-variant') && (init?.method ?? 'GET') === 'POST')).toHaveLength(0);
  });

  it('HangarEntry loads scene envelopes in exact case-list order', async () => {
    stubCaseApi();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return null; } return Scene; },
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    const requests = vi.mocked(fetch).mock.calls.map(([input, init]) => `${init?.method ?? 'GET'} ${String(input)}`);
    expect(requests.filter((request) => request.includes('/api/v1/cases'))).toEqual([
      'GET /api/v1/cases',
      'GET /api/v1/cases/cp-case-a/scene',
      'GET /api/v1/cases/cp-case-b/scene',
      'GET /api/v1/cases/cp-case-c/scene',
    ]);
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    expect(screen.getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh Hangar scene' }));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/scene')).length).toBe(6));
  });

  it('HangarEntry rejects invalid contracts and scene reads with exact fallback', async () => {
    const cases = [
      { label: 'absent version', list: { cases: [] } },
      { label: 'string version', list: { contractVersion: '1', cases: [] } },
      { label: 'numeric two', list: { contractVersion: 2, cases: [] } },
    ];
    for (const testCase of cases) {
      stubCaseApi();
      const original = vi.mocked(fetch).getMockImplementation()!;
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === '/api/v1/cases') return new Response(JSON.stringify(testCase.list), { status: 200, headers: { 'Content-Type': 'application/json' } });
        return original(input, init);
      });
      render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: vi.fn() }));
      await signInAsReviewer();
      fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
      await screen.findByText(/Invalid hangar scene contract/);
      expect(document.querySelectorAll('canvas')).toHaveLength(0);
      cleanup(); vi.unstubAllGlobals();
    }

    for (const sceneCase of [
      { label: 'absent envelope', body: null },
      { label: 'unsupported envelope', body: { contractVersion: 2 } },
    ]) {
      stubCaseApi();
      const original = vi.mocked(fetch).getMockImplementation()!;
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === '/api/v1/cases') return new Response(JSON.stringify({ contractVersion: 1, cases: [{ caseId: 'cp-case-a' }] }), { status: 200 });
        if (url === '/api/v1/cases/cp-case-a/scene') return new Response(sceneCase.body === null ? null : JSON.stringify(sceneCase.body), { status: 200, headers: { 'Content-Type': 'application/json' } });
        return original(input, init);
      });
      render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: vi.fn() }));
      await signInAsReviewer();
      fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
      await screen.findByText(/Invalid hangar scene contract/);
      expect(document.querySelectorAll('canvas')).toHaveLength(0);
      cleanup(); vi.unstubAllGlobals();
    }

    stubCaseApi();
    const originalMismatch = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/v1/cases') return new Response(JSON.stringify({ contractVersion: 1, cases: [{ caseId: 'cp-case-a' }] }), { status: 200 });
      if (url === '/api/v1/cases/cp-case-a/scene') return new Response(JSON.stringify({ contractVersion: 1, case: {
        caseId: 'cp-case-b', caseRevision: 7, status: {}, owner: 'Technical reviewer', nextAction: null, capabilityLabel: 'Rules-based', permission: true,
      }, objects: [{ objectId: 'SYN-CP-B-AIR-001', kind: 'aircraft', caseId: 'cp-case-b' }, { objectId: 'SYN-CP-B-ENG-001', kind: 'engine', caseId: 'cp-case-b' }, { objectId: 'SYN-CP-B-CMP-001', kind: 'component', caseId: 'cp-case-b' }, { objectId: 'SYN-CP-B-CMP-002', kind: 'component', caseId: 'cp-case-b' }], canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      return originalMismatch(input, init);
    });
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: vi.fn() }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText(/Invalid hangar scene contract/);
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(vi.mocked(fetch).mock.calls.map(([input]) => String(input)).filter((url) => url.endsWith('/scene'))).toEqual(['/api/v1/cases/cp-case-a/scene']);
    cleanup(); vi.unstubAllGlobals();

    for (const status of [403, 500]) {
      stubCaseApi();
      const original = vi.mocked(fetch).getMockImplementation()!;
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url === '/api/v1/cases') return new Response(JSON.stringify({ contractVersion: 1, cases: [{ caseId: 'cp-case-a' }] }), { status: 200 });
        if (url === '/api/v1/cases/cp-case-a/scene') return new Response(null, { status });
        return original(input, init);
      });
      render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: vi.fn() }));
      await signInAsReviewer();
      fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
      await screen.findByText(/Hangar refresh failed/);
      expect(document.querySelectorAll('canvas')).toHaveLength(0);
      cleanup(); vi.unstubAllGlobals();
    }
  });

  it('HangarEntry queues overlapping focus and explicit refreshes and rejects the lower revision', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    const original = vi.mocked(fetch).getMockImplementation()!;
    let resolveFocus!: (value: Response) => void; let resolveExplicit!: (value: Response) => void;
    const focusResponse = new Promise<Response>((resolve) => { resolveFocus = resolve; });
    const explicitResponse = new Promise<Response>((resolve) => { resolveExplicit = resolve; });
    let sceneCalls = 0;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/v1/cases/cp-case-a/scene') {
        sceneCalls += 1;
        return sceneCalls === 1 ? focusResponse : sceneCalls === 2 ? explicitResponse : new Response(JSON.stringify({ contractVersion: 1, case: { caseId: 'cp-case-a', caseRevision: 9, status: {}, owner: 'Technical reviewer', nextAction: null, capabilityLabel: 'Rules-based', permission: true }, objects: [
          { objectId: 'SYN-CP-A-AIR-001', kind: 'aircraft', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-ENG-001', kind: 'engine', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-CMP-001', kind: 'component', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-CMP-002', kind: 'component', caseId: 'cp-case-a' },
        ], canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return original(input, init);
    });
    window.dispatchEvent(new Event('focus'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh Hangar scene' }));
    await waitFor(() => expect(sceneCalls).toBe(1));
    const makeEnvelope = (revision: number) => new Response(JSON.stringify({ contractVersion: 1, case: { caseId: 'cp-case-a', caseRevision: revision, status: {}, owner: 'Technical reviewer', nextAction: null, capabilityLabel: 'Rules-based', permission: true }, objects: [
      { objectId: 'SYN-CP-A-AIR-001', kind: 'aircraft', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-ENG-001', kind: 'engine', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-CMP-001', kind: 'component', caseId: 'cp-case-a' }, { objectId: 'SYN-CP-A-CMP-002', kind: 'component', caseId: 'cp-case-a' },
    ], canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    resolveFocus(makeEnvelope(8));
    await waitFor(() => expect(sceneCalls).toBe(2));
    expect(sceneCalls).toBe(2);
    resolveExplicit(makeEnvelope(7));
    await screen.findByText(/Hangar refresh failed/);
    expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 8; Labels: Technical reviewer; Rules-based');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(screen.getAllByRole('button').some((button) => button.getAttribute('name') === null)).toBe(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Select case' }));
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(selection).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh Hangar scene' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    await screen.findByRole('button', { name: 'Open data rack' });
    await waitFor(() => expect(screen.queryByText(/Hangar refresh failed/)).toBeNull());
    await waitFor(() => expect(screen.getByLabelText('Hangar retained snapshot').textContent).toBe('Revision: 9; Labels: Technical reviewer; Rules-based'));
    expect(screen.getByLabelText('Hangar retained snapshot')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Open data rack' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Pointer select AIR-1' }));
    expect(selection).toHaveBeenCalledTimes(1);
    expect(selection).toHaveBeenCalledWith('AIR-1');
    fireEvent.click(screen.getByRole('button', { name: 'Open data rack' }));
    expect(screen.getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
  });

  it('HangarEntry default loader returns a renderable scene component', async () => {
    const Scene = await loadHangarScene();
    expect(typeof Scene).toBe('function');
  });

  it('HangarEntry production Data Room confirmation starts mapped guide route', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function ProductionScene() { return null; } return ProductionScene; },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    // App offers only its Data Room entries: an unmapped identifier such as AIR-X has no menu item to confirm.
    expect(screen.getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
    expect(screen.queryByRole('menuitem', { name: /AIR-X/ })).toBeNull();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Air One' }));
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    const routePoints = document.querySelector('polyline')?.getAttribute('points')?.trim().split(/\s+/) ?? [];
    expect(routePoints.at(-1)).toBe('4,-4');
    expect(screen.queryByRole('menu', { name: 'Data rack menu' })).toBeNull();
    expect(selection).not.toHaveBeenCalled();

    const unknownConfirmed = confirmHangarRackMenuEntry(hangarDataRoomEntriesForTest(), 'AIR-X', selection);
    expect(unknownConfirmed).toBe(false);
    expect(selection).not.toHaveBeenCalled();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();

    const walk = screen.getByRole('region', { name: 'Walk region' });
    fireEvent.keyDown(walk, { key: 'Escape' });
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open data rack' }));
    expect(confirmHangarRackMenuEntry(hangarDataRoomEntriesForTest(), 'AIR-X', selection)).toBe(false);
    expect(selection).not.toHaveBeenCalled();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
  });

  function hangarDataRoomEntriesForTest() {
    return [{ caseId: 'AIR-1', name: 'Air One', category: 'narrow-body', unavailable: false, sources: null }];
  }

  it('HangarEntry production guide arrival uses shared aircraft selection once', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Pointer select AIR-1' }));
    expect(selection).toHaveBeenCalledTimes(1);
    expect(selection).toHaveBeenLastCalledWith('AIR-1');
    // A real keyboard press (keydown, then the button's own click): exactly one selection.
    screen.getByRole('button', { name: 'Keyboard select AIR-2' }).focus();
    await userEvent.setup().keyboard('{Enter}');
    expect(selection).toHaveBeenCalledTimes(2);
    expect(selection).toHaveBeenLastCalledWith('AIR-2');
    selection.mockClear();
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Air One' }));
    const walk = screen.getByRole('region', { name: 'Walk region' });
    for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowRight' });
    for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowUp' });
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('Arrival');
    expect(selection).toHaveBeenCalledTimes(1);
    expect(selection).toHaveBeenCalledWith('AIR-1');
    fireEvent.keyDown(walk, { key: 'ArrowDown' });
    expect(selection).toHaveBeenCalledTimes(1);
  });

  it('HangarEntry non-3D parity', async () => {
    const requiredActions = ['selection', 'status', 'evidence', 'journey', 'review'];
    expect(HANGAR_NON_3D_ACTIONS).toEqual(requiredActions);
    expect(HANGAR_3D_ACTIONS).toEqual(requiredActions);
    expect(getHangarNon3dActions()).toEqual(requiredActions);
    expect(getHangarFallbackReason({ contractVersion: 1, browserSupported: false, webgl2: { webgl2: true } })).toBe('unsupported_browser');

    stubCaseApi();
    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: vi.fn(),
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');

    const renderedLabels = ['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review'];
    expect(renderedLabels).toEqual(['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review']);
    for (const label of renderedLabels) expect(screen.getByRole('button', { name: label })).toBeTruthy();
    expect(screen.getAllByRole('button').filter((button) => renderedLabels.includes(button.textContent?.trim() ?? ''))).toHaveLength(5);
  });

  it('hangar UI rejects fallback entry before mounting a canvas', async () => {
    stubCaseApi();

    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: vi.fn(),
    } as HangarEntryAppProps));
    await signInAsReviewer();
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
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(nullLoader).not.toHaveBeenCalled();
  });

  it('supported versions load once and contract fallback renders no canvas with a non-3D link', async () => {
    stubCaseApi();
    const loader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    await signInAsReviewer();
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
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText(/Invalid hangar scene contract/);
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(loader).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('link', { name: 'Use the supported contract version in the non-3D list.' }).getAttribute('href')).toBe('#non-3d-list');
  });

  it('supported versions pass eligibility and unsupported versions reject', () => {
    for (const version of [120, 121, 139, 140]) {
      expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Chrome', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
      expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Edge', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
    }
    expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Firefox', version: 140 }, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
  });
});
