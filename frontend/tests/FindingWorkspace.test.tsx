import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import App from '../src/App';
import FindingWorkspace from '../src/workspaces/FindingWorkspace';
import type { WorklistItem } from '../src/workspaces/TechnicalWorkspace';

function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

const worklist: WorklistItem[] = [
  { caseId: 'case-001', itemId: 'finding-1', capabilityId: 'technical-review', state: 'open', owner: 'Ada', ageSeconds: 300, label: 'Human decision' },
  { caseId: 'case-002', itemId: 'finding-2', capabilityId: 'technical-review', state: 'open', owner: 'Ada', ageSeconds: 200, label: 'Human decision' },
  { caseId: 'case-002', itemId: 'finding-3', capabilityId: 'technical-review', state: 'open', owner: 'Ada', ageSeconds: 100, label: 'Human decision' },
];
const decisions = [{ id: 'accept', label: 'Accept finding' }, { id: 'reject', label: 'Reject finding' }];
const session = { id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001', 'case-002'], csrfToken: 'csrf-session-token' };

beforeAll(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/');
});

// A server that owns the revision: a POST with another revision is rejected and changes nothing.
function setupServer(options: { postStatus?: number; allowed?: typeof decisions; worklistGate?: Promise<void> } = {}) {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  const calls: { url: string; init?: RequestInit }[] = [];
  let worklistReads = 0;
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({ url, init });
    if (url === '/api/health') return json({ status: 'ok' });
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json([{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }]);
    if (url === '/api/auth/sign-in') return json(session);
    if (url === '/api/auth/sign-out') return new Response(null, { status: 204 });
    if (url === '/api/v1/workspace/worklist') {
      worklistReads += 1;
      if (options.worklistGate && worklistReads === 1) {
        await options.worklistGate;
        return json({ contractVersion: 1, items: [{ ...worklist[0], itemId: 'stale-item' }], unavailableCaseIds: [], truncated: false });
      }
      return json({ contractVersion: 1, items: worklist, unavailableCaseIds: [], truncated: false });
    }
    const caseId = url.match(/^\/api\/cases\/([^/]+)/)?.[1];
    if (caseId && url === `/api/cases/${caseId}`) return json({ revision: 7, basis: [{ version: 4 }] });
    if (caseId && url.endsWith('/dashboard')) return json({
      allowedDecisions: options.allowed ?? decisions,
      capabilities: [{ id: 'records-investigation', name: 'Records investigation', label: 'AI agent', designOnly: false, openItems: 1, nextDecisionOwner: null, evidence: [{ label: 'Maintenance log 14', url: `/api/cases/${caseId}/dashboard/traces/t1` }], design: null }],
      queue: [], activity: [],
    });
    if (caseId && url.endsWith('/readiness')) return json({ monthsUntilReturn: 9, gapsFound: 0, requestsSent: 0, heldForReview: 0, notProcessed: 0, sentence: 'Return in 9 months.' });
    if (caseId && url.endsWith('/technical-reviews') && method === 'GET') return json({ basisVersion: 4, decisions: [] });
    if (caseId && url.endsWith('/technical-reviews') && method === 'POST') return options.postStatus ? json({ error: 'rejected' }, options.postStatus) : json({ decision: 'accept', current: true });
    return json({ error: 'not found' }, 404);
  }));
  return { calls, worklistReads: () => worklistReads };
}

async function signInAsReviewer() {
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
  await waitFor(() => expect((screen.getByRole('button', { name: 'Sign in' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Signed in as Ada Reviewer.');
}

async function openFinding(itemId: string) {
  fireEvent.click(await screen.findByText(itemId));
  await screen.findByRole('heading', { name: 'Finding workspace' });
  await screen.findByLabelText('Decision panel');
}

const panel = () => within(screen.getByLabelText('Decision panel'));

describe('technical finding workspace', () => {
  it('technical_finding_split_and_worklist_navigation', () => {
    const onNavigate = vi.fn();
    const evidence = [{ id: 'e1', label: 'Records investigation', text: 'Maintenance log 14' }];
    const view = (caseId: string | null, itemId: string | null) => (
      <FindingWorkspace worklist={worklist} caseId={caseId} itemId={itemId} onNavigate={onNavigate}
        findings={{ title: 'Finding', summary: 'Summary', status: 'Awaiting review', evidence }} allowedDecisions={decisions}
        decisionReason="" onDecisionReasonChange={() => {}} />
    );
    const { rerender } = render(view('case-002', 'finding-2'));

    // Left: evidence list and request status. Right: finding and decision panel.
    const left = screen.getByRole('heading', { name: 'Evidence' }).parentElement!;
    expect(within(left).getByText('Maintenance log 14')).toBeTruthy();
    expect(within(left).getByRole('heading', { name: 'Request status' })).toBeTruthy();
    expect(within(left).getByText('Awaiting review')).toBeTruthy();
    const right = screen.getByRole('heading', { name: 'Finding' }).parentElement!;
    expect(within(right).getByLabelText('Allowed decisions')).toBeTruthy();
    expect(within(left).queryByLabelText('Allowed decisions')).toBeNull();

    // Middle of the worklist crosses cases in both directions.
    fireEvent.click(screen.getByRole('button', { name: 'Previous' }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onNavigate.mock.calls).toEqual([['previous'], ['next']]);

    // Boundaries drop the corresponding action.
    rerender(view('case-001', 'finding-1'));
    expect(screen.queryByRole('button', { name: 'Previous' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Next' })).toBeTruthy();
    rerender(view('case-002', 'finding-3'));
    expect(screen.getByRole('button', { name: 'Previous' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Next' })).toBeNull();

    // A case outside the server array, an item outside it, or no case at all offers no navigation.
    for (const [caseId, itemId] of [['case-999', 'finding-1'], ['case-001', 'finding-9'], [null, null]] as const) {
      rerender(view(caseId, itemId));
      expect(screen.queryByRole('button', { name: 'Previous' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Next' })).toBeNull();
    }
  });

  it('AC_009_technical_decision_contract', async () => {
    const { calls, worklistReads } = setupServer();
    render(<App />);
    await signInAsReviewer();
    await openFinding('finding-1');
    const readsBefore = worklistReads();

    fireEvent.change(panel().getByLabelText('Reason for decision'), { target: { value: 'Evidence reviewed.' } });
    fireEvent.click(within(screen.getByLabelText('Allowed decisions')).getByRole('button', { name: 'Accept finding' }));

    const toast = await screen.findByText('Decision recorded');
    expect(toast.getAttribute('role')).toBe('status');
    const post = calls.find(({ url, init }) => url === '/api/cases/case-001/technical-reviews' && init?.method === 'POST')!;
    expect((post.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-session-token');
    expect(JSON.parse(String(post.init?.body))).toEqual({ expectedRevision: 7, expectedBasisVersion: 4, decision: 'accept', reason: 'Evidence reviewed.' });
    // The worklist is read again from the server after the decision.
    await waitFor(() => expect(worklistReads()).toBeGreaterThan(readsBefore));
    // Exactly the allowed decisions are offered.
    expect(within(screen.getByLabelText('Allowed decisions')).getAllByRole('button').map((b) => b.textContent)).toEqual(['Accept finding', 'Reject finding']);
  });

  it('AC_009_technical_decision_contract rejects without a success toast or a decision form', async () => {
    const empty = render(<FindingWorkspace worklist={worklist} caseId="case-001" itemId="finding-1" allowedDecisions={[]}
      decisionReason="" onDecisionReasonChange={() => {}} onDecisionSubmit={vi.fn()} />);
    expect(empty.container.querySelector('textarea')).toBeNull();
    expect(screen.queryByLabelText('Allowed decisions')).toBeNull();
    cleanup();

    const rejected = vi.fn(async () => false);
    render(<FindingWorkspace worklist={worklist} caseId="case-001" itemId="finding-1" allowedDecisions={decisions}
      decisionReason="Because." onDecisionReasonChange={() => {}} onDecisionSubmit={rejected} />);
    fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));
    await waitFor(() => expect(rejected).toHaveBeenCalledWith('accept', 'Because.'));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('technical_stale_revision_refresh_contract', async () => {
    const { calls, worklistReads } = setupServer({ postStatus: 409 });
    render(<App />);
    await signInAsReviewer();
    await openFinding('finding-1');
    const readsBefore = worklistReads();
    const dashboardsBefore = calls.filter(({ url }) => url === '/api/cases/case-001/dashboard').length;

    fireEvent.change(panel().getByLabelText('Reason for decision'), { target: { value: 'Evidence reviewed.' } });
    fireEvent.click(panel().getByRole('button', { name: 'Accept finding' }));

    expect((await screen.findByRole('alert')).textContent).toBe('The case changed. Refresh the case before deciding.');
    // Server state is read again; nothing is applied locally and the queue does not advance.
    await waitFor(() => expect(calls.filter(({ url }) => url === '/api/cases/case-001/dashboard').length).toBeGreaterThan(dashboardsBefore));
    await waitFor(() => expect(worklistReads()).toBeGreaterThan(readsBefore));
    expect(calls.filter(({ init }) => init?.method === 'POST' && String(init.body).includes('decision'))).toHaveLength(1);
    expect(screen.queryByText('Decision recorded')).toBeNull();
    expect(screen.getByText('Case case-001: finding-1')).toBeTruthy();
  });

  it('worklist_response_after_sign_out_is_discarded', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    setupServer({ worklistGate: gate });
    render(<App />);
    await signInAsReviewer();
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByLabelText('Demo passcode');
    // The next reviewer signs in while the first reviewer's worklist request is still pending.
    await signInAsReviewer();
    await screen.findByText('finding-1');
    release();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByText('stale-item')).toBeNull();
    expect(screen.getByText('finding-1')).toBeTruthy();
  });

  it('AI_trace_and_finding_text_remains_inert', () => {
    const hostile = ['<b>x</b>', '<img src=x onerror=alert(1)>', 'javascript:alert(1)'];
    const { container } = render(
      <FindingWorkspace worklist={worklist} caseId="case-001" itemId="finding-1"
        findings={{ title: hostile[0], summary: hostile[1], status: hostile[2], evidence: hostile.map((text, index) => ({ id: `h${index}`, label: hostile[(index + 1) % 3], text })) }}
        allowedDecisions={[{ id: 'accept', label: hostile[0] }]} decisionReason="" onDecisionReasonChange={() => {}} />,
    );
    for (const text of hostile) expect(screen.getAllByText(text).length).toBeGreaterThan(0);
    expect(container.querySelector('b, img, a, [href], [src], [role="link"]')).toBeNull();
    expect(container.innerHTML).not.toContain('<img');
  });
});
