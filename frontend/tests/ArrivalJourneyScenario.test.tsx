import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

const sources = ['technical', 'maintenance', 'finance', 'records', 'contracts'] as const;
const permissions: Record<string, readonly string[]> = {
  presenter: [],
  'technical-reviewer': ['technical', 'maintenance', 'records'],
  'finance-reviewer': ['technical', 'finance'],
  'legal-reviewer': ['records', 'contracts'],
};
const at = '2026-11-02T12:00:00Z';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubScenario() {
  const sessions = {
    presenter: { id: 'presenter', displayName: 'Pat Presenter', role: 'presenter', caseScopes: ['case-001'], csrfToken: 'csrf-presenter' },
    reviewer: { id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-reviewer' },
    'fin-1': { id: 'fin-1', displayName: 'Fin Analyst', role: 'finance-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-finance' },
    'legal-1': { id: 'legal-1', displayName: 'Lee Legal', role: 'legal-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-legal' },
  };
  type SessionKey = keyof typeof sessions;
  let active: SessionKey = 'presenter';
  let revision = 1;
  let arrivalState: 'not_started' | 'collecting' | 'ready' = 'not_started';
  const pinned = new Set<string>();
  const audit: { sequence: number; revision: number; actor: string; action: string; detail: string; at: string }[] = [];

  const status = () => ({
    contractVersion: 1, caseId: 'case-001', caseRevision: revision, state: arrivalState,
    missingSources: arrivalState === 'ready' ? [] : sources.filter((source) => !pinned.has(source)),
    canComplete: arrivalState === 'collecting' && pinned.size === sources.length,
  });
  const record = () => ({
    caseId: 'case-001', revision, basis: pinned.size === 0 ? [] : [{ version: pinned.size }],
    audit: audit.map((event) => {
      if (event.action !== 'arrival.source_read') return event;
      const detail = JSON.parse(event.detail) as { sourceId: string; recordRefs?: unknown[]; [key: string]: unknown };
      if (permissions[sessions[active].role].includes(detail.sourceId)) return event;
      const { recordRefs: _recordRefs, ...projected } = detail;
      return { ...event, detail: JSON.stringify(projected) };
    }),
    arrival: status(),
  });
  const append = (actor: string, action: string, detail = '') => {
    revision += 1;
    audit.push({ sequence: audit.length + 1, revision, actor, action, detail, at });
  };
  const people = Object.values(sessions).map(({ id, displayName, role }) => ({ id, displayName, role }));
  const routes: Record<string, (init?: RequestInit) => Response> = {
    'GET /api/health': () => json({ status: 'ok' }),
    'POST /api/auth/passcode': () => new Response(null, { status: 204 }),
    'GET /api/auth/users': () => json(people),
    'POST /api/auth/sign-in': () => json(sessions.presenter),
    'POST /api/auth/switch-user': (init) => {
      active = JSON.parse(String(init?.body)).userId as SessionKey;
      return json(sessions[active]);
    },
    'GET /api/cases/case-001': () => json(record()),
    'GET /api/cases/case-001/history': () => json({ events: [] }),
    'GET /api/cases/case-001/technical-reviews': () => json({ basisVersion: pinned.size, decisions: [] }),
    'GET /api/cases/case-001/reserve': () => new Response(null, { status: 403 }),
    'GET /api/cases/case-001/obligations': () => new Response(null, { status: 403 }),
    'GET /api/cases/case-001/readiness': () => json({ sentence: 'Return in 9 months.' }),
    'GET /api/v1/cases/case-001/copilot/activity': () => json({ contractVersion: 1, caseId: 'case-001', items: [] }),
    'GET /api/v1/cases/case-001/arrival': () => json(status()),
    'GET /api/v1/cases/case-001/sources': () => json({
      contractVersion: 1, caseId: 'case-001', caseRevision: revision,
      sources: sources.map((sourceId) => ({
        sourceId, mode: 'available', capabilityLabel: 'Simulated',
        coverage: sourceId !== 'technical',
        ...(permissions[sessions[active].role].includes(sourceId) ? { recordCount: 2 } : {}),
      })),
    }),
    'POST /api/v1/cases/case-001/arrival/start': (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision) return json({ error: { code: 'stale_revision' } }, 409);
      arrivalState = 'collecting';
      append(sessions[active].id, 'arrival.started');
      return json({ contractVersion: 1 });
    },
    'POST /api/v1/cases/case-001/arrival/complete': (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision || pinned.size !== sources.length) {
        return json({ error: { code: 'arrival_sources_missing', missingSources: status().missingSources } }, 409);
      }
      arrivalState = 'ready';
      append(sessions[active].id, 'arrival.completed');
      return json({ contractVersion: 1 });
    },
  };
  for (const sourceId of sources) {
    routes[`POST /api/v1/cases/case-001/arrival/sources/${sourceId}/retrieve`] = (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision) return json({ error: { code: 'stale_revision' } }, 409);
      if (!permissions[sessions[active].role].includes(sourceId)) return json({ error: { code: 'forbidden' } }, 403);
      const priorRevision = revision;
      pinned.add(sourceId);
      const recordRefs = [{ sourceId, recordId: `${sourceId}-record-1`, recordVersion: 1 }];
      append(sessions[active].id, 'arrival.source_read', JSON.stringify({
        contractVersion: 1, sourceId, role: sessions[active].role, outcome: 'pinned', recordRefs,
      }));
      return json({
        contractVersion: 1, commandId: `scenario-${sourceId}`, caseId: 'case-001', sourceId, outcome: 'pinned',
        priorRevision, newRevision: revision, auditEventId: `audit-${revision}`, recordRefs,
      });
    };
  }

  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const handler = routes[`${init?.method ?? 'GET'} ${url}`];
    if (!handler) throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`);
    return handler(init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    fetchMock,
    isPinned(sourceId: string) { return pinned.has(sourceId); },
    role() { return sessions[active].role; },
  };
}

async function signInAsPresenter() {
  fireEvent.change(await screen.findByLabelText('Demo passcode'), { target: { value: 'demo' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: /Pat Presenter/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByRole('heading', { name: 'Return pool' });
}

async function switchPersona(menuLabel: string, displayName: string, role: string, stub: ReturnType<typeof stubScenario>) {
  fireEvent.click(screen.getByRole('button', { name: /Pat Presenter|Riley Reviewer|Fin Analyst|Lee Legal/ }));
  fireEvent.click(await screen.findByRole('menuitemradio', { name: new RegExp(menuLabel) }));
  await waitFor(() => expect(stub.role()).toBe(role));
  await screen.findByText(`You are now signed in as ${displayName}.`);
}

async function retrieve(sourceId: typeof sources[number], role: string, roleLabel: string) {
  const panel = await screen.findByRole('article', { name: `${sourceId} source` });
  fireEvent.click(within(panel).getByRole('button', { name: 'Retrieve and pin records' }));
  await within(panel).findByText('Records retrieved and pinned.');
  await waitFor(() => expect(scenario.isPinned(sourceId)).toBe(true));
  const activity = screen.getByRole('complementary', { name: 'Activity' });
  const event = within(activity).getByText(`${sourceId} source read: pinned`).closest('li');
  expect(event?.textContent).toContain(role);
  expect(event?.textContent).toContain(roleLabel);
  expect(event?.textContent).toContain(`${sourceId} ${sourceId}-record-1 v1`);
}

let scenario: ReturnType<typeof stubScenario>;
beforeEach(() => {
  window.history.replaceState({}, '', '/');
  window.localStorage.clear();
  scenario = stubScenario();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('arrival scenario', () => {
  it('presenter walks five governed sources to enabled handoff', async () => {
    render(<App />);
    await signInAsPresenter();

    fireEvent.click(await screen.findByRole('button', { name: 'Boeing 737-8 N-DEMO1: Arrived' }));
    await screen.findByRole('article', { name: 'technical source' });
    const sourceCards = screen.getAllByRole('article').filter((element) => element.getAttribute('aria-label')?.endsWith(' source'));
    expect(sourceCards.map((element) => element.getAttribute('aria-label'))).toEqual(sources.map((source) => `${source} source`));
    expect(within(screen.getByRole('article', { name: 'technical source' })).getByText('Not covered')).toBeTruthy();
    expect(within(screen.getByRole('article', { name: 'maintenance source' })).getByText('Covered')).toBeTruthy();
    const financePanel = screen.getByRole('article', { name: 'finance source' });
    expect(within(financePanel).getByText('Not permitted')).toBeTruthy();
    expect(within(financePanel).queryByRole('button', { name: 'Retrieve and pin records' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Records intake/ }));
    expect((await screen.findByRole('alert')).textContent).toContain('This gate is blocked until arrival is complete.');
    expect(screen.getByText('Missing sources: technical, maintenance, finance, records, contracts')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Next gate' }).matches(':disabled')).toBe(true);

    fireEvent.click(await screen.findByRole('button', { name: /Gate 1Arrival/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Start arrival' }));
    await screen.findByText('Arrival status: collecting · Missing: technical, maintenance, finance, records, contracts');

    await switchPersona('Technical reviewer', 'Riley Reviewer', 'technical-reviewer', scenario);
    for (const sourceId of ['technical', 'maintenance', 'records'] as const)
      await retrieve(sourceId, 'technical-reviewer', 'Technical reviewer');

    await switchPersona('Finance reviewer', 'Fin Analyst', 'finance-reviewer', scenario);
    await retrieve('finance', 'finance-reviewer', 'Finance reviewer');

    await switchPersona('Legal reviewer', 'Lee Legal', 'legal-reviewer', scenario);
    await retrieve('contracts', 'legal-reviewer', 'Legal reviewer');

    await switchPersona('Demo presenter', 'Pat Presenter', 'presenter', scenario);
    fireEvent.click(await screen.findByRole('button', { name: /Gate 1Arrival/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Complete arrival' }));
    await screen.findByRole('heading', { name: 'Records package intake' });
    const handoffButton = await screen.findByRole('button', { name: 'Send demo records package' });
    expect(handoffButton.matches(':disabled')).toBe(false);
    expect(screen.getByRole('button', { name: 'Next gate' }).matches(':disabled')).toBe(false);

    const activity = screen.getByRole('complementary', { name: 'Activity' });
    for (const sourceId of sources) {
      expect(within(activity).getByText(`${sourceId} source read: pinned`)).toBeTruthy();
      expect(activity.textContent).not.toContain(`${sourceId}-record-1`);
    }
    expect(scenario.fetchMock.mock.calls.some(([url]) => url === '/api/cases/case-001/packages')).toBe(false);
  }, 15000);
});
