import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import App, { resolveSessionCaseId } from '../src/App';

const users = [{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }];
const signedIn = { id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' };
const decisions = [{ id: 'accept', label: 'Accept finding' }, { id: 'reject', label: 'Reject finding' }];

function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

const originalWindowLocation = window.location;

beforeAll(() => {
  vi.stubGlobal('ResizeObserver', class {
    observe() {}
    unobserve() {}
    disconnect() {}
  });
});

function resetWindowLocation() {
  Object.defineProperty(window, 'location', {
    configurable: true,
    value: originalWindowLocation,
  });
  window.history.replaceState({}, '', '/');
}

function dashboard(queue: unknown[] = [], activity: unknown[] = [], allowed = decisions, traces: { label: string; url: string }[] = []) {
  return {
    allowedDecisions: allowed,
    capabilities: [{
      id: 'records-investigation', name: 'Records investigation', label: 'AI agent', designOnly: false,
      openItems: 0, nextDecisionOwner: null, evidence: traces, design: null,
      caseStudyAutonomy: 'L4, autonomous', demoAutonomy: 'L3 demo',
    }],
    queue,
    activity,
  };
}

function setupApi(options: {
  healthStatus?: number;
  dashboardStatus?: number;
  // Per-URL GET answers, consumed in order (the last one repeats); anything but 200 is an error whose body must never be shown.
  statuses?: Record<string, number[]>;
  decisionStatus?: number;
  allowed?: typeof decisions;
  traces?: { label: string; url: string }[];
  traceStatus?: number;
  trace?: unknown;
  canReset?: boolean;
  resetStatus?: number;
  session?: typeof signedIn;
  signInSessions?: typeof signedIn[];
  pendingCaseId?: string;
  users?: Array<{ id: string; displayName: string; role: string }>;
} = {}) {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  const calls: { url: string; init?: RequestInit }[] = [];
  let dashboardRequests = 0;
  let signInSessionIndex = 0;
  let resolvePendingCase: ((response: Response) => void) | undefined;
  const pendingCaseProjection = options.pendingCaseId
    ? new Promise<Response>((resolve) => { resolvePendingCase = resolve; })
    : null;
  Object.defineProperty(calls, 'resolvePendingCase', { get: () => resolvePendingCase });
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({ url, init });
    const queued = options.statuses?.[url];
    if (queued && method === 'GET') {
      const status = queued.length > 1 ? queued.shift()! : queued[0];
      if (status !== 200) return json({ error: 'stack passcode session csrf finding-secret' }, status);
    }
    if (url === '/api/health') return json({ status: 'ok' }, options.healthStatus ?? 200);
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json(options.users ?? users);
    if (url === '/api/auth/sign-in') {
      const session = options.signInSessions?.[signInSessionIndex++] ?? options.session ?? signedIn;
      return json(session);
    }
    if (url === '/api/auth/sign-out' && method === 'POST') return new Response(null, { status: 204 });
    if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
    if (options.pendingCaseId && url === `/api/cases/${encodeURIComponent(options.pendingCaseId)}` && method === 'GET') return pendingCaseProjection!;
    if (url === '/api/cases/case-001/readiness' && method === 'GET') return json({
      monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2,
      sentence: 'Return in 9 months. 1 records gap found, 1 routine request sent, 1 item held for internal review, 2 documents not processed.',
    });
    if (url === '/api/cases/case-001/dashboard' && method === 'GET') {
      dashboardRequests += 1;
      if (options.dashboardStatus) return json({ error: 'not allowed' }, options.dashboardStatus);
      return json(dashboardRequests === 1
        ? dashboard([], [], options.allowed, options.traces)
        : dashboard(
          [{ itemId: 'finding-2', label: 'Human decision' }],
          [{ sequence: 1, actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' }],
          options.allowed, options.traces,
        ));
    }
    const scopedCase = url.match(/^\/api\/cases\/([^/]+)$/)?.[1];
    if (scopedCase && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
    const scopedDashboard = url.match(/^\/api\/cases\/([^/]+)\/dashboard$/)?.[1];
    if (scopedDashboard && method === 'GET') return json(dashboard([], [], options.allowed, options.traces));
    const scopedReadiness = url.match(/^\/api\/cases\/([^/]+)\/readiness$/)?.[1];
    if (scopedReadiness && method === 'GET') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months. 0 records gap found, 0 routine request sent, 0 item held for internal review, 0 documents not processed.' });
    const scopedReviews = url.match(/^\/api\/cases\/([^/]+)\/technical-reviews$/)?.[1];
    if (scopedReviews && scopedReviews !== 'case-001' && method === 'GET') return json({ basisVersion: 4, decisions: [] });
    if (scopedReviews && scopedReviews !== 'case-001' && method === 'POST') return json({ decision: 'accept', current: true });
    if (url.includes('/dashboard/traces/')) return json(options.trace ?? {}, options.traceStatus ?? 200);
    if (url === '/api/v1/cases') return json({ contractVersion: 1, cases: [
      { caseId: 'cp-case-a', name: 'Case A', aircraftCategory: 'narrow-body', caseRevision: 7 },
      { caseId: 'cp-case-b', name: 'Case B', aircraftCategory: 'regional', caseRevision: 7 },
      { caseId: 'cp-case-c', name: 'Case C', aircraftCategory: 'wide-body', caseRevision: 7 },
    ] });
    const sceneMatch = url.match(/^\/api\/v1\/cases\/([^/]+)\/scene$/);
    if (sceneMatch && method === 'GET') {
      const caseId = sceneMatch[1]; const letter = caseId.slice(-1).toUpperCase();
      return json({ contractVersion: 1, case: { caseId, caseRevision: 7, status: { assessment: 'ready' }, owner: 'Technical reviewer', nextAction: { text: 'Review evidence' }, name: `Case ${letter}`, aircraftName: `Case ${letter}`, aircraftCategory: letter === 'A' ? 'narrow-body' : letter === 'B' ? 'regional' : 'wide-body', capabilityLabel: 'Rules-based', permission: options.canReset ? true : { canResetBlockedVariant: options.canReset ?? false } }, canResetBlockedVariant: caseId === 'cp-case-c' && (options.canReset ?? false), allowedActions: caseId === 'cp-case-c' && options.canReset ? ['reset'] : [], history: [], evidence: [], documentRefs: [], objects: ['AIR-001','ENG-001','CMP-001','CMP-002'].map((suffix, index) => ({ objectId: `SYN-CP-${letter}-${suffix}`, kind: index === 0 ? 'aircraft' : index === 1 ? 'engine' : 'component', caseId })) });
    }
    if (url === '/api/cases/case-001/technical-reviews' && method === 'GET')
      return json({ basisVersion: 4, decisions: dashboardRequests > 1 ? [{ decision: 'accept', current: true }] : [] });
    if (url === '/api/cases/case-001/technical-reviews' && method === 'POST')
      return options.decisionStatus ? json({ error: 'decision rejected' }, options.decisionStatus) : json({ decision: 'accept', current: true });
    if (url === '/api/v1/cases/cp-case-c/reset-blocked-variant' && method === 'POST')
      return json({ accepted: true }, options.resetStatus ?? 200);
    throw new Error(`Unexpected request: ${method} ${url}`);
  }));
  return calls;
}

async function prepareSignIn() {
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
  fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
  await waitFor(() => expect((screen.getByRole('button', { name: 'Sign in' }) as HTMLButtonElement).disabled).toBe(false));
}

async function signIn() {
  await prepareSignIn();
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Signed in as Ada Reviewer.');
  await waitFor(() => expect(
    screen.queryByText('Revision: 7')
    || screen.queryAllByRole('alert')[0]
    || screen.queryByText('No cases are assigned to your account.')
  ).toBeTruthy());
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetWindowLocation();
});

describe('reviewer workspace', () => {
  it('AC_004_switch_role_behavior', async () => {
    const calls: Array<{ url: string; init?: RequestInit }> = [];
    const assignSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign: assignSpy },
    });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === '/api/health') return json({ status: 'ok' }, 200);
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'reviewer', displayName: 'Technical reviewer', role: 'technical-reviewer' }, { id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer' }, { id: 'legal-1', displayName: 'Legal reviewer', role: 'legal-reviewer' }, { id: 'exec-1', displayName: 'Executive observer', role: 'executive' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'reviewer', displayName: 'Technical reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' });
      if (url === '/api/auth/switch-user') {
        const body = JSON.parse(String(init?.body ?? '{}'));
        return json({ id: body.userId, displayName: 'Finance reviewer', role: 'finance-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-next-token' }, 200);
      }
      if (url === '/api/cases/case-001') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard') return json(dashboard([], [], decisions));
      if (url === '/api/cases/case-001/technical-reviews') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
      return json({});
    }));

    render(<App />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Technical reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Signed in as Technical reviewer.');

    fireEvent.click(screen.getByRole('button', { name: 'Technical reviewer' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Switch role: Finance reviewer' }));

    await waitFor(() => expect(calls.some(({ url, init }) => url === '/api/auth/switch-user' && init?.method === 'POST')).toBe(true));
    const switchCall = calls.find(({ url, init }) => url === '/api/auth/switch-user' && init?.method === 'POST');
    expect(JSON.parse(String(switchCall?.init?.body))).toEqual({ userId: 'fin-1' });
    expect((switchCall?.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
    expect(assignSpy).toHaveBeenCalledWith('/work');
    expect(screen.getByText('Finance reviewer')).toBeTruthy();
  });

  it('AC_004_switch_role_failure', async () => {
    const calls: Array<{ url: string; init?: RequestInit }> = [];
    const assignSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign: assignSpy },
    });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === '/api/health') return json({ status: 'ok' }, 200);
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'reviewer', displayName: 'Technical reviewer', role: 'technical-reviewer' }, { id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'reviewer', displayName: 'Technical reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' });
      if (url === '/api/auth/switch-user') return json({ error: 'You do not have permission for this action.' }, 403);
      if (url === '/api/cases/case-001') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard') return json(dashboard([], [], decisions));
      if (url === '/api/cases/case-001/technical-reviews') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
      return json({});
    }));

    render(<App />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Technical reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Signed in as Technical reviewer.');

    fireEvent.click(screen.getByRole('button', { name: 'Technical reviewer' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Switch role: Finance reviewer' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('You do not have permission for this action.');
    expect(calls.filter(({ url }) => url === '/api/auth/switch-user')).toHaveLength(1);
    expect(assignSpy).not.toHaveBeenCalled();
    expect(screen.getByText('Technical reviewer')).toBeTruthy();
    expect(screen.getByText('Case case-001')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Switch role: Finance reviewer' })).toBeNull();
  });

  it('presenter_scoped_reset_app_request_contract', async () => {
    const calls: Array<{ url: string; init?: RequestInit }> = [];
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign: vi.fn(), pathname: '/demo' },
    });
    window.history.replaceState({}, '', '/demo');
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ url, init });
      if (url === '/api/health') return json({ status: 'ok' }, 200);
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'presenter', displayName: 'Demo presenter', role: 'presenter' }]);
      if (url === '/api/auth/sign-in') return json({ id: 'presenter', displayName: 'Demo presenter', role: 'presenter', caseScopes: ['cp-case-c'], csrfToken: 'csrf-session-token' });
      if (url === '/api/v1/cases') return json({ contractVersion: 1, cases: [{ caseId: 'cp-case-c', name: 'Case C', aircraftCategory: 'wide-body', caseRevision: 7 }] });
      if (url === '/api/v1/cases/cp-case-c/scene') return json({
        contractVersion: 1,
        current: true,
        canResetBlockedVariant: true,
        allowedActions: ['reset'],
        case: {
          caseId: 'cp-case-c',
          caseRevision: 7,
          name: 'Case C',
          aircraftName: 'Case C',
          aircraftCategory: 'wide-body',
          capabilityLabel: 'Rules-based',
          permission: true,
          allowedActions: ['reset'],
        },
        objects: [],
      });
      if (url === '/api/v1/cases/cp-case-c/reset-blocked-variant' && method === 'POST') {
        const body = JSON.parse(String(init?.body ?? '{}'));
        expect(body).toMatchObject({ contractVersion: 1, expectedRevision: 7, variant: 'blocked' });
        expect(body.commandId).toMatch(/^cp-reset-7-\d+$/);
        expect((init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
        return json({ accepted: true }, 200);
      }
      if (url === '/api/cases/cp-case-c') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/cp-case-c/dashboard') return json(dashboard([], [], decisions));
      if (url === '/api/cases/cp-case-c/technical-reviews') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/cp-case-c/readiness') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
      return json({});
    }));

    render(<App />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: /Demo presenter/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Signed in as Demo presenter.');
    fireEvent.click(await screen.findByRole('button', { name: 'Reset blocked variant' }));

    await waitFor(() => expect(calls.some(({ url, init }) => url === '/api/v1/cases/cp-case-c/reset-blocked-variant' && init?.method === 'POST')).toBe(true));
    const resetCall = calls.find(({ url, init }) => url === '/api/v1/cases/cp-case-c/reset-blocked-variant' && init?.method === 'POST');
    expect(resetCall).toBeTruthy();
    expect((resetCall?.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
    const body = JSON.parse(String(resetCall?.init?.body ?? '{}'));
    expect(body).toMatchObject({ contractVersion: 1, expectedRevision: 7, variant: 'blocked' });
    expect(body.commandId).toMatch(/^cp-reset-7-\d+$/);
  });

  it('AC_004_demo_view_as_contract', async () => {
    const personaChecks = [
      { id: 'presenter', label: 'Demo presenter', home: '/demo' },
      { id: 'reviewer', label: 'Technical reviewer', home: '/work' },
      { id: 'fin-1', label: 'Finance reviewer', home: '/work' },
      { id: 'legal-1', label: 'Legal reviewer', home: '/work' },
      { id: 'exec-1', label: 'Executive observer', home: '/portfolio' },
    ] as const;

    for (const persona of personaChecks) {
      const calls: Array<{ url: string; init?: RequestInit }> = [];
      const assignSpy = vi.fn();
      Object.defineProperty(window, 'location', {
        configurable: true,
        value: { ...window.location, assign: assignSpy },
      });
      vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = init?.method ?? 'GET';
        calls.push({ url, init });
        if (url === '/api/health') return json({ status: 'ok' }, 200);
        if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
        if (url === '/api/auth/users') return json(personaChecks.map((entry) => ({ id: entry.id, displayName: entry.label, role: entry.id === 'presenter' ? 'presenter' : entry.id === 'reviewer' ? 'technical-reviewer' : entry.id === 'fin-1' ? 'finance-reviewer' : entry.id === 'legal-1' ? 'legal-reviewer' : 'executive' })));
        if (url === '/api/auth/sign-in') return json({ id: 'presenter', displayName: 'Demo presenter', role: 'presenter', caseScopes: ['cp-case-c'], csrfToken: 'csrf-session-token' });
        if (url === '/api/auth/switch-user') {
          const body = JSON.parse(String(init?.body ?? '{}'));
          expect(body).toEqual({ userId: persona.id });
          expect((init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
          return json({ id: persona.id, displayName: persona.label, role: persona.id === 'presenter' ? 'presenter' : persona.id === 'reviewer' ? 'technical-reviewer' : persona.id === 'fin-1' ? 'finance-reviewer' : persona.id === 'legal-1' ? 'legal-reviewer' : 'executive', caseScopes: ['cp-case-c'], csrfToken: 'csrf-next-token' }, 200);
        }
        if (url === '/api/v1/cases') return json({ contractVersion: 1, cases: [{ caseId: 'cp-case-c', name: 'Case C', aircraftCategory: 'wide-body', caseRevision: 7 }] });
        if (url === '/api/v1/cases/cp-case-c/scene') return json({ contractVersion: 1, current: true, canResetBlockedVariant: true, allowedActions: ['reset'], case: { caseId: 'cp-case-c', caseRevision: 7, name: 'Case C', aircraftName: 'Case C', aircraftCategory: 'wide-body', capabilityLabel: 'Rules-based', permission: true, allowedActions: ['reset'] }, objects: [] });
        if (url === '/api/cases/cp-case-c') return json({ revision: 7, basis: [{ version: 4 }] });
        if (url === '/api/cases/cp-case-c/dashboard') return json(dashboard([], [], decisions));
        if (url === '/api/cases/cp-case-c/technical-reviews') return json({ basisVersion: 4, decisions: [] });
        if (url === '/api/cases/cp-case-c/readiness') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
        return json({});
      }));

      render(<App />);
      fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
      fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
      fireEvent.click(await screen.findByRole('radio', { name: /Demo presenter/i }));
      fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
      await screen.findByText('Signed in as Demo presenter.');
      fireEvent.click(screen.getAllByRole('button', { name: new RegExp(`View as ${persona.label}`, 'i') })[0]);

      await waitFor(() => expect(calls.some(({ url, init }) => url === '/api/auth/switch-user' && init?.method === 'POST')).toBe(true));
      const switchCall = calls.find(({ url, init }) => url === '/api/auth/switch-user' && init?.method === 'POST');
      expect(switchCall).toBeTruthy();
      expect(JSON.parse(String(switchCall?.init?.body ?? '{}'))).toEqual({ userId: persona.id });
      expect((switchCall?.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
      expect(assignSpy).toHaveBeenCalledWith(persona.home);
      cleanup();
    }
  });

  it('keeps the active case when it stays in scope and falls back otherwise', () => {
    const session = { ...signedIn, caseScopes: ['cp-case-a', 'cp-case-b'] };
    expect(resolveSessionCaseId(session, 'cp-case-b')).toBe('cp-case-b');
    expect(resolveSessionCaseId(session, 'cp-case-a')).toBe('cp-case-a');
    expect(resolveSessionCaseId({ ...session, caseScopes: ['cp-case-a'] }, 'cp-case-b')).toBe('cp-case-a');
    expect(resolveSessionCaseId({ ...session, caseScopes: [] }, 'cp-case-b')).toBeNull();
    expect(resolveSessionCaseId(null, 'cp-case-b')).toBeNull();
  });

  it('retains the selected in-scope case when the session changes and the new first scope differs', async () => {
    const firstSession = { ...signedIn, caseScopes: ['cp-case-b', 'cp-case-a'] };
    const nextSession = { ...signedIn, caseScopes: ['cp-case-a', 'cp-case-b'], csrfToken: 'csrf-next-token' };
    setupApi({ session: firstSession, signInSessions: [firstSession, nextSession] });
    render(<App />);
    await signIn();

    expect(await screen.findByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy();
    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-a' } });
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Case cp-case-a' })).toBeTruthy());
    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-b' } });
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy());

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(screen.getByLabelText('Demo passcode')).toBeTruthy());

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Ada Reviewer.');
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy());
  });

  it('falls back to the first in-scope case when the selected case is no longer available after sign-in', async () => {
    const firstSession = { ...signedIn, caseScopes: ['cp-case-b', 'cp-case-a'] };
    const nextSession = { ...signedIn, caseScopes: ['cp-case-a'], csrfToken: 'csrf-next-token' };
    setupApi({ session: firstSession, signInSessions: [firstSession, nextSession] });
    render(<App />);
    await signIn();

    expect(await screen.findByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(screen.getByLabelText('Demo passcode')).toBeTruthy());

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Ada Reviewer.');
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Case cp-case-a' })).toBeTruthy());
    expect(screen.queryByRole('heading', { name: 'Case cp-case-b' })).toBeNull();
  });

  it('shows no trace when the dashboard has no stored trace links', async () => {
    setupApi();
    render(<App />);
    await signIn();

    expect(screen.getByText('No trace recorded')).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Investigation trace steps' })).toBeNull();
  });

  it('fetches a selected trace URL and shows no trace for a missing record', async () => {
    const url = '/api/cases/case-001/dashboard/traces/run-1/investigation-1';
    const calls = setupApi({ traces: [{ label: 'Investigation trace', url }], traceStatus: 404 });
    render(<App />);
    await signIn();

    fireEvent.click(screen.getByRole('link', { name: 'Investigation trace' }));
    expect(await screen.findByText('No trace recorded')).toBeTruthy();
    expect(calls.some((call) => call.url === url && (call.init?.method ?? 'GET') === 'GET')).toBe(true);
    expect(screen.queryByRole('list', { name: 'Investigation trace steps' })).toBeNull();
  });

  it('renders the selected stored trace returned by the same-origin endpoint', async () => {
    const url = '/api/cases/case-001/dashboard/traces/run-1/investigation-1';
    const calls = setupApi({
      traces: [{ label: 'Investigation trace', url }],
      trace: { steps: [
        { sequence: 2, toolName: 'search-manual', documentId: 'doc-b', page: 2, citationCheckOutcome: 'requested', correctionCode: 'None', policyHandover: 'None' },
        { sequence: 1, toolName: 'citation-check', documentId: 'doc-a', page: 1, citationCheckOutcome: 'verified', correctionCode: 'None', policyHandover: 'None' },
      ] },
    });
    render(<App />);
    await signIn();

    fireEvent.click(screen.getByRole('link', { name: 'Investigation trace' }));
    const list = await screen.findByRole('list', { name: 'Investigation trace steps' });
    expect(calls.some((call) => call.url === url && (call.init?.method ?? 'GET') === 'GET')).toBe(true);
    expect(Array.from(list.querySelectorAll('li')).map((item) => item.textContent))
      .toEqual(['Citation check — Outcome: verified', 'Tool call — search-manual — Outcome: requested']);
  });

  it('AC_006_returnTo_and_role_route_integration', async () => {
    const financeSession = { ...signedIn, role: 'finance-reviewer', displayName: 'Finance reviewer', caseScopes: ['case-001'] };
    const financeUsers = [{ id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer' }];

    const validCalls = setupApi({
      session: financeSession,
      users: financeUsers,
    });
    window.history.replaceState({}, '', '/portfolio?returnTo=%2Fcases%2Fcase-001%2Freserve');
    render(<App />);

    expect(screen.queryByRole('navigation', { name: 'Primary' })).toBeNull();
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Finance reviewer.');
    const primaryNavigation = screen.getByRole('navigation', { name: 'Primary' });
    const workspaceMain = primaryNavigation.closest('aside')?.parentElement?.querySelector('main');
    expect(primaryNavigation).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Finance reviewer' })).toBeTruthy();
    expect(workspaceMain).not.toBeNull();
    expect(workspaceMain?.contains(screen.getByRole('heading', { name: 'Case case-001' }))).toBe(true);
    expect(window.location.pathname).toBe('/cases/case-001/reserve');
    expect(validCalls.some(({ url }) => url === '/api/auth/sign-in')).toBe(true);

    cleanup();
    const invalidCalls = setupApi({
      session: financeSession,
      users: financeUsers,
    });
    window.history.replaceState({}, '', '/portfolio?returnTo=%2Fcases%2Fcase-001%2Ffindings');
    render(<App />);

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Finance reviewer.');
    const invalidPrimaryNavigation = screen.getByRole('navigation', { name: 'Primary' });
    const invalidWorkspaceMain = invalidPrimaryNavigation.closest('aside')?.parentElement?.querySelector('main');
    expect(window.location.pathname).toBe('/work');
    expect(invalidWorkspaceMain).not.toBeNull();
    expect(invalidWorkspaceMain?.contains(screen.getByRole('heading', { name: 'Case case-001' }))).toBe(true);
    expect(invalidCalls.some(({ url }) => url === '/api/auth/sign-in')).toBe(true);
  });

  it('AC_001_App_root_shell_and_Hangar_composition', async () => {
    setupApi({ canReset: true });
    render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarLazyLoader={() => { function Scene() { return <canvas aria-label="3D hangar scene" />; } return Scene; }} />);
    await signIn();
    const primaryNavigation = screen.getByRole('navigation', { name: 'Primary' });
    const workspaceMain = primaryNavigation.closest('aside')?.parentElement?.querySelector('main');
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeTruthy();
    expect(primaryNavigation).toBeTruthy();
    expect(workspaceMain).not.toBeNull();
    expect(workspaceMain?.contains(screen.getByRole('heading', { name: 'Case case-001' }))).toBe(true);
    expect(screen.queryByRole('heading', { name: 'Hangar' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    const hangarHeading = screen.getByRole('heading', { name: 'Hangar' });
    expect(workspaceMain?.contains(hangarHeading)).toBe(true);
    const hangarSceneControl = screen.getByRole('button', { name: 'View Case A' });
    const hangarOverviewControl = screen.getByRole('button', { name: 'Overview' });
    expect(hangarSceneControl.textContent).toBe('View Case A');
    expect(hangarOverviewControl.textContent).toBe('Overview');
    expect(screen.getByRole('button', { name: 'Open local diagnostics' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Hangar object selection' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Case A guided journey' })).toBeTruthy();
  });

  it('resets stale Hangar state when the selected case changes', async () => {
    setupApi({ canReset: true, session: { ...signedIn, caseScopes: ['case-001', 'cp-case-b'] } });
    render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarLazyLoader={() => { function Scene() { return <canvas aria-label="3D hangar scene" />; } return Scene; }} />);
    await signIn();

    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    expect(screen.getByRole('heading', { name: 'Hangar' })).toBeTruthy();

    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-b' } });
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Hangar' })).toBeNull());
    expect(screen.getByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy();
  });

  it('Dashboard_waits_for_authorized_server_state', async () => {
    const calls = setupApi();
    render(<App />);

    expect(await screen.findByText('Service connected')).toBeTruthy();
    expect(screen.queryByText('Revision: 7')).toBeNull();
    await prepareSignIn();
    expect(calls.filter(({ url }) => url.startsWith('/api/cases/'))).toHaveLength(0);

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Revision: 7')).toBeTruthy();
    expect(calls.filter(({ url }) => url.startsWith('/api/cases/')).map(({ url, init }) => `${init?.method ?? 'GET'} ${url}`))
      .toEqual(['GET /api/cases/case-001', 'GET /api/cases/case-001/dashboard', 'GET /api/cases/case-001/technical-reviews', 'GET /api/cases/case-001/readiness']);
    const summary = screen.getByText('Return in 9 months. 1 records gap found, 1 routine request sent, 1 item held for internal review, 2 documents not processed.');
    expect(summary.parentElement?.firstElementChild).toBe(summary);

    for (const [status, text] of [[401, 'Signing in…'], [404, 'Case not found or not in your scope. Retry']] as const) {
      cleanup();
      const assign = vi.fn();
      Object.defineProperty(window, 'location', { configurable: true, value: { ...originalWindowLocation, pathname: '/work', search: '', assign } });
      setupApi({ dashboardStatus: status });
      render(<App />);
      await signIn();
      expect((await screen.findByRole('alert')).textContent).toBe(text);
      expect(screen.getByText('Revision: 7')).toBeTruthy();
      expect(screen.queryByRole('heading', { name: 'Records investigation' })).toBeNull();
      expect(assign.mock.calls.length).toBe(status === 401 ? 1 : 0);
      if (status === 401) expect(assign).toHaveBeenCalledWith(expect.stringMatching(/^\/sign-in\?returnTo=/));
    }
  });

  it('legal reviewer workspace opens the obligation record route from the worklist', async () => {
    const legalSession = { id: 'legal-1', displayName: 'Ava Legal', role: 'legal-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' };
    const legalCalls: Array<{ url: string; init?: RequestInit }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      legalCalls.push({ url, init });
      if (url === '/api/health') return json({ status: 'ok' }, 200);
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([{ id: 'legal-1', displayName: 'Ava Legal', role: 'legal-reviewer' }]);
      if (url === '/api/auth/sign-in') return json(legalSession);
      if (url === '/api/v1/workspace/worklist') return json({
        contractVersion: 1,
        items: [{ caseId: 'case-001', itemId: 'OBL-1', capabilityId: 'contract-obligation', state: 'open', owner: 'legal', ageSeconds: 300, label: 'Human decision' }],
        unavailableCaseIds: [],
        truncated: false,
      });
      if (url === '/api/cases/case-001') return json({ revision: 7, basis: [{ version: 4 }] });
      if (url === '/api/cases/case-001/dashboard') return json({ allowedDecisions: [], capabilities: [], queue: [], activity: [] });
      if (url === '/api/cases/case-001/technical-reviews') return json({ basisVersion: 4, decisions: [] });
      if (url === '/api/cases/case-001/readiness') return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
      if (url === '/api/cases/case-001/obligations/OBL-1/decisions' && method === 'POST') return json({ decision: 'signed-off', simulated: true, noticeSent: false }, 200);
      return json({});
    }));

    render(<App />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Ava Legal (legal-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Signed in as Ava Legal.');
    fireEvent.click(await screen.findByText('OBL-1'));
    expect(await screen.findByRole('heading', { name: 'Obligation workspace' })).toBeTruthy();
    expect(screen.getByText('Simulated — no notice is sent.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /send notice/i })).toBeNull();
    expect(window.location.pathname).toBe('/cases/case-001/obligations');
    expect(legalCalls.some(({ url, init }) => url === '/api/cases/case-001/obligations/OBL-1/decisions' && init?.method === 'POST')).toBe(false);
  });

  it('loads the first case in the session scope and encodes its id', async () => {
    const calls = setupApi({ session: { ...signedIn, caseScopes: ['cp/case a', 'cp-case-b'] } });
    render(<App />);
    await signIn();
    expect(await screen.findByRole('heading', { name: 'Case cp/case a' })).toBeTruthy();
    expect(calls.filter(({ url }) => url.startsWith('/api/cases/')).map(({ url }) => url)).toEqual([
      '/api/cases/cp%2Fcase%20a', '/api/cases/cp%2Fcase%20a/dashboard',
      '/api/cases/cp%2Fcase%20a/technical-reviews', '/api/cases/cp%2Fcase%20a/readiness',
    ]);
    expect(calls.some(({ url }) => url.includes('case-001'))).toBe(false);

    cleanup();
    const case001Calls = setupApi({ session: { ...signedIn, caseScopes: ['case-001'] } });
    render(<App />);
    await signIn();
    expect(await screen.findByRole('heading', { name: 'Case case-001' })).toBeTruthy();
    expect(case001Calls.filter(({ url }) => url.startsWith('/api/cases/')).map(({ url }) => url)).toEqual([
      '/api/cases/case-001', '/api/cases/case-001/dashboard',
      '/api/cases/case-001/technical-reviews', '/api/cases/case-001/readiness',
    ]);
    expect(screen.queryByRole('combobox', { name: 'Case' })).toBeNull();
  });

  it('shows a message and requests nothing when no case is assigned', async () => {
    const calls = setupApi({ session: { ...signedIn, caseScopes: [] } });
    render(<App />);
    await signIn();
    expect(await screen.findByText('No cases are assigned to your account.')).toBeTruthy();
    expect(calls.filter(({ url }) => url.startsWith('/api/cases/'))).toHaveLength(0);
    expect(screen.queryByRole('combobox', { name: 'Case' })).toBeNull();
  });

  it('switching the case picker reloads projections and clears prior case data', async () => {
    const calls = setupApi({
      session: { ...signedIn, caseScopes: ['cp-case-a', 'cp-case-b'] },
      pendingCaseId: 'cp-case-b',
    });
    render(<App />);
    await signIn();
    await screen.findByRole('heading', { name: 'Case cp-case-a' });

    const picker = screen.getByRole('combobox', { name: 'Case' }) as HTMLSelectElement;
    expect(Array.from(picker.options).map((option) => option.value)).toEqual(['cp-case-a', 'cp-case-b']);
    expect(screen.getByRole('button', { name: 'Accept finding' })).toBeTruthy();

    fireEvent.change(picker, { target: { value: 'cp-case-b' } });
    expect(screen.queryByText('Revision: 7')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Accept finding' })).toBeNull();

    const resolver = (calls as typeof calls & { resolvePendingCase: (response: Response) => void }).resolvePendingCase;
    resolver(json({ revision: 7, basis: [{ version: 4 }] }));

    expect(await screen.findByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy();
    await waitFor(() => expect(calls.map(({ url }) => url)).toEqual(expect.arrayContaining([
      '/api/cases/cp-case-b', '/api/cases/cp-case-b/dashboard',
      '/api/cases/cp-case-b/technical-reviews', '/api/cases/cp-case-b/readiness',
    ])));
    expect(calls.some(({ url }) => url.startsWith('/api/cases/cp-case-a'))).toBe(true);
  });

  const FORBIDDEN = 'You do not have permission for this action.';
  const UNAVAILABLE = 'Service temporarily unavailable';
  const regionOf = (name: string) => screen.getByRole('region', { name: `${name} region` });
  const requests = (calls: { url: string; init?: RequestInit }[], url: string) => calls.filter((call) => call.url === url && (call.init?.method ?? 'GET') === 'GET').length;
  const REVIEWS = '/api/cases/case-001/technical-reviews';

  it('AC_019_mixed_region_error_contract', async () => {
    for (const [status, text] of [[403, FORBIDDEN], [404, 'Case not found or not in your scope.'], [503, UNAVAILABLE]] as const) {
      cleanup();
      setupApi({ statuses: { '/api/cases/case-001': [status] } });
      render(<App />);
      await signIn();
      expect(within(regionOf('Case')).getByRole('alert').textContent).toBe(`${text} Retry`);
      expect(screen.queryByText('Revision: 7')).toBeNull();
      expect(screen.getByRole('heading', { name: 'Records investigation' })).toBeTruthy();
      expect(document.body.textContent).not.toMatch(/stack|passcode|csrf|finding-secret/);
    }

    // 401 leaves for sign-in and keeps the current path in returnTo.
    cleanup();
    const assign = vi.fn();
    Object.defineProperty(window, 'location', { configurable: true, value: { ...originalWindowLocation, pathname: '/work', search: '?tab=review', assign } });
    setupApi({ statuses: { '/api/cases/case-001': [401] } });
    render(<App />);
    await signIn();
    expect(within(regionOf('Case')).getByRole('alert').textContent).toBe('Signing in…');
    expect(assign).toHaveBeenCalledWith('/sign-in?returnTo=%2Fwork%3Ftab%3Dreview');
    expect(document.body.textContent).not.toMatch(/stack|passcode|csrf|finding-secret/);
  });

  it('AC_019_simultaneous_region_errors_remain_recoverable', async () => {
    setupApi({ statuses: { '/api/cases/case-001/dashboard': [403, 200], [REVIEWS]: [503, 200] } });
    render(<App />);
    await signIn();

    expect(screen.getByText('Revision: 7')).toBeTruthy();
    expect(screen.getByText(/^Return in 9 months\./)).toBeTruthy();
    expect(within(regionOf('Dashboard')).getByRole('alert').textContent).toBe(`${FORBIDDEN} Retry`);
    expect(within(regionOf('Technical review')).getByRole('alert').textContent).toBe(`${UNAVAILABLE} Retry`);
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(2);
    expect(screen.queryByText(/Technical-review decision/)).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Records investigation' })).toBeNull();
    expect(document.body.textContent).not.toMatch(/stack|passcode|csrf|finding-secret/);

    fireEvent.click(within(regionOf('Technical review')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Technical-review decision: No current decision')).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Technical review region' })).toBeNull();
    expect(within(regionOf('Dashboard')).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByText('Revision: 7')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Records investigation' })).toBeNull();

    fireEvent.click(within(regionOf('Dashboard')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('heading', { name: 'Records investigation' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Dashboard region' })).toBeNull();
    expect(screen.getByText('Revision: 7')).toBeTruthy();
    expect(screen.getByText('Technical-review decision: No current decision')).toBeTruthy();
    // Case and readiness fail too: all four failures stay visible, each with its own Retry.
    cleanup();
    setupApi({ statuses: {
      '/api/cases/case-001': [503, 200], '/api/cases/case-001/dashboard': [403, 200], [REVIEWS]: [503, 200], '/api/cases/case-001/readiness': [503, 200],
    } });
    render(<App />);
    await signIn();

    for (const [name, text] of [['Case', UNAVAILABLE], ['Dashboard', FORBIDDEN], ['Technical review', UNAVAILABLE], ['Readiness', UNAVAILABLE]]) {
      expect(within(regionOf(name)).getByRole('alert').textContent).toBe(`${text} Retry`);
      expect(within(regionOf(name)).getAllByRole('button', { name: 'Retry' })).toHaveLength(1);
    }
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(4);
    expect(screen.queryByText('Revision: 7')).toBeNull();
    expect(screen.queryByText(/^Return in 9 months\./)).toBeNull();
    expect(screen.queryByText(/Technical-review decision/)).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Records investigation' })).toBeNull();

    fireEvent.click(within(regionOf('Case')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Revision: 7')).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Case region' })).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(3);

    fireEvent.click(within(regionOf('Readiness')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(/^Return in 9 months\./)).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Readiness region' })).toBeNull();
    expect(within(regionOf('Dashboard')).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(within(regionOf('Technical review')).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByText('Revision: 7')).toBeTruthy();
  });

  it('retry_reloads_region_once_per_click', async () => {
    const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

    // Technical reviews answer 503, then 200.
    let calls = setupApi({ statuses: { [REVIEWS]: [503, 200] } });
    render(<App />);
    await signIn();
    expect(within(regionOf('Technical review')).getByRole('alert').textContent).toBe(`${UNAVAILABLE} Retry`);
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(1);
    expect(screen.getByText('Revision: 7')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Records investigation' })).toBeTruthy();
    for (const url of ['/api/cases/case-001', '/api/cases/case-001/dashboard', REVIEWS, '/api/cases/case-001/readiness']) expect(requests(calls, url)).toBe(1);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Technical-review decision: No current decision')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(requests(calls, REVIEWS)).toBe(2);
    for (const url of ['/api/cases/case-001', '/api/cases/case-001/dashboard', '/api/cases/case-001/readiness']) expect(requests(calls, url)).toBe(1);

    // The retry answers 503 again: one more request, the banner stays, and nothing is requested until the next click.
    cleanup();
    calls = setupApi({ statuses: { [REVIEWS]: [503, 503, 200] } });
    render(<App />);
    await signIn();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    // While the retry is pending the error and Retry stay, and Retry cannot be clicked again.
    expect((screen.getByRole('button', { name: 'Retry' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(requests(calls, REVIEWS)).toBe(2));
    expect(await screen.findByText(UNAVAILABLE)).toBeTruthy();
    await settle();
    expect(requests(calls, REVIEWS)).toBe(2);
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(1);
    await waitFor(() => expect((screen.getByRole('button', { name: 'Retry' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Technical-review decision: No current decision')).toBeTruthy();
    expect(requests(calls, REVIEWS)).toBe(3);

    // The case request answers 503 on first load; its retry fills only the case region.
    cleanup();
    calls = setupApi({ statuses: { '/api/cases/case-001': [503, 200] } });
    render(<App />);
    await signIn();
    expect(within(regionOf('Case')).getByRole('alert').textContent).toBe(`${UNAVAILABLE} Retry`);
    expect(screen.queryByText('Revision: 7')).toBeNull();
    expect(screen.getByRole('heading', { name: 'Records investigation' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Revision: 7')).toBeTruthy();
    expect(requests(calls, '/api/cases/case-001')).toBe(2);
    for (const url of ['/api/cases/case-001/dashboard', REVIEWS, '/api/cases/case-001/readiness']) expect(requests(calls, url)).toBe(1);
  });

  it('retry_response_is_ignored_after_context_switch', async () => {
    const holdRetry = () => {
      let release!: (response: Response) => void;
      const pending = new Promise<Response>((resolve) => { release = resolve; });
      const original = vi.mocked(fetch).getMockImplementation()!;
      let held = false;
      vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input) !== REVIEWS || held) return original(input, init);
        held = true; // only the retry that is started next waits; later loads are answered normally
        return pending;
      });
      return () => release(json({ basisVersion: 4, decisions: [{ decision: 'reject', current: true }] }));
    };

    // Case A -> case B while the case A retry is pending.
    setupApi({ session: { ...signedIn, caseScopes: ['case-001', 'cp-case-b'] }, statuses: { [REVIEWS]: [503] } });
    render(<App />);
    await signIn();
    const resolveCaseA = holdRetry();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-b' } });
    expect(await screen.findByRole('heading', { name: 'Case cp-case-b' })).toBeTruthy();
    await screen.findByText('Technical-review decision: No current decision');
    resolveCaseA();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.getByRole('combobox', { name: 'Case' })).toHaveProperty('value', 'cp-case-b');
    expect(screen.queryByText('Technical-review decision: reject')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();

    // Case A -> case B -> case A again: the response of the first visit stays ignored.
    cleanup();
    setupApi({ session: { ...signedIn, caseScopes: ['case-001', 'cp-case-b'] }, statuses: { [REVIEWS]: [503] } });
    render(<App />);
    await signIn();
    const resolveFirstVisit = holdRetry();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-b' } });
    await screen.findByRole('heading', { name: 'Case cp-case-b' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'case-001' } });
    await screen.findByRole('heading', { name: 'Case case-001' });
    expect(await within(regionOf('Technical review')).findByRole('alert')).toBeTruthy();
    resolveFirstVisit();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText('Technical-review decision: reject')).toBeNull();
    expect(within(regionOf('Technical review')).getByRole('alert').textContent).toBe(`${UNAVAILABLE} Retry`);

    // Session A -> session B while the session A retry is pending.
    cleanup();
    setupApi({ signInSessions: [{ ...signedIn, id: 'session-a' }, { ...signedIn, id: 'session-b' }], statuses: { [REVIEWS]: [503, 200] } });
    render(<App />);
    await signIn();
    const resolveSessionA = holdRetry();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByText('You are signed out.');
    await prepareSignIn();
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Technical-review decision: accept');
    resolveSessionA();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText('Technical-review decision: reject')).toBeNull();
    expect(screen.getByText('Technical-review decision: accept')).toBeTruthy();
  });

  it('a new session loads the first case of that session', async () => {
    const calls = setupApi({ signInSessions: [
      { ...signedIn, caseScopes: ['case-001'] },
      { ...signedIn, caseScopes: ['cp-case-c'] },
    ] });
    render(<App />);
    await signIn();
    await screen.findByRole('heading', { name: 'Case case-001' });

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByText('You are signed out.');
    await prepareSignIn();
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Case cp-case-c' })).toBeTruthy();
    expect(calls.filter(({ url }) => url === '/api/cases/case-001').length).toBe(1);
    expect(calls.filter(({ url }) => url === '/api/cases/cp-case-c').length).toBe(1);
    expect(calls.some(({ url }) => url === '/api/cases/case-001/dashboard')).toBe(true);
  });

  it('records a technical review against the selected case', async () => {
    const calls = setupApi({ session: { ...signedIn, caseScopes: ['cp-case-a', 'cp-case-b'] } });
    render(<App />);
    await signIn();
    await screen.findByRole('heading', { name: 'Case cp-case-a' });

    fireEvent.change(screen.getByRole('combobox', { name: 'Case' }), { target: { value: 'cp-case-b' } });
    await screen.findByRole('heading', { name: 'Case cp-case-b' });
    fireEvent.change(screen.getByLabelText('Reason for decision'), { target: { value: 'Selected case evidence reviewed.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));

    await waitFor(() => expect(calls.some(({ url, init }) => url === '/api/cases/cp-case-b/technical-reviews' && init?.method === 'POST')).toBe(true));
    const post = calls.find(({ url, init }) => url === '/api/cases/cp-case-b/technical-reviews' && init?.method === 'POST');
    expect(post?.url).toBe('/api/cases/cp-case-b/technical-reviews');
    expect((post?.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
    expect(calls.some(({ url, init }) => url === '/api/cases/case-001/technical-reviews' && init?.method === 'POST')).toBe(false);
  });

  it('Hangar case-work composition remains in the App host', async () => {
    setupApi({ canReset: true });
    render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarLazyLoader={() => { function Scene() { return <canvas aria-label="3D hangar scene" />; } return Scene; }} />);
    await signIn();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });

    const objectButtons = screen.getByLabelText('Hangar scene objects').querySelectorAll('button');
    expect(objectButtons).toHaveLength(12);
    for (const name of ['Case A', 'Case B', 'Case C']) {
      expect(screen.getByRole('button', { name: `View ${name}` })).toBeTruthy();
    }
    expect(screen.getByRole('button', { name: 'Overview' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Move avatar' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Aircraft' })).toBeNull();

    fireEvent.click(objectButtons[0]);
    expect(screen.getByRole('region', { name: /^Selection SYN-CP-A-/ })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'SYN-CP-A-AIR-001' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Hangar object selection' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Case A guided journey' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Move avatar' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Aircraft' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Reset blocked variant' })).toBeTruthy();
  });

  it('reports an unavailable API without claiming it is connected', async () => {
    setupApi({ healthStatus: 503 });
    render(<App />);
    expect(await screen.findByText('Service unavailable. Try again later.')).toBeTruthy();
  });

  it('Posts_permitted_decision_with_csrf_and_refreshes_projections', async () => {
    const calls = setupApi({ allowed: [decisions[0], { id: 'approve', label: 'Unprojected decision' }] });
    render(<App />);
    await signIn();
    expect(screen.queryByRole('button', { name: 'Reject finding' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Unprojected decision' })).toBeNull();
    expect(calls.some(({ url, init }) => url.endsWith('/technical-reviews') && init?.method === 'POST')).toBe(false);
    fireEvent.change(screen.getByLabelText('Reason for decision'), { target: { value: 'Evidence supports return.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));

    await screen.findByText('finding-2');
    expect(screen.getByText('Technical-review decision: accept')).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Activity' }).textContent).toContain('technical-review.decided');
    const post = calls.find(({ url, init }) => url.endsWith('/technical-reviews') && init?.method === 'POST');
    expect(post?.url).toBe('/api/cases/case-001/technical-reviews');
    expect((post?.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
    expect(JSON.parse(String(post?.init?.body))).toEqual({ expectedRevision: 7, expectedBasisVersion: 4, decision: 'accept', reason: 'Evidence supports return.' });
    expect(calls.filter(({ url }) => url.endsWith('/dashboard'))).toHaveLength(2);
  });

  it('App focus refresh updates production label and open selection panel from server scene', async () => {
    setupApi();
    const baseFetch = vi.mocked(fetch).getMockImplementation()!;
    let revision = 7;
    let injectedText = 'Review evidence';
    const makeEnvelope = (caseId = 'cp-case-a', caseRevision = revision) => ({ contractVersion: 1, packVersion: '1.0.2', case: { caseId, caseRevision, status: { assessment: 'ready', review: null }, name: caseId === 'cp-case-a' ? 'Air One' : caseId === 'cp-case-b' ? 'Air Two' : 'Air Three', category: caseId === 'cp-case-a' ? 'narrow-body' : caseId === 'cp-case-b' ? 'regional' : 'wide-body', owner: 'Technical reviewer', nextAction: { actionId: 'review', text: caseId === 'cp-case-a' ? (caseRevision >= 8 ? 'Approve server evidence' : 'Review evidence') : 'Review evidence' }, capabilityLabel: 'Rules-based', permission: { canResetBlockedVariant: false, allowedActions: ['evidence','selection'] } }, canResetBlockedVariant: false, allowedActions: ['evidence','selection'], history: [], evidence: [], documentRefs: [], objects: [
      { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-AIR-001`, kind: 'aircraft', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-ENG-001`, kind: 'engine', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-CMP-001`, kind: 'component', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-CMP-002`, kind: 'component', caseId },
    ] });
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === '/api/v1/cases') return json({ contractVersion: 1, cases: [{ caseId: 'cp-case-a', name: 'Air One', aircraftCategory: 'narrow-body', caseRevision: 7 }, { caseId: 'cp-case-b', name: 'Air Two', aircraftCategory: 'regional', caseRevision: 7 }, { caseId: 'cp-case-c', name: 'Air Three', aircraftCategory: 'wide-body', caseRevision: 7 }] });
      const scene = url.match(/^\/api\/v1\/cases\/([^/]+)\/scene$/);
      if (scene) return json(makeEnvelope(scene[1], scene[1] === 'cp-case-a' ? revision : 7));
      return baseFetch(input, init);
    });
    const { container } = render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarAircraftSelectionHandler={vi.fn()} hangarClientSnapshot={{ case: { caseId: 'cp-case-a', nextAction: { text: 'Injected client text' } } }} />);
    await signIn(); fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Pointer select AIR-1' });
    fireEvent.click(screen.getByRole('button', { name: 'Pointer select AIR-1' }));
        injectedText = 'Injected client text';
    expect(screen.queryByText(`Next action: ${injectedText}`)).toBeNull();
    revision = 8;
    window.dispatchEvent(new Event('focus'));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/scene')).length).toBeGreaterThanOrEqual(3));
    await waitFor(() => {
      const productionLabel = container.querySelector('[data-hangar-label-host] [data-hangar-label="SYN-CP-A-AIR-001"]') as HTMLElement | null;
      expect(productionLabel?.textContent).toBe('Air One\nnarrow-body\nRules-based\nApprove server evidence');
      expect(productionLabel?.textContent).not.toContain('Injected client text');
    });
    await waitFor(() => {
      const panel = screen.getByLabelText('Selection SYN-CP-A-AIR-001');
      expect(panel.textContent).toContain('Next action: Approve server evidence');
      expect(panel.textContent).not.toContain('Injected client text');
    });
    expect(document.body.textContent).not.toContain('Injected client text');
  });

  it('App focus refresh failure preserves stale panel and recovery enables server-authorized actions', async () => {
    setupApi();
    const baseFetch = vi.mocked(fetch).getMockImplementation()!;
    let revision = 7; let fail = false;
    const makeEnvelope = (caseId: string, caseRevision: number) => ({ contractVersion: 1, packVersion: '1.0.2', case: { caseId, caseRevision, status: { assessment: 'ready', review: null }, name: caseId === 'cp-case-a' ? 'Air One' : caseId === 'cp-case-b' ? 'Air Two' : 'Air Three', category: caseId === 'cp-case-a' ? 'narrow-body' : caseId === 'cp-case-b' ? 'regional' : 'wide-body', owner: 'Technical reviewer', nextAction: { actionId: 'review', text: caseRevision >= 8 ? 'Approve server evidence' : 'Review evidence' }, capabilityLabel: 'Rules-based', permission: { canResetBlockedVariant: false, allowedActions: ['evidence','selection'] } }, canResetBlockedVariant: false, allowedActions: caseRevision >= 8 ? ['evidence','selection'] : [], history: [], evidence: [], documentRefs: [], objects: [
      { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-AIR-001`, kind: 'aircraft', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-ENG-001`, kind: 'engine', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-CMP-001`, kind: 'component', caseId }, { objectId: `SYN-CP-${caseId.slice(-1).toUpperCase()}-CMP-002`, kind: 'component', caseId },
    ] });
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === '/api/v1/cases') return json({ contractVersion: 1, cases: [{ caseId: 'cp-case-a', name: 'Air One', aircraftCategory: 'narrow-body', caseRevision: 7 }, { caseId: 'cp-case-b', name: 'Air Two', aircraftCategory: 'regional', caseRevision: 7 }, { caseId: 'cp-case-c', name: 'Air Three', aircraftCategory: 'wide-body', caseRevision: 7 }] });
      const scene = url.match(/^\/api\/v1\/cases\/([^/]+)\/scene$/);
      if (scene) return fail ? json({ error: 'refresh failed' }, 500) : json(makeEnvelope(scene[1], scene[1] === 'cp-case-a' ? revision : 7));
      return baseFetch(input, init);
    });
    render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarAircraftSelectionHandler={vi.fn()} />);
    await signIn(); fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    const select = await screen.findByRole('button', { name: 'Pointer select AIR-1' }); fireEvent.click(select);
    await waitFor(() => expect(screen.getByText('Next action: Review evidence')).toBeTruthy());
    fail = true; window.dispatchEvent(new Event('focus'));
    await screen.findByText('Stale server snapshot: evidence and commands are unavailable.');
    expect((screen.getByRole('button', { name: 'Select SYN-CP-A-AIR-001' }) as HTMLButtonElement).disabled).toBe(true);
    fail = false; revision = 8; window.dispatchEvent(new Event('focus'));
    await waitFor(() => expect(screen.getByText('Next action: Approve server evidence')).toBeTruthy());
    expect((screen.getByRole('button', { name: 'Select SYN-CP-A-AIR-001' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps server projections unchanged after forbidden or stale decisions', async () => {
    for (const status of [403, 409]) {
      cleanup();
      const calls = setupApi({ decisionStatus: status });
      render(<App />);
      await signIn();
      fireEvent.change(screen.getByLabelText('Reason for decision'), { target: { value: 'Evidence reviewed.' } });
      fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));

      const alert = await screen.findByRole('alert');
      expect(alert.textContent).toBe(status === 403 ? 'You are not permitted to record this decision.' : 'The case changed. Refresh the case before deciding.');
      expect(calls.filter(({ url, init }) => url.endsWith('/technical-reviews') && init?.method === 'POST')).toHaveLength(1);
      expect(screen.queryByText('Decision recorded')).toBeNull();
      if (status === 403) {
        expect(screen.getByText('Revision: 7')).toBeTruthy();
        expect(screen.getByText('Technical-review decision: No current decision')).toBeTruthy();
        expect(screen.queryByText('finding-2')).toBeNull();
        expect(calls.filter(({ url }) => url.endsWith('/dashboard'))).toHaveLength(1);
      } else {
        // A stale revision is not applied locally; the server projections are read again.
        await waitFor(() => expect(calls.filter(({ url }) => url.endsWith('/dashboard'))).toHaveLength(2));
        expect(screen.getByRole('alert').textContent).toBe('The case changed. Refresh the case before deciding.');
      }
    }
  });
});
