import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import App from '../src/App';
import ReserveWorksheet from '../src/workspaces/ReserveWorksheet';

function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }

const financeSession = { id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-finance' };
const approveReturn = [{ id: 'approve', label: 'Approve reserve' }, { id: 'return', label: 'Return reserve' }];

beforeAll(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/');
});

const tableRows = () => screen.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell').map((cell) => cell.textContent));

describe('reserve worksheet', () => {
  it('AC_010_reserve_worksheet_contract', () => {
    const { rerender } = render(
      <ReserveWorksheet
        draft={{
          status: 'Blocked',
          lines: [
            { input: 'Pax frequency', value: null, source: 'source-a', freshness: '2 days ago', status: 'blocked' },
            { input: 'Rate', value: 12.3, source: 'source-b', freshness: '6 hours ago', status: 'stale' },
            { input: 'Amount', value: 0, source: 'source-c', freshness: 'fresh', status: 'ready' },
          ],
        }}
      />,
    );

    expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(['Input', 'Value', 'Source', 'Freshness', 'Status']);
    // Every projected value sits in its own row and column; a missing value is "Not available", a real zero stays "0".
    expect(tableRows()).toEqual([
      ['Pax frequency', 'Not available', 'source-a', '2 days ago', 'Blocked'],
      ['Rate', '12.3', 'source-b', '6 hours ago', 'Stale'],
      ['Amount', '0', 'source-c', 'fresh', 'ready'],
    ]);
    expect(screen.getByText('Draft only — no invoice is issued.')).toBeTruthy();

    // The banner is permanent, including with no draft and no rows.
    rerender(<ReserveWorksheet draft={null} />);
    expect(screen.getByText('Draft only — no invoice is issued.')).toBeTruthy();
    expect(screen.getByText('No reserve inputs available.')).toBeTruthy();
  });

  it('reserve_decision_panel_offers_only_server_allowed_decisions', () => {
    const onDecision = vi.fn();
    const onDecisionReasonChange = vi.fn();
    const draft = (allowedDecisions: Array<{ id: string; label: string }>) => (
      <ReserveWorksheet draft={{ status: 'Ready', lines: [], allowedDecisions }} decisionReason="" onDecisionReasonChange={onDecisionReasonChange} onDecision={onDecision} />
    );
    const { rerender } = render(draft(approveReturn));
    fireEvent.change(screen.getByLabelText('Reason for decision', { selector: 'textarea' }), { target: { value: 'Ready to review' } });
    expect(onDecisionReasonChange).toHaveBeenCalledTimes(1);
    expect(onDecisionReasonChange).toHaveBeenCalledWith('Ready to review');
    fireEvent.click(screen.getByRole('button', { name: 'Return reserve' }));
    expect(onDecision).toHaveBeenCalledWith('return');

    rerender(draft([approveReturn[0]]));
    expect(screen.getByRole('button', { name: 'Approve reserve' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Return reserve' })).toBeNull();

    rerender(draft([]));
    expect(screen.queryByRole('group', { name: 'Reserve decisions' })).toBeNull();
    expect(screen.queryByLabelText('Reason for decision')).toBeNull();
  });

  it('reserve_text_and_status_remain_inert', () => {
    const hostile = ['<b>x</b>', '"><img src=x onerror=alert(1)>', 'javascript:alert(1)', 'x" style="color:red', '<script>evil()</script>', 'https://example.test/ok'];
    const { container } = render(
      <ReserveWorksheet
        draft={{
          status: hostile[0],
          payableTotal: 1,
          discrepancies: [hostile[1]],
          blockers: [{ code: hostile[2], detail: hostile[3] }],
          lines: [
            { input: hostile[0], value: hostile[4], source: hostile[5], freshness: hostile[3], status: hostile[2] },
            { input: hostile[1], value: hostile[2], source: hostile[0], freshness: hostile[4], status: 'stale' },
          ],
          allowedDecisions: [{ id: hostile[3], label: hostile[1] }],
        }}
        decisionReason=""
        onDecisionReasonChange={() => {}}
      />,
    );

    for (const text of hostile) expect(screen.getAllByText(text).length).toBeGreaterThan(0);
    expect(container.querySelector('b, img, script, a, [href], [src], [action], [style], [onerror], [formaction]')).toBeNull();
    // No server string reaches any attribute of any element.
    for (const element of Array.from(container.querySelectorAll('*'))) {
      for (const attribute of Array.from(element.attributes)) {
        for (const text of hostile) expect(attribute.value).not.toContain(text);
      }
    }
    expect(screen.getAllByText('Stale')).toHaveLength(1);
  });
});

// A server that owns the case revision (7) and the reserve draft; the draft itself carries no revision.
function setupFinanceServer(options: { decisionStatus?: number; allowedDecisions?: typeof approveReturn; decisionGate?: Promise<void> } = {}) {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  const calls: { url: string; init?: RequestInit }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({ url, init });
    if (url === '/api/health') return json({ status: 'ok' });
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json([{ id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer' }]);
    if (url === '/api/auth/sign-in') return json(financeSession);
    if (url === '/api/v1/workspace/worklist') return json({ items: [{ caseId: 'case-001', itemId: 'reserve-1', capabilityId: 'reserve', state: 'Blocked', owner: 'finance-reviewer', ageSeconds: 300, label: 'Reserve case 001' }] });
    if (url === '/api/cases/case-001') return json({ revision: 7, basis: [{ version: 1 }] });
    if (url === '/api/cases/case-001/reserve' && method === 'GET') {
      return json({ version: 1, status: 'Ready', lines: [{ input: 'Pax frequency', value: null, source: 'usage', freshness: 'fresh', status: 'blocked' }], allowedDecisions: options.allowedDecisions ?? approveReturn });
    }
    if (url === '/api/cases/case-001/reserve/decisions' && method === 'POST') {
      if (options.decisionGate) await options.decisionGate;
      return options.decisionStatus ? json({ error: 'rejected' }, options.decisionStatus) : json({ decision: 'approve', revision: 8, simulated: true, invoiceIssued: false });
    }
    return json({ error: 'not found' }, 404);
  }));
  const count = (url: string, verb = 'GET') => calls.filter((call) => call.url === url && (call.init?.method ?? 'GET') === verb).length;
  return { calls, count };
}

async function signInAsFinance() {
  render(<App />);
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
  await waitFor(() => expect((screen.getByRole('button', { name: 'Sign in' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Signed in as Finance reviewer.');
  await screen.findByLabelText('Reason for decision');
}

async function decide(label: string) {
  fireEvent.change(screen.getByLabelText('Reason for decision'), { target: { value: 'Ready to review' } });
  fireEvent.click(screen.getByRole('button', { name: label }));
}

describe('finance reserve decision', () => {
  it('finance_reserve_decision_contract', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const { calls, count } = setupFinanceServer({ decisionGate: gate });
    await signInAsFinance();
    const reserveBefore = count('/api/cases/case-001/reserve');
    const worklistBefore = count('/api/v1/workspace/worklist');

    await decide('Approve reserve');
    // Success is not shown before the server has answered.
    await waitFor(() => expect(count('/api/cases/case-001/reserve/decisions', 'POST')).toBe(1));
    expect(screen.queryByText('Decision recorded')).toBeNull();
    release();

    const toast = await screen.findByText('Decision recorded');
    expect(toast.getAttribute('role')).toBe('status');
    const post = calls.find(({ url, init }) => url === '/api/cases/case-001/reserve/decisions' && init?.method === 'POST')!;
    expect((post.init?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-finance');
    // The case revision (7) is sent, not the reserve draft's own version (1).
    expect(JSON.parse(String(post.init?.body))).toEqual({ expectedRevision: 7, decision: 'approve', reason: 'Ready to review' });
    // Reserve and worklist projections are read again.
    await waitFor(() => expect(count('/api/cases/case-001/reserve')).toBeGreaterThan(reserveBefore));
    await waitFor(() => expect(count('/api/v1/workspace/worklist')).toBeGreaterThan(worklistBefore));
  });

  it('finance_reserve_decision_contract withholds the form when the server allows no decision', async () => {
    const { count } = setupFinanceServer({ allowedDecisions: [] });
    render(<App />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Finance reviewer (finance-reviewer)' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Sign in' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Pax frequency');
    expect(screen.queryByLabelText('Reason for decision')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Approve reserve' })).toBeNull();
    expect(count('/api/cases/case-001/reserve/decisions', 'POST')).toBe(0);
  });

  it('finance_reserve_decision_contract forbidden response shows no success and changes nothing', async () => {
    const { count } = setupFinanceServer({ decisionStatus: 403 });
    await signInAsFinance();
    const reserveBefore = count('/api/cases/case-001/reserve');
    await decide('Approve reserve');
    expect((await screen.findByText('You are not permitted to record this decision.')).getAttribute('role')).toBe('alert');
    expect(screen.queryByText('Decision recorded')).toBeNull();
    expect(count('/api/cases/case-001/reserve')).toBe(reserveBefore);
  });

  it('finance_reserve_decision_contract stale revision re-reads the server and shows no success', async () => {
    const { count } = setupFinanceServer({ decisionStatus: 409 });
    await signInAsFinance();
    const reserveBefore = count('/api/cases/case-001/reserve');
    const worklistBefore = count('/api/v1/workspace/worklist');
    await decide('Approve reserve');
    expect((await screen.findByText('The case changed. Refresh the case before deciding.')).getAttribute('role')).toBe('alert');
    await waitFor(() => expect(count('/api/cases/case-001/reserve')).toBeGreaterThan(reserveBefore));
    await waitFor(() => expect(count('/api/v1/workspace/worklist')).toBeGreaterThan(worklistBefore));
    expect(screen.queryByText('Decision recorded')).toBeNull();
    expect(count('/api/cases/case-001/reserve/decisions', 'POST')).toBe(1);
  });
});
