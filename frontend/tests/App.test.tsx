import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

const users = [{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }];
const signedIn = { id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-session-token' };
const decisions = [{ id: 'accept', label: 'Accept finding' }, { id: 'reject', label: 'Reject finding' }];

function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

function dashboard(queue: unknown[] = [], activity: unknown[] = [], allowed = decisions) {
  return {
    allowedDecisions: allowed,
    capabilities: [{
      id: 'records-investigation', name: 'Records investigation', label: 'AI agent', designOnly: false,
      openItems: 0, nextDecisionOwner: null, evidence: [], design: null,
      caseStudyAutonomy: 'L4, autonomous', demoAutonomy: 'L3 demo',
    }],
    queue,
    activity,
  };
}

function setupApi(options: { healthStatus?: number; dashboardStatus?: number; decisionStatus?: number; allowed?: typeof decisions } = {}) {
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
        ? dashboard([], [], options.allowed)
        : dashboard(
          [{ itemId: 'finding-2', label: 'Human decision' }],
          [{ sequence: 1, actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' }],
          options.allowed,
        ));
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
