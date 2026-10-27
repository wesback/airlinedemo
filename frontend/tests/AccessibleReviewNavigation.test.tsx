import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';
import CapabilityDashboard, { type DashboardData } from '../src/CapabilityDashboard';
import ReadinessSummary from '../src/ReadinessSummary';

const users = [{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }];
const signedIn = { id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-token' };
const decisions = [{ id: 'accept', label: 'Accept finding' }, { id: 'reject', label: 'Reject finding' }];
const traceUrl = '/api/traces/one';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

function setupApi(options: {
  health?: Response | Promise<Response>;
  caseResponse?: Response | Promise<Response>;
  dashboardStatus?: number;
  allowed?: typeof decisions;
  traces?: { label: string; url: string }[];
  traceResponses?: Record<string, Response | Promise<Response>>;
} = {}) {
  let dashboardRequests = 0;
  let caseRequests = 0;
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    if (url === '/api/health') return options.health ?? json({ status: 'ok' });
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json(users);
    if (url === '/api/auth/sign-in') return json(signedIn);
    if (url === '/api/cases/case-001' && method === 'GET') {
      caseRequests += 1;
      return caseRequests === 1
        ? options.caseResponse ?? json({ revision: 7, basis: [{ version: 4 }] })
        : json({ revision: 8, basis: [{ version: 4 }] });
    }
    if (url === '/api/cases/case-001/dashboard' && method === 'GET') {
      dashboardRequests += 1;
      if (options.dashboardStatus) return json({}, options.dashboardStatus);
      return json(dashboardRequests === 1
        ? { allowedDecisions: options.allowed ?? decisions, capabilities: [{
          id: 'records-investigation', name: 'Records investigation', label: 'AI agent', designOnly: false,
          openItems: 0, nextDecisionOwner: null, evidence: options.traces ?? [{ label: 'Investigation trace', url: traceUrl }],
          design: null, caseStudyAutonomy: 'L4', demoAutonomy: 'L3',
        }, {
          id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false,
          openItems: 1, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null,
        }], queue: [], activity: [] }
        : { allowedDecisions: options.allowed ?? decisions, capabilities: [{
          id: 'records-investigation', name: 'Records investigation', label: 'AI agent', designOnly: false,
          openItems: 0, nextDecisionOwner: null, evidence: options.traces ?? [{ label: 'Investigation trace', url: traceUrl }],
          design: null,
        }, {
          id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false,
          openItems: 1, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null,
        }], queue: [{ itemId: 'finding-2', label: 'Human decision' }],
        activity: [{ sequence: 1, actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' }] });
    }
    if (url === '/api/cases/case-001/technical-reviews' && method === 'GET') {
      return json({ basisVersion: 4, decisions: dashboardRequests > 1 ? [{ decision: 'accept', current: true }] : [] });
    }
    if (url === '/api/cases/case-001/readiness') {
      return json({ monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2, sentence: 'Return in 9 months.' });
    }
    if (url === '/api/cases/case-001/technical-reviews' && method === 'POST') return json({ decision: 'accept' });
    if (url in (options.traceResponses ?? {})) return options.traceResponses![url];
    throw new Error(`Unexpected request: ${method} ${url}`);
  }));
}

async function signIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Demo passcode'), 'demo-pass');
  await user.click(screen.getByRole('button', { name: 'Continue' }));
  await user.click(await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Signed in as Ada Reviewer.');
}

function expectStatus(text: string) {
  const element = screen.getByText(text, { exact: true });
  expect(element.getAttribute('role')).toBe('status');
  expect(element.textContent).toBe(text);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('accessible review navigation', () => {
  it('AccessibleReviewNavigationTest.KeyboardFlowFollowsDocumentOrderAndCanLeaveReviewControls', async () => {
    setupApi();
    const user = userEvent.setup();
    render(<App />);
    await signIn(user);
    await screen.findByLabelText('Reason for decision');

    const focusOrder: string[] = [];
    for (let step = 0; step < 9; step += 1) {
      await user.tab();
      const active = document.activeElement as HTMLElement;
      focusOrder.push(active.id === 'decision-reason'
        ? 'Reason for decision'
        : active.getAttribute('aria-label') ?? active.textContent?.trim() ?? '');
    }
    expect(focusOrder).toEqual([
      'Sign out', 'Enter Hangar', 'Case A, narrow-body', 'Case B, regional',
      'Case C, wide-body', 'Investigation trace', 'Reason for decision',
      'Accept finding', 'Reject finding',
    ]);
    const lastDecision = screen.getByRole('button', { name: 'Reject finding' });
    await user.tab();
    expect(document.activeElement).not.toBe(lastDecision);
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Sign out' }));
  });

  it('AccessibleReviewNavigationTest.ControlsAndStatusUpdatesExposeAccessibleText', async () => {
    const health = deferred<Response>();
    const caseLoad = deferred<Response>();
    const traceLoad = deferred<Response>();
    const errorUrl = '/api/traces/error';
    const missingUrl = '/api/traces/missing';
    setupApi({
      health: health.promise,
      caseResponse: caseLoad.promise,
      traces: [
        { label: 'Investigation trace', url: traceUrl },
        { label: 'Error trace', url: errorUrl },
        { label: 'Missing trace', url: missingUrl },
      ],
      traceResponses: {
        [traceUrl]: traceLoad.promise,
        [errorUrl]: json({}, 500),
        [missingUrl]: json({}, 404),
      },
    });
    const user = userEvent.setup();
    render(<App />);
    expectStatus('Checking service connection…');
    health.resolve(json({ status: 'ok' }));
    await screen.findByText('Service connected');
    expectStatus('Service connected');
    await user.type(screen.getByLabelText('Demo passcode'), 'demo-pass');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Loading case data…');
    expectStatus('Loading case data…');
    caseLoad.resolve(json({ revision: 7, basis: [{ version: 4 }] }));
    await screen.findByLabelText('Reason for decision');

    expect(screen.getByRole('link', { name: 'Investigation trace' })).toBeTruthy();
    expect(screen.getByRole('textbox', { name: 'Reason for decision' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Accept finding' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reject finding' })).toBeTruthy();
    await user.click(screen.getByRole('link', { name: 'Investigation trace' }));
    expectStatus('Loading investigation trace…');
    traceLoad.resolve(json({ steps: [] }));
    await screen.findByRole('list', { name: 'Investigation trace steps' });
    await user.click(screen.getByRole('link', { name: 'Missing trace' }));
    expectStatus('No trace recorded');
    await user.click(screen.getByRole('link', { name: 'Error trace' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Unable to load investigation trace.');

    await user.type(screen.getByRole('textbox', { name: 'Reason for decision' }), 'Evidence supports return.');
    await user.click(screen.getByRole('button', { name: 'Accept finding' }));
    await screen.findByText('finding-2');
    const queueStatus = screen.getByRole('status', { name: 'Queue updates' });
    const activityStatus = screen.getByRole('status', { name: 'Activity updates' });
    expect(queueStatus.textContent).toBe('Human decision finding-2');
    expect(within(queueStatus).getByRole('listitem').textContent).toBe('Human decision finding-2');
    expect(activityStatus.textContent).toBe('Human decision Ada Reviewer: technical-review.decided');
    expect(within(activityStatus).getByRole('listitem').textContent).toBe('Human decision Ada Reviewer: technical-review.decided');

    cleanup();
    setupApi({ allowed: [] });
    render(<App />);
    await signIn(user);
    const reviewCard = screen.getByRole('article', { name: 'Technical review' });
    expect(within(reviewCard).queryByRole('button')).toBeNull();

    cleanup();
    setupApi({ dashboardStatus: 503 });
    render(<App />);
    await signIn(user);
    expect((await screen.findByRole('alert')).textContent).toBe('Service temporarily unavailable Retry');

    cleanup();
    setupApi({ health: json({}, 503) });
    render(<App />);
    await screen.findByText('Service unavailable. Try again later.');
    expectStatus('Service unavailable. Try again later.');
  });

  it('AccessibleReviewNavigationTest.PreservesSummaryAndDesignOnlyText', () => {
    const data: DashboardData = {
      allowedDecisions: [],
      capabilities: [{
        id: 'design-only', name: 'Design-only card', label: 'Design only', designOnly: true,
        openItems: null, nextDecisionOwner: null, evidence: [],
        design: { purpose: 'Demonstration', intendedAutonomy: 'None', humanAuthority: 'Human', notice: 'Not implemented in this demo' },
      }, {
        id: 'second-design-only', name: 'Second design-only card', label: 'Design only', designOnly: true,
        openItems: null, nextDecisionOwner: null, evidence: [],
        design: { purpose: 'Demonstration', intendedAutonomy: 'None', humanAuthority: 'Human', notice: 'Not implemented in this demo' },
      }],
      queue: [], activity: [],
    };
    render(
      <>
        <ReadinessSummary summary={{ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' }} />
        <CapabilityDashboard data={data} />
      </>,
    );

    expectStatus('Return in 9 months.');
    for (const card of screen.getAllByRole('article')) {
      expect(within(card).getByText('Not implemented in this demo', { exact: true }).textContent)
        .toBe('Not implemented in this demo');
    }
  });
});
