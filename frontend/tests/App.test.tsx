import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

const users = [{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }];
const signedIn = { id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' };
const decisions = [{ id: 'accept', label: 'Accept finding' }, { id: 'reject', label: 'Reject finding' }];

function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

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
} = {}) {
  const calls: { url: string; init?: RequestInit }[] = [];
  let dashboardRequests = 0;
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({ url, init });
    if (url === '/api/health') return json({ status: 'ok' }, options.healthStatus ?? 200);
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json(users);
    if (url === '/api/auth/sign-in') return json(signedIn);
    if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
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
  await waitFor(() => expect(screen.queryByText('Revision: 7') || screen.queryByRole('alert')).toBeTruthy());
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('reviewer workspace', () => {
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
    expect(screen.getByRole('region', { name: /^Selection SYN-CP-/ })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Case A guided journey' })).toBeTruthy();
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
