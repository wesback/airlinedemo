import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

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
    || screen.queryByRole('alert')
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
    window.history.replaceState({}, '', '/cases/case-001/reserve?returnTo=%2Fcases%2Fcase-001%2Freserve');
    render(<App />);

    expect(screen.queryByRole('navigation', { name: 'Primary' })).toBeNull();
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Finance reviewer.');
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Finance reviewer' })).toBeTruthy();
    expect(window.location.pathname).toBe('/cases/case-001/reserve');
    expect(validCalls.some(({ url }) => url === '/api/auth/sign-in')).toBe(true);

    cleanup();
    const invalidCalls = setupApi({
      session: financeSession,
      users: financeUsers,
    });
    window.history.replaceState({}, '', '/work?returnTo=%2Fcases%2Fcase-001%2Ffindings');
    render(<App />);

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Signed in as Finance reviewer.');
    expect(window.location.pathname).toBe('/work');
    expect(invalidCalls.some(({ url }) => url === '/api/auth/sign-in')).toBe(true);
  });

  it('AC_001_App_root_shell_and_Hangar_composition', async () => {
    setupApi({ canReset: true });
    render(<App hangarBrowserSupported={true} hangarWebGl2Probe={{ webgl2: true }} hangarLazyLoader={() => { function Scene() { return <canvas aria-label="3D hangar scene" />; } return Scene; }} />);
    await signIn();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    expect(screen.getByRole('heading', { name: 'Hangar' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'View Case A' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Overview' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Hangar object selection' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Case A guided journey' })).toBeTruthy();
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

    for (const status of [401, 404]) {
      cleanup();
      setupApi({ dashboardStatus: status });
      render(<App />);
      await signIn();
      expect(await screen.findByRole('alert')).toBeTruthy();
      expect(screen.queryByText('Revision: 7')).toBeNull();
      expect(screen.queryByRole('heading', { name: 'Records investigation' })).toBeNull();
    }
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
      expect(screen.getByText('Revision: 7')).toBeTruthy();
      expect(screen.getByText('Technical-review decision: No current decision')).toBeTruthy();
      expect(screen.queryByText('finding-2')).toBeNull();
      expect(calls.filter(({ url }) => url.endsWith('/dashboard'))).toHaveLength(1);
    }
  });
});
