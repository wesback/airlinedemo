import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import CopilotPanel from '../src/journey/CopilotPanel';

const presenter = { id: 'presenter', displayName: 'Demo Presenter', role: 'presenter', caseScopes: ['cp-case-c'], csrfToken: 'csrf-p' };
const proposalId = '7b0c2a52-5d1e-4b39-9c1f-1d2e3f405162';
const decisionUrl = `/api/v1/cases/cp-case-c/copilot/proposals/${proposalId}/decision`;
const advanceUrl = '/api/v1/cases/cp-case-c/copilot/advance';
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const confirmation = {
  contractVersion: 1, proposalId, caseId: 'cp-case-c', aircraftId: 'MSN-4471', action: 'final-release', expectedRevision: 7,
  note: 'Ready <b>now</b>', createdAt: '2026-10-31T12:00:00+00:00', expiresAt: '2026-10-31T12:05:00+00:00', state: 'pending', simulated: true,
  warning: 'Simulated release: no lessee or external system is contacted.',
  controls: { decision: decisionUrl, advance: advanceUrl },
};
const answer = { contractVersion: 1, answer: 'A release proposal is waiting for your confirmation. Nothing has changed yet.', citations: [], records: [], confirmation };
const receipt = { contractVersion: 1, proposalId, caseId: 'cp-case-c', decision: 'released', revision: 8, auditSequence: 12, simulated: true };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function stub(routes: Record<string, Response[]>) {
  const fetcher = vi.fn(async (url: string, _init?: RequestInit) => {
    const next = routes[url]?.shift();
    if (!next) throw new Error(`unexpected request ${url}`);
    return next;
  });
  vi.stubGlobal('fetch', fetcher);
  return fetcher;
}

async function ask() {
  render(<CopilotPanel session={presenter} caseId="cp-case-c" />);
  fireEvent.change(screen.getByRole('textbox', { name: 'Your question' }), { target: { value: 'Release it' } });
  fireEvent.change(screen.getByRole('textbox', { name: 'Release note (optional, presenter only)' }), { target: { value: ' Ready <b>now</b> ' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  return screen.findByRole('region', { name: 'Confirm release proposal' });
}

const sent = (fetcher: ReturnType<typeof stub>, index: number) => {
  const [url, init] = fetcher.mock.calls[index];
  expect(init?.method).toBe('POST');
  expect(init?.headers).toEqual({ 'Content-Type': 'application/json', 'X-CSRF-Token': 'csrf-p' });
  return [url, init?.body];
};

it('CopilotConfirmation_ExplicitControlsAndExactRetry', async () => {
  let fetcher = stub({
    '/api/v1/copilot/questions': [json(answer)],
    [decisionUrl]: [json({ contractVersion: 1, proposalId, caseId: 'cp-case-c', state: 'approved', expiresAt: confirmation.expiresAt })],
    [advanceUrl]: [json(receipt)],
  });
  let card = await ask();
  expect(within(card).getByText(confirmation.warning)).toBeTruthy();
  const terms = [...card.querySelectorAll('dt')].map((item) => item.textContent);
  const values = [...card.querySelectorAll('dd')].map((item) => item.textContent);
  expect(terms).toEqual(['Aircraft', 'Action', 'Expected revision', 'Release note', 'Expires']);
  expect(values).toEqual(['MSN-4471', 'Final simulated release', '7', 'Ready <b>now</b>', '2026-10-31T12:05:00+00:00']);
  expect(card.querySelector('b')).toBeNull();
  expect(within(card).getAllByRole('button').map((button) => button.textContent)).toEqual(['Approve', 'Decline', 'Cancel']);
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(sent(fetcher, 0)).toEqual(['/api/v1/copilot/questions',
    JSON.stringify({ question: 'Release it', selectedCaseId: 'cp-case-c', releaseNote: ' Ready <b>now</b> ' })]);

  fireEvent.click(within(card).getByRole('button', { name: 'Approve' }));
  const release = await within(card).findByRole('button', { name: 'Release now' });
  expect(sent(fetcher, 1)).toEqual([decisionUrl, JSON.stringify({ decision: 'approve' })]);
  expect(within(card).getByRole('status').textContent).toBe('Proposal approved. Nothing is released yet.');
  expect(within(card).queryByRole('button', { name: 'Approve' })).toBeNull();
  expect(fetcher).toHaveBeenCalledTimes(2);
  fireEvent.click(release);
  expect(await within(card).findByText(`Released (simulated). Revision 8, audit entry 12, proposal ${proposalId}.`)).toBeTruthy();
  expect(sent(fetcher, 2)).toEqual([advanceUrl, JSON.stringify({ expectedRevision: 7, releaseNote: ' Ready <b>now</b> ', proposalId })]);
  expect(fetcher.mock.calls.filter(([url]) => url === '/api/v1/copilot/questions')).toHaveLength(1);
  expect(within(card).queryAllByRole('button')).toHaveLength(0);
  cleanup();

  fetcher = stub({
    '/api/v1/copilot/questions': [json(answer)],
    [decisionUrl]: [json({ contractVersion: 1, proposalId, caseId: 'cp-case-c', state: 'declined', expiresAt: confirmation.expiresAt })],
  });
  card = await ask();
  fireEvent.click(within(card).getByRole('button', { name: 'Decline' }));
  expect(await within(card).findByText('You declined this release. Nothing changed.')).toBeTruthy();
  expect(sent(fetcher, 1)).toEqual([decisionUrl, JSON.stringify({ decision: 'decline' })]);
  expect(within(card).queryAllByRole('button')).toHaveLength(0);
  expect(fetcher).toHaveBeenCalledTimes(2);
  cleanup();

  fetcher = stub({
    '/api/v1/copilot/questions': [json(answer)],
    [decisionUrl]: [json({ contractVersion: 1, error: { code: 'confirmation_expired' } }, 410)],
  });
  card = await ask();
  fireEvent.click(within(card).getByRole('button', { name: 'Approve' }));
  expect((await within(card).findByRole('alert')).textContent).toBe('This confirmation has expired. Ask again to create a new proposal.');
  expect(within(card).queryAllByRole('button')).toHaveLength(0);
  expect(fetcher).toHaveBeenCalledTimes(2);
  cleanup();

  fetcher = stub({
    '/api/v1/copilot/questions': [json(answer)],
    [decisionUrl]: [json({ contractVersion: 1, proposalId, caseId: 'cp-case-c', state: 'approved', expiresAt: confirmation.expiresAt })],
    [advanceUrl]: [json({ contractVersion: 1, httpStatus: 409, error: { code: 'release_blocked', blockers: ['reserve_not_approved', 'obligations_not_signed_off'] } }, 409)],
  });
  card = await ask();
  fireEvent.click(within(card).getByRole('button', { name: 'Approve' }));
  fireEvent.click(await within(card).findByRole('button', { name: 'Release now' }));
  const alert = await within(card).findByRole('alert');
  expect(within(alert).getByText('Release is blocked until every gate is passed.')).toBeTruthy();
  expect(within(alert).getAllByRole('listitem').map((item) => item.textContent))
    .toEqual(['The maintenance reserve is not approved.', 'The return obligations are not signed off.']);
  expect(within(card).getByRole('button', { name: 'Release now' })).toBeTruthy();
  expect(fetcher).toHaveBeenCalledTimes(3);
});
