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
    'GET /api/v1/cases/cp-case-c/copilot/activity': () => json({ contractVersion: 1, caseId: 'cp-case-c', items: [] }),
    ...overrides,
  };
  fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const handler = routes[`${init?.method ?? 'GET'} ${url}`];
    if (!handler) throw new Error(`unexpected request ${init?.method ?? 'GET'} ${url}`);
    return handler(init);
  });
  vi.stubGlobal('fetch', fetchMock);
}

const arrivalSources = ['technical', 'maintenance', 'finance', 'records', 'contracts'] as const;
const arrivalPermissions: Record<string, readonly string[]> = {
  presenter: [],
  'technical-reviewer': ['technical', 'maintenance', 'records'],
  'finance-reviewer': ['technical', 'finance'],
  'legal-reviewer': ['records', 'contracts'],
};

function stubArrivalScenario(initialState: 'not_started' | 'collecting' = 'not_started') {
  const sessions: Record<string, { id: string; displayName: string; role: string; caseScopes: string[]; csrfToken: string }> = {
    presenter: { id: 'presenter', displayName: 'Pat Presenter', role: 'presenter', caseScopes: ['case-001'], csrfToken: 'csrf-presenter' },
    reviewer: { id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-reviewer' },
    'fin-1': { id: 'fin-1', displayName: 'Fin Analyst', role: 'finance-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-finance' },
    'legal-1': { id: 'legal-1', displayName: 'Lee Legal', role: 'legal-reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-legal' },
  };
  let activeSession = sessions.presenter;
  let revision = 1;
  let arrivalState: 'not_started' | 'collecting' | 'ready' = initialState;
  const pinned = new Set<string>();
  const audit: { sequence: number; revision: number; actor: string; action: string; detail: string; at: string }[] = [];
  const projectedAudit = () => audit.map((event) => {
    if (event.action !== 'arrival.source_read') return event;
    const detail = JSON.parse(event.detail) as { sourceId: string; recordRefs?: unknown[]; [key: string]: unknown };
    if (arrivalPermissions[activeSession.role].includes(detail.sourceId)) return event;
    const { recordRefs: _recordRefs, ...safeDetail } = detail;
    return { ...event, detail: JSON.stringify(safeDetail) };
  });
  const appendAudit = (actor: string, action: string, detail = '') => {
    revision += 1;
    audit.push({ sequence: audit.length + 1, revision, actor, action, detail, at });
  };
  const status = () => {
    const missingSources = arrivalState === 'ready' ? [] : arrivalSources.filter((sourceId) => !pinned.has(sourceId));
    return { contractVersion: 1, caseId: 'case-001', caseRevision: revision, state: arrivalState, missingSources,
      canComplete: arrivalState === 'collecting' && missingSources.length === 0 };
  };
  const routes: Record<string, (init?: RequestInit) => Response> = {
    'GET /api/auth/users': () => json([
      { id: 'presenter', displayName: 'Pat Presenter', role: 'presenter' },
      { id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer' },
      { id: 'fin-1', displayName: 'Fin Analyst', role: 'finance-reviewer' },
      { id: 'legal-1', displayName: 'Lee Legal', role: 'legal-reviewer' },
    ]),
    'POST /api/auth/sign-in': () => json(sessions.presenter),
    'POST /api/auth/switch-user': (init) => {
      const { userId } = JSON.parse(String(init?.body)) as { userId: string };
      activeSession = sessions[userId];
      return json(activeSession);
    },
    'GET /api/cases/case-001': () => json({
      caseId: 'case-001', revision, basis: pinned.size > 0 ? [{ version: pinned.size }] : [], audit: projectedAudit(),
      arrival: status(),
    }),
    'GET /api/cases/case-001/history': () => json({ events: [] }),
    'GET /api/cases/case-001/technical-reviews': () => json({ basisVersion: pinned.size, decisions: [] }),
    'GET /api/cases/case-001/reserve': () => new Response(null, { status: 403 }),
    'GET /api/cases/case-001/obligations': () => new Response(null, { status: 403 }),
    'GET /api/cases/case-001/readiness': () => json({ sentence: 'Return in 9 months.' }),
    'GET /api/v1/cases/case-001/copilot/activity': () => json({ contractVersion: 1, caseId: 'case-001', items: [] }),
    'GET /api/v1/cases/case-001/arrival': () => json(status()),
    'GET /api/v1/cases/case-001/sources': () => json({
      contractVersion: 1, caseId: 'case-001', caseRevision: revision,
      sources: arrivalSources.map((sourceId) => ({
        sourceId, mode: 'available', capabilityLabel: 'Simulated', coverage: true,
        ...(arrivalPermissions[activeSession.role].includes(sourceId) ? { recordCount: 2 } : {}),
      })),
    }),
    'POST /api/v1/cases/case-001/arrival/start': (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision) return json({ error: { code: 'stale_revision' } }, 409);
      arrivalState = 'collecting';
      appendAudit(activeSession.id, 'arrival.started');
      return json({ contractVersion: 1 });
    },
    'POST /api/v1/cases/case-001/arrival/complete': (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision || pinned.size !== arrivalSources.length)
        return json({ error: { code: 'stale_revision' } }, 409);
      arrivalState = 'ready';
      appendAudit(activeSession.id, 'arrival.completed');
      return json({ contractVersion: 1 });
    },
  };
  for (const sourceId of arrivalSources) {
    routes[`POST /api/v1/cases/case-001/arrival/sources/${sourceId}/retrieve`] = (init) => {
      const command = JSON.parse(String(init?.body)) as { expectedRevision: number };
      if (command.expectedRevision !== revision) return json({ error: { code: 'stale_revision' } }, 409);
      if (!arrivalPermissions[activeSession.role].includes(sourceId))
        return json({ error: { code: 'forbidden' } }, 403);
      const priorRevision = revision;
      pinned.add(sourceId);
      const recordRefs = [{ sourceId, recordId: `${sourceId}-record-1`, recordVersion: 1 }];
      appendAudit(activeSession.id, 'arrival.source_read', JSON.stringify({
        contractVersion: 1, sourceId, role: activeSession.role, outcome: 'pinned', recordRefs,
      }));
      return json({ contractVersion: 1, commandId: 'ui-test', caseId: 'case-001', sourceId, outcome: 'pinned',
        priorRevision, newRevision: revision, auditEventId: `audit-${revision}`, recordRefs });
    };
  }
  stubApi(routes);
  return {
    fetchMock,
    isPinned(sourceId: string) { return pinned.has(sourceId); },
    getSession() { return activeSession; },
  };
}

async function signInAs(name: string) {
  fireEvent.change(await screen.findByLabelText('Demo passcode'), { target: { value: 'demo' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: new RegExp(name) }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

beforeEach(() => { window.history.replaceState({}, '', '/'); window.localStorage.clear(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const copilotItem = (invocationId: string, operation: string, minute: number, extra: object = {}) => ({
  invocationId, operation, at: `2026-10-30T10:0${minute}:00Z`, outcome: 'succeeded', httpStatus: 200, resultCode: 'ok', source: 'from copilot',
  user: 'Technical reviewer', role: 'technical-reviewer', provenance: 'copilot-client', records: [], screening: null, proposalId: null, gate: null, ...extra,
});
const copilotItems = [
  copilotItem('i-read', 'read_source', 5, { records: [{ sourceId: 'maintenance', recordId: 'mx-cp-a-task-card-001', recordVersion: 1 }],
    screening: { state: 'detected', flaggedFields: ['taskText'] } }),
  copilotItem('i-gate', 'advance_stage', 6, { outcome: 'failed', httpStatus: 409, resultCode: 'release_blocked', user: null, role: 'presenter',
    proposalId: 'p-1', gate: { name: 'final-release', result: 'release_blocked', blockers: ['reserve_not_approved', 'obligations_not_signed_off'] } }),
];
const trailTexts = (trail: HTMLElement) => within(trail).getAllByRole('listitem').map((item) => item.querySelector('p')?.textContent);

describe('activity trail', () => {
  it('filters the activity trail to Copilot and UI entries', async () => {
    stubApi({ 'GET /api/v1/cases/cp-case-c/copilot/activity': () => json({ contractVersion: 1, caseId: 'cp-case-c', items: copilotItems }) });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');
    const trail = await screen.findByRole('complementary', { name: 'Activity' });
    await waitFor(() => expect(within(trail).getAllByRole('listitem')).toHaveLength(5));
    const ui = trailTexts(trail).slice(2);
    expect(trailTexts(trail).slice(0, 2)).toEqual(['Copilot advance_stage: failed', 'Copilot read_source: succeeded']);
    expect(ui).toHaveLength(3);

    fireEvent.click(within(trail).getByRole('radio', { name: 'Copilot' }));
    expect(trailTexts(trail)).toEqual(['Copilot advance_stage: failed', 'Copilot read_source: succeeded']);
    expect([...trail.querySelectorAll('.av-activity-badge')].map((badge) => badge.textContent)).toEqual(['Copilot', 'Copilot']);
    fireEvent.click(within(trail).getByRole('radio', { name: 'UI' }));
    expect(trailTexts(trail)).toEqual(ui);
    expect(trail.querySelectorAll('.av-activity-badge')).toHaveLength(0);
    fireEvent.click(within(trail).getByRole('radio', { name: 'All' }));
    expect(within(trail).getAllByRole('listitem')).toHaveLength(5);
    expect(within(trail).getAllByRole('button').every((button) => button.textContent!.startsWith('Details for '))).toBe(true);
    expect(within(trail).queryByRole('textbox')).toBeNull();
  });

  it('opens a copilot entry with who, role, tool, records, screening and the gate blockers', async () => {
    stubApi({ 'GET /api/v1/cases/cp-case-c/copilot/activity': () => json({ contractVersion: 1, caseId: 'cp-case-c', items: copilotItems }) });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');
    const trail = await screen.findByRole('complementary', { name: 'Activity' });
    const details = (button: HTMLElement) => {
      fireEvent.click(button);
      expect(button.getAttribute('aria-expanded')).toBe('true');
      const list = document.getElementById(button.getAttribute('aria-controls')!)!;
      return [...list.querySelectorAll('dd')].map((value) => `${value.previousElementSibling!.textContent}=${value.textContent}`);
    };
    const [gate, read] = await waitFor(() => { const found = within(trail).getAllByRole('button', { name: /^Details for / }); expect(found).toHaveLength(2); return found; });
    expect(details(gate)).toEqual(['Who=unknown user', 'Role=presenter', 'Tool=advance_stage', 'Outcome=failed (409 release_blocked)', 'Records=None',
      'Screening=None', 'Proposal=p-1', 'Gate=final-release: release_blocked; blockers: reserve_not_approved, obligations_not_signed_off']);
    expect(details(read)).toEqual(['Who=Technical reviewer', 'Role=technical-reviewer', 'Tool=read_source', 'Outcome=succeeded (200 ok)',
      'Records=maintenance mx-cp-a-task-card-001 v1', 'Screening=detected: taskText', 'Proposal=None', 'Gate=None']);
    expect(gate.getAttribute('aria-expanded')).toBe('false');
  });

  it('keeps the UI entries with a notice when copilot activity is unavailable', async () => {
    stubApi({ 'GET /api/v1/cases/cp-case-c/copilot/activity': () => json({ contractVersion: 1, error: { code: 'audit_unavailable' } }, 503) });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');
    const trail = await screen.findByRole('complementary', { name: 'Activity' });
    expect((await within(trail).findByRole('status')).textContent).toBe('Copilot activity is unavailable right now. Showing activity recorded in the app only.');
    expect(within(trail).getAllByRole('listitem')).toHaveLength(3);
  });

  it('shows no notice when the case is outside the copilot scope', async () => {
    stubApi({ 'GET /api/v1/cases/cp-case-c/copilot/activity': () => new Response(null, { status: 404 }) });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');
    const trail = await screen.findByRole('complementary', { name: 'Activity' });
    await waitFor(() => expect(within(trail).getAllByRole('listitem')).toHaveLength(3));
    expect(within(trail).queryByRole('status')).toBeNull();
  });
});

describe('journey app', () => {
  it('shows Data flows only after sign-in from its direct route', async () => {
    stubApi();
    window.history.replaceState({}, '', '/data-flows');
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Sign in to the demo' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Data flows' })).toBeNull();
    await signInAs('Riley Reviewer');

    expect(await screen.findByRole('heading', { name: 'Data flows', level: 1 })).toBeTruthy();
    expect(window.location.pathname).toBe('/data-flows');
  });

  it('opens Data flows from global navigation and preserves the unknown-route fallback', async () => {
    stubApi();
    render(<App />);
    await signInAs('Riley Reviewer');
    await screen.findByRole('heading', { name: 'Return pool' });

    fireEvent.click(screen.getByRole('button', { name: 'Data flows' }));
    expect(await screen.findByRole('heading', { name: 'Data flows', level: 1 })).toBeTruthy();
    expect(window.location.pathname).toBe('/data-flows');

    window.history.pushState({}, '', '/not-a-route');
    window.dispatchEvent(new PopStateEvent('popstate'));
    expect(await screen.findByRole('heading', { name: 'Return pool' })).toBeTruthy();
  });

  it('renders the Data flows page without Hangar Data Room content', async () => {
    stubApi();
    window.history.replaceState({}, '', '/data-flows');
    render(<App />);
    await signInAs('Riley Reviewer');

    expect(await screen.findByRole('heading', { name: 'Data flows', level: 1 })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Data Room aircraft' })).toBeNull();
  });

  it('blocks the next gate with exact missing sources', async () => {
    stubArrivalScenario('collecting');
    window.history.replaceState({}, '', '/cases/case-001');
    render(<App />);
    await signInAs('Pat Presenter');

    fireEvent.click(await screen.findByRole('button', { name: /Records intake/ }));
    expect(await screen.findByText('Missing sources: technical, maintenance, finance, records, contracts')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Send demo records package' })).toBeNull();
    expect((screen.getByRole('button', { name: 'Next gate' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: /AI investigation/ }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Record decision/ })).toBeNull();
  });

  it('preserves the open arrival case through authenticated reviewer handoffs', async () => {
    const scenario = stubArrivalScenario();
    window.history.replaceState({}, '', '/');
    render(<App />);
    await signInAs('Pat Presenter');

    fireEvent.click(await screen.findByRole('button', { name: 'Boeing 737-8 N-DEMO1: Arrived' }));
    expect(await screen.findByRole('heading', { name: 'N-DEMO1', level: 1 })).toBeTruthy();
    expect(window.location.pathname).toBe('/cases/case-001');
    fireEvent.click(await screen.findByRole('button', { name: 'Start arrival' }));
    expect(await screen.findByText('Arrival started. Request records from each permitted source.')).toBeTruthy();

    const switchFromSource = async (sourceId: string, label: string, role: string) => {
      const panel = await screen.findByRole('article', { name: `${sourceId} source` });
      fireEvent.click(within(panel).getByRole('button', { name: `Continue as ${label}` }));
      await waitFor(() => expect(scenario.getSession().role).toBe(role));
      expect(window.location.pathname).toBe('/cases/case-001');
    };
    const retrieve = async (sourceId: string) => {
      const panel = await screen.findByRole('article', { name: `${sourceId} source` });
      fireEvent.click(within(panel).getByRole('button', { name: 'Retrieve and pin records' }));
      expect(await within(panel).findByText('Records retrieved and pinned.')).toBeTruthy();
      await waitFor(() => expect(
        (within(panel).getByRole('button', { name: 'Retrieve and pin records' }) as HTMLButtonElement).disabled,
      ).toBe(false));
      expect(scenario.isPinned(sourceId)).toBe(true);
    };
    const switchFromMenu = async (userId: string, displayName: string) => {
      fireEvent.click(screen.getByRole('button', { name: new RegExp(displayName) }));
      fireEvent.click(screen.getByRole('menuitemradio', { name: /Demo presenter/ }));
      await waitFor(() => expect(scenario.getSession().id).toBe(userId));
      expect(window.location.pathname).toBe('/cases/case-001');
    };

    await switchFromSource('technical', 'Technical reviewer', 'technical-reviewer');
    await retrieve('technical');
    await switchFromMenu('presenter', 'Riley Reviewer');
    const activity = await screen.findByRole('complementary', { name: 'Activity' });
    expect(within(activity).getByText('technical source read: pinned')).toBeTruthy();
    expect(within(activity).getByText('technical-reviewer')).toBeTruthy();
    fireEvent.click(within(activity).getByRole('radio', { name: 'UI' }));
    expect(within(activity).getByText('None')).toBeTruthy();
    expect(within(activity).queryByText('technical-record-1')).toBeNull();
    fireEvent.click(within(activity).getByRole('radio', { name: 'All' }));

    await switchFromSource('technical', 'Technical reviewer', 'technical-reviewer');
    await retrieve('maintenance');
    await retrieve('records');
    await switchFromSource('finance', 'Finance reviewer', 'finance-reviewer');
    await retrieve('finance');
    await switchFromSource('contracts', 'Legal reviewer', 'legal-reviewer');
    await retrieve('contracts');
    await switchFromMenu('presenter', 'Lee Legal');

    fireEvent.click(await screen.findByRole('button', { name: 'Complete arrival' }));
    expect(await screen.findByRole('heading', { name: 'Records package intake', level: 2 })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Send demo records package' })).toBeTruthy();
    expect(window.location.pathname).toBe('/cases/case-001');
    fireEvent.click(screen.getByRole('button', { name: /AI investigation/ }));
    expect(await screen.findByText('Starts after intake')).toBeTruthy();
    expect(screen.queryByText('The records agent is queued')).toBeNull();
    expect(arrivalSources.every((sourceId) => scenario.isPinned(sourceId))).toBe(true);
  });

  it('opens the read-only compliance view from the case decision dossier', async () => {
    stubApi({
      'GET /api/v1/cases/cp-case-c/compliance-evidence': () => json({
        basisVersion: 1, currentBasis: true, applicabilityAccess: 'permitted', regimes: [], untagged: [],
        basisDecision: null, decisions: [],
        responsibilities: { platform: ['Access control', 'Audit trail', 'Pinned evidence'], customer: ['Review evidence and record decisions'], limitation: 'Evidence only.' },
      }),
    });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');
    fireEvent.click(within(await screen.findByRole('complementary', { name: 'Decision dossier' })).getByRole('button', { name: 'Compliance evidence' }));
    expect(await screen.findByRole('heading', { name: 'Compliance evidence', level: 2 })).toBeTruthy();
    expect(await screen.findByText('No permitted, tagged regimes are recorded on this basis. Needs review.')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Open source-system case' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Back to journey' }));
    expect(await screen.findByRole('heading', { name: 'Technical review', level: 2 })).toBeTruthy();
  });

  it('navigates an authorized presenter to a separate legacy source case without labelling it as a pool aircraft', async () => {
    const emptyEvidence = { basisVersion: 1, currentBasis: true, applicabilityAccess: 'permitted', regimes: [], untagged: [],
      basisDecision: null, decisions: [], responsibilities: { platform: [], customer: [], limitation: 'Evidence only.' } };
    stubApi({
      'GET /api/auth/users': () => json([{ id: 'presenter', displayName: 'Pat Presenter', role: 'presenter' }]),
      'POST /api/auth/sign-in': () => json({ ...reviewer, id: 'presenter', role: 'presenter', caseScopes: ['cp-case-c', 'case-001'] }),
      'GET /api/v1/cases/cp-case-c/compliance-evidence': () => json(emptyEvidence),
      'GET /api/cases/case-001': () => json({ caseId: 'case-001', revision: 1, basis: [{ version: 1 }], audit: [] }),
      'GET /api/v1/cases/case-001/compliance-evidence': () => json(emptyEvidence),
    });
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Pat Presenter');
    fireEvent.click(within(await screen.findByRole('complementary', { name: 'Decision dossier' })).getByRole('button', { name: 'Compliance evidence' }));
    fireEvent.click(await screen.findByRole('link', { name: 'Open source-system case' }));
    expect(await screen.findByRole('heading', { name: 'case-001', level: 1 })).toBeTruthy();
    expect(window.location.pathname).toBe('/cases/case-001');
    expect(screen.getByText('Synthetic source-system demonstration')).toBeTruthy();
    fireEvent.click(within(await screen.findByRole('complementary', { name: 'Decision dossier' })).getByRole('button', { name: 'Compliance evidence' }));
    expect(await screen.findByText('No permitted, tagged regimes are recorded on this basis. Needs review.')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Open source-system case' })).toBeNull();
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/v1/cases/case-001/compliance-evidence')).toBe(true);
  });

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

  it('explains why the technical review needs a person and links to the full AI briefing', async () => {
    stubApi();
    window.history.replaceState({}, '', '/cases/cp-case-c');
    render(<App />);
    await signInAs('Riley Reviewer');

    const why = await screen.findByRole('region', { name: 'Why you are reviewing' });
    expect(within(why).getByText('Landing-gear inspection report not processed')).toBeTruthy();
    expect(within(why).getByText('If you reject')).toBeTruthy();

    fireEvent.click(within(screen.getByRole('region', { name: 'AI insight' })).getByRole('button', { name: 'See what the AI found' }));
    expect(await screen.findByRole('heading', { name: 'AI records investigation', level: 2 })).toBeTruthy();
    expect(screen.getByText('Main landing-gear inspection report')).toBeTruthy();
    expect(screen.getByText('Could not read')).toBeTruthy();
    expect(screen.getByText('Did not infer the content of the unread report from the work card.')).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'AI insight' })).toBeNull();
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

  it('lets the presenter send the demo records package from the intake step', async () => {
    const presenter = { id: 'presenter', displayName: 'Pat Presenter', role: 'presenter', caseScopes: ['case-001'], csrfToken: 'csrf-4' };
    let uploaded = false;
    const pdf = () => new Response('%PDF-1.4', { status: 200, headers: { 'Content-Type': 'application/pdf' } });
    stubApi({
      'GET /api/auth/users': () => json([{ id: 'presenter', displayName: 'Pat Presenter', role: 'presenter' }]),
      'POST /api/auth/sign-in': () => json(presenter),
      'GET /demo-records/case-001/engine-identity-record.pdf': pdf,
      'GET /demo-records/case-001/llp-status-sheet.pdf': pdf,
      'GET /demo-records/case-001/maintenance-log-extract.pdf': pdf,
      'POST /api/cases/case-001/packages': () => { uploaded = true; return json({ packageId: 'pkg-1', caseId: 'case-001', state: 'complete', documents: [] }, 201); },
      'GET /api/cases/case-001': () => json(uploaded
        ? { caseId: 'case-001', revision: 3, basis: [{ version: 1 }], audit: [
          { sequence: 1, revision: 1, actor: 'intake', action: 'journey', detail: 'Records package received.', at },
          { sequence: 2, revision: 2, actor: 'records-agent', action: 'journey', detail: 'Records agent read 3 documents.', at }] }
        : { caseId: 'case-001', revision: 0, basis: [], audit: [] }),
      'GET /api/cases/case-001/history': () => json({ events: [] }),
      'GET /api/cases/case-001/technical-reviews': () => json({ basisVersion: 1, decisions: [] }),
      'GET /api/cases/case-001/reserve': () => new Response(null, { status: 403 }),
      'GET /api/cases/case-001/obligations': () => new Response(null, { status: 403 }),
      'GET /api/cases/case-001/readiness': () => json({ sentence: 'Return in 9 months.' }),
    });
    window.history.replaceState({}, '', '/cases/case-001');
    render(<App />);
    await signInAs('Pat Presenter');

    fireEvent.click(await screen.findByRole('button', { name: 'Send demo records package' }));
    await waitFor(() => expect(uploaded).toBe(true));
    const post = fetchMock.mock.calls.find(([url, init]) => url === '/api/cases/case-001/packages' && init?.method === 'POST')!;
    const init = post[1] as RequestInit;
    expect(init.headers).toEqual({ 'X-CSRF-Token': 'csrf-4' });
    expect((init.body as FormData).getAll('files').map((file) => (file as File).name))
      .toEqual(['engine-identity-record.pdf', 'llp-status-sheet.pdf', 'maintenance-log-extract.pdf']);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Send demo records package' })).toBeNull());
  });

  it('switches to the 3D hangar mode from the top bar', async () => {
    stubApi();
    render(<App />);
    await signInAs('Riley Reviewer');
    await screen.findByRole('heading', { name: 'Return pool' });
    fireEvent.click(screen.getByRole('button', { name: '3D hangar' }));
    expect(window.location.pathname).toBe('/hangar');
    expect(screen.getByRole('button', { name: '3D hangar' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.queryByRole('heading', { name: 'Data flows' })).toBeNull();
  });
});
