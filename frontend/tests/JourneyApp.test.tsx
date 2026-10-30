import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

const reviewer = { id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer', caseScopes: ['cp-case-c'], csrfToken: 'csrf-1' };
const finance = { id: 'fin-1', displayName: 'Fin Analyst', role: 'finance-reviewer', caseScopes: ['cp-case-c'], csrfToken: 'csrf-2' };
const at = '2026-10-30T10:00:00Z';
const audit = [
  { sequence: 1, revision: 1, actor: 'presenter', action: 'journey', detail: 'Package received.', at },
  { sequence: 2, revision: 2, actor: 'records-agent', action: 'journey', detail: 'AI agent raised findings.', at },
  { sequence: 3, revision: 3, actor: 'assessment-service', action: 'create-work-item', detail: 'review-cp-case-c', at },
];
const caseRecord = { caseId: 'cp-case-c', revision: 3, basis: [{ version: 1 }], audit };
const history = { events: [{ eventId: 'e2', sequence: 2, eventType: 'findings-raised', actorRole: 'records-agent', summary: 'AI agent raised findings.', capabilityLabel: 'AI agent',
  witnesses: [{ witnessId: 'w1', documentId: 'case-c/engine-serial-record.txt', documentVersion: 1, page: 1 }] }] };

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let fetchMock: ReturnType<typeof vi.fn>;

function stubApi(overrides: Record<string, (init?: RequestInit) => Response> = {}) {
  const routes: Record<string, (init?: RequestInit) => Response> = {
    'GET /api/health': () => json({ status: 'ok' }),
    'POST /api/auth/passcode': () => new Response(null, { status: 204 }),
    'GET /api/auth/users': () => json([{ id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer' }, { id: 'fin-1', displayName: 'Fin Analyst', role: 'finance-reviewer' }]),
    'POST /api/auth/sign-in': () => json(reviewer),
    'GET /api/cases/cp-case-c': () => json(caseRecord),
    'GET /api/cases/cp-case-c/history': () => json(history),
    'GET /api/cases/cp-case-c/technical-reviews': () => json({ basisVersion: 1, decisions: [] }),
    'GET /api/cases/cp-case-c/reserve': () => new Response(null, { status: 403 }),
    'GET /api/cases/cp-case-c/obligations': () => new Response(null, { status: 403 }),
    'GET /api/cases/cp-case-c/readiness': () => json({ sentence: 'Return in 9 months.' }),
    ...overrides,
  };
  fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const handler = routes[`${init?.method ?? 'GET'} ${url}`];
    if (!handler) throw new Error(`unexpected request ${init?.method ?? 'GET'} ${url}`);
    return handler(init);
  });
  vi.stubGlobal('fetch', fetchMock);
}

async function signInAs(name: string) {
  fireEvent.change(await screen.findByLabelText('Demo passcode'), { target: { value: 'demo' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: new RegExp(name) }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

beforeEach(() => { window.history.replaceState({}, '', '/'); window.localStorage.clear(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('journey app', () => {
  it('signs in and shows the aircraft at its gate with the human waiting state', async () => {
    stubApi();
    render(<App />);
    await signInAs('Riley Reviewer');

    expect(await screen.findByRole('heading', { name: 'Return pool' })).toBeTruthy();
    const card = await screen.findByRole('button', { name: 'Airbus A350-900 CP-WB-003: Waiting for technical reviewer' });
    expect(card).toBeTruthy();
    expect(within(screen.getByRole('region', { name: 'Waiting on you' })).getByText('Technical review')).toBeTruthy();
  });

  it('records a technical decision with the expected revision and basis version', async () => {
    let decided = false;
    stubApi({
      'POST /api/cases/cp-case-c/technical-reviews': () => { decided = true; return json({ decision: 'accept' }); },
      'GET /api/cases/cp-case-c': () => json(decided
        ? { ...caseRecord, revision: 4, audit: [...audit, { sequence: 4, revision: 4, actor: 'reviewer', action: 'technical-review.decided', detail: '{"Decision":"accept","Reason":"Checked."}', at }] }
        : caseRecord),
    });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');

    expect(await screen.findByRole('heading', { name: 'Technical review', level: 2 })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Record decision' }));
    expect((await screen.findByRole('alert')).textContent).toBe('Add a short reason. It is kept in the audit trail.');

    fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'Checked.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Record decision' }));

    await waitFor(() => expect(decided).toBe(true));
    const post = fetchMock.mock.calls.find(([url, init]) => url === '/api/cases/cp-case-c/technical-reviews' && init?.method === 'POST')!;
    expect((post[1] as RequestInit).headers).toEqual({ 'Content-Type': 'application/json', 'X-CSRF-Token': 'csrf-1' });
    expect(JSON.parse((post[1] as RequestInit).body as string)).toEqual({ expectedRevision: 3, expectedBasisVersion: 1, decision: 'accept', reason: 'Checked.' });
    expect(await screen.findByText('Accepted the AI findings')).toBeTruthy();
  });

  it('hands a human gate to its owner instead of showing the form to another persona', async () => {
    stubApi({
      'POST /api/auth/sign-in': () => json(finance),
      'POST /api/auth/switch-user': (init) => { expect(JSON.parse(init!.body as string)).toEqual({ userId: 'reviewer' }); return json(reviewer); },
    });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Fin Analyst');

    expect(await screen.findByText('Waiting for the technical reviewer')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Record decision' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Continue as technical reviewer' }));
    expect(await screen.findByRole('button', { name: 'Record decision' })).toBeTruthy();
  });

  it('lets the transition lead release a fully signed-off aircraft to its next lessee', async () => {
    const presenter = { id: 'presenter', displayName: 'Pat Presenter', role: 'presenter', caseScopes: ['cp-case-c'], csrfToken: 'csrf-3' };
    const signedOff = [...audit,
      { sequence: 4, revision: 4, actor: 'reviewer', action: 'technical-review.decided', detail: '{"Decision":"accept","Reason":"ok"}', at },
      { sequence: 5, revision: 5, actor: 'fin-1', action: 'reserve.decided', detail: '{"decision":"approve","reason":"ok"}', at },
      { sequence: 6, revision: 6, actor: 'legal-1', action: 'obligation.legal-decided', detail: '{"Decision":"signed-off","Reason":"ok"}', at }];
    let released = false;
    stubApi({
      'GET /api/auth/users': () => json([{ id: 'presenter', displayName: 'Pat Presenter', role: 'presenter' }]),
      'POST /api/auth/sign-in': () => json(presenter),
      'POST /api/cases/cp-case-c/release': () => { released = true; return json({ decision: 'released' }); },
      'GET /api/cases/cp-case-c': () => json(released
        ? { ...caseRecord, revision: 7, audit: [...signedOff, { sequence: 7, revision: 7, actor: 'presenter', action: 'aircraft.released', detail: '{"decision":"released","note":"Keys handed over."}', at }] }
        : { ...caseRecord, revision: 6, audit: signedOff }),
    });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Pat Presenter');

    const submit = await screen.findByRole('button', { name: 'Release to Polaris International' });
    fireEvent.click(submit);
    expect((await screen.findByRole('alert')).textContent).toBe('Add a short handover note. It is kept in the audit trail.');
    expect(released).toBe(false);

    fireEvent.change(screen.getByLabelText('Handover note'), { target: { value: 'Keys handed over.' } });
    fireEvent.click(submit);
    await waitFor(() => expect(released).toBe(true));
    const post = fetchMock.mock.calls.find(([url, init]) => url === '/api/cases/cp-case-c/release' && init?.method === 'POST')!;
    expect((post[1] as RequestInit).headers).toEqual({ 'Content-Type': 'application/json', 'X-CSRF-Token': 'csrf-3' });
    expect(JSON.parse((post[1] as RequestInit).body as string)).toEqual({ expectedRevision: 6, note: 'Keys handed over.' });
    expect(await screen.findByText('Released to Polaris International')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Release to Polaris International' })).toBeNull();
  });

  it('switches to the 3D hangar mode from the top bar', async () => {
    stubApi();
    render(<App />);
    await signInAs('Riley Reviewer');
    await screen.findByRole('heading', { name: 'Return pool' });
    fireEvent.click(screen.getByRole('button', { name: '3D hangar' }));
    expect(window.location.pathname).toBe('/hangar');
    expect(screen.getByRole('button', { name: '3D hangar' }).getAttribute('aria-pressed')).toBe('true');
  });
});
