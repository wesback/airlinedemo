import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Session } from '../src/journey/api';
import ArrivalSources from '../src/journey/ArrivalSources';
import { ARRIVAL_SOURCE_IDS } from '../src/journey/arrivalApi';

const caseId = 'case-001';
const revisionStart = 7;
const csrfToken = 'csrf-arrival';
const allMissingSources = [...ARRIVAL_SOURCE_IDS];
const sourceRoles: Record<string, string[]> = {
  'technical-reviewer': ['technical', 'maintenance', 'records'],
  'finance-reviewer': ['technical', 'finance'],
  'legal-reviewer': ['records', 'contracts'],
  presenter: [],
};

function session(id: string, role: string): Session {
  return { id, displayName: id, role, caseScopes: [caseId], csrfToken };
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function sourceRecord(sourceId: string, fields: Record<string, string> = {}) {
  return {
    sourceId, recordId: `${sourceId}-record-1`, recordVersion: 3, asOf: '2026-11-02T10:00:00Z',
    caseId, kind: sourceId === 'maintenance' ? 'task-card' : 'source-record', stale: false,
    payload: { fields }, synthetic: true,
  };
}

type StubOptions = {
  list?: (sourceId: string, init: RequestInit | undefined, call: number) => Response | Promise<Response>;
  retrieve?: (sourceId: string, init: RequestInit | undefined) => Response | Promise<Response>;
};

function stubArrivalApi(options: StubOptions = {}) {
  let currentRole = 'technical-reviewer';
  let revision = revisionStart;
  let state: 'collecting' | 'ready' = 'collecting';
  let listCount = 0;
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET' && url === `/api/cases/${caseId}`) {
      return json({ caseId, revision, basis: [], audit: [] });
    }
    if (method === 'GET' && url === `/api/v1/cases/${caseId}/arrival`) {
      return json({ contractVersion: 1, caseId, caseRevision: revision, state, missingSources: allMissingSources, canComplete: false });
    }
    if (method === 'GET' && url === `/api/v1/cases/${caseId}/sources`) {
      const permitted = sourceRoles[currentRole];
      return json({
        contractVersion: 1, caseId, caseRevision: revision,
        sources: ARRIVAL_SOURCE_IDS.map((sourceId) => ({
          sourceId, mode: 'available', capabilityLabel: 'Simulated',
          coverage: sourceId !== 'technical', ...(permitted.includes(sourceId) ? { recordCount: 2 } : {}),
        })),
      });
    }
    const listMatch = url.match(new RegExp(`^/api/v1/cases/${caseId}/sources/([^/]+)/records$`));
    if (method === 'GET' && listMatch) {
      listCount += 1;
      const sourceId = listMatch[1];
      const response = options.list
        ? await options.list(sourceId, init, listCount)
        : json({ contractVersion: 1, caseId, sourceId, records: [sourceRecord(sourceId)] });
      revision += 1;
      return response;
    }
    const retrieveMatch = url.match(new RegExp(`^/api/v1/cases/${caseId}/arrival/sources/([^/]+)/retrieve$`));
    if (method === 'POST' && retrieveMatch) {
      const sourceId = retrieveMatch[1];
      const response = options.retrieve
        ? await options.retrieve(sourceId, init)
        : json({
          contractVersion: 1, commandId: 'ui-test-command', caseId, sourceId, outcome: 'pinned',
          priorRevision: revision, newRevision: revision + 1, auditEventId: 'audit-1',
          recordRefs: [{ sourceId, recordId: `${sourceId}-record-1`, recordVersion: 3 }],
        });
      revision += 1;
      return response;
    }
    if (method === 'POST' && url === `/api/v1/cases/${caseId}/arrival/start`) {
      state = 'collecting';
      revision += 1;
      return json({ contractVersion: 1 });
    }
    if (method === 'POST' && url === `/api/v1/cases/${caseId}/arrival/complete`) {
      state = 'ready';
      revision += 1;
      return json({ contractVersion: 1 });
    }
    throw new Error(`Unexpected request ${method} ${url}`);
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    fetchMock,
    setRole(role: string) { currentRole = role; },
    getListCount() { return listCount; },
  };
}

function mount(onChanged = vi.fn(), onSwitchPersona = vi.fn()) {
  const view = render(<ArrivalSources caseId={caseId} session={session('reviewer', 'technical-reviewer')}
    onChanged={onChanged} onSwitchPersona={onSwitchPersona} />);
  return { ...view, onChanged, onSwitchPersona };
}

function sourcePanel(sourceId: string) {
  return screen.findByRole('article', { name: `${sourceId} source` });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('arrival source panels', () => {
  it('renders exactly five simulated panels using server coverage', async () => {
    const { fetchMock } = stubArrivalApi();
    mount();

    const region = await screen.findByRole('region', { name: 'Governed source access' });
    expect(within(region).getAllByRole('article').map((panel) => panel.getAttribute('aria-label'))).toEqual(
      ARRIVAL_SOURCE_IDS.map((sourceId) => `${sourceId} source`),
    );
    for (const sourceId of ARRIVAL_SOURCE_IDS) {
      expect(within(await sourcePanel(sourceId)).getByText('Simulated')).toBeTruthy();
    }
    expect(within(await sourcePanel('technical')).getByText('Not covered')).toBeTruthy();
    expect(within(await sourcePanel('maintenance')).getByText('Covered')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([url]) => url.endsWith('/records'))).toBe(false);
  });

  it('does not read denied sources and clears records on persona change', async () => {
    let resolveLateRead: ((response: Response) => void) | undefined;
    const stub = stubArrivalApi({
      list: (_sourceId, _init, call) => call === 2
        ? new Promise<Response>((resolve) => { resolveLateRead = resolve; })
        : json({ contractVersion: 1, caseId, sourceId: 'maintenance', records: [sourceRecord('maintenance', { taskText: 'maintenance-private' })] }),
    });
    const onSwitchPersona = vi.fn();
    const view = mount(vi.fn(), onSwitchPersona);
    const maintenance = await sourcePanel('maintenance');
    fireEvent.click(within(maintenance).getByRole('button', { name: 'View records' }));
    expect(await within(maintenance).findByText('maintenance-private')).toBeTruthy();

    fireEvent.click(within(maintenance).getByRole('button', { name: 'View records' }));
    await waitFor(() => expect(stub.getListCount()).toBe(2));
    expect(resolveLateRead).toBeDefined();

    stub.setRole('finance-reviewer');
    view.rerender(<ArrivalSources caseId={caseId} session={session('finance', 'finance-reviewer')}
      onChanged={vi.fn()} onSwitchPersona={onSwitchPersona} />);
    const financeMaintenance = await screen.findByRole('article', { name: 'maintenance source' });
    expect(within(financeMaintenance).getByText('Not permitted')).toBeTruthy();
    expect(within(financeMaintenance).queryByRole('button', { name: 'View records' })).toBeNull();
    expect(within(financeMaintenance).queryByText('maintenance-private')).toBeNull();
    fireEvent.click(within(financeMaintenance).getByRole('button', { name: 'Continue as Technical reviewer' }));
    expect(onSwitchPersona).toHaveBeenCalledWith('reviewer');

    await act(async () => {
      resolveLateRead!(json({ contractVersion: 1, caseId, sourceId: 'maintenance',
        records: [sourceRecord('maintenance', { taskText: 'late-private-response' })] }));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(within(financeMaintenance).queryByText('late-private-response')).toBeNull();
    expect(stub.fetchMock.mock.calls.filter(([url]) => url.endsWith('/maintenance/records'))).toHaveLength(2);
  });

  it('shows exact source outcomes without treating empty as unavailable', async () => {
    const stub = stubArrivalApi({
      retrieve: (sourceId) => json({
        contractVersion: 1, commandId: 'ui-test-command', caseId, sourceId, outcome: 'empty',
        priorRevision: revisionStart, newRevision: revisionStart + 1, auditEventId: 'audit-empty', recordRefs: [],
      }),
      list: (sourceId) => sourceId === 'technical'
        ? json({ contractVersion: 1, error: { code: 'source_unavailable' } }, 503)
        : json({ contractVersion: 1, caseId, sourceId, records: [] }),
    });
    mount();

    const maintenance = await sourcePanel('maintenance');
    fireEvent.click(within(maintenance).getByRole('button', { name: 'Retrieve and pin records' }));
    expect(await within(maintenance).findByText('No records were available from this source.')).toBeTruthy();

    const technical = await sourcePanel('technical');
    fireEvent.click(within(technical).getByRole('button', { name: 'View records' }));
    expect(await within(technical).findByText('The source is unavailable.')).toBeTruthy();
    expect(within(maintenance).queryByText('The source is unavailable.')).toBeNull();
    expect(stub.fetchMock.mock.calls.filter(([url]) => url.endsWith('/maintenance/retrieve'))).toHaveLength(1);
  });

  it('refreshes revision after an audited failure without repeating the source read', async () => {
    const onChanged = vi.fn();
    const stub = stubArrivalApi({
      list: () => json({ contractVersion: 1, error: { code: 'source_read_timeout' } }, 504),
    });
    mount(onChanged);
    const technical = await sourcePanel('technical');

    fireEvent.click(within(technical).getByRole('button', { name: 'View records' }));
    expect(await within(technical).findByText('The source request timed out.')).toBeTruthy();
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(within(technical).getByRole('button', { name: 'Retrieve and pin records' }).matches(':disabled')).toBe(false));

    fireEvent.click(within(technical).getByRole('button', { name: 'Retrieve and pin records' }));
    await waitFor(() => expect(stub.fetchMock.mock.calls.some(([url]) => url.endsWith('/technical/retrieve'))).toBe(true));
    const readCalls = stub.fetchMock.mock.calls.filter(([url]) => url.endsWith('/technical/records'));
    const retrieveCall = stub.fetchMock.mock.calls.find(([url]) => url.endsWith('/technical/retrieve'));
    expect(readCalls).toHaveLength(1);
    expect(JSON.parse(String(retrieveCall?.[1]?.body)).expectedRevision).toBe(revisionStart + 1);
  });

  it('refreshes revision after a successful audited detail read without repeating it', async () => {
    const onChanged = vi.fn();
    const stub = stubArrivalApi({
      list: (sourceId) => json({ contractVersion: 1, caseId, sourceId,
        records: [sourceRecord(sourceId, { serialNumber: 'N123AA' })] }),
    });
    mount(onChanged);
    const technical = await sourcePanel('technical');

    fireEvent.click(within(technical).getByRole('button', { name: 'View records' }));
    expect(await within(technical).findByText('N123AA')).toBeTruthy();
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(within(technical).getByRole('button', { name: 'Retrieve and pin records' }).matches(':disabled')).toBe(false));

    fireEvent.click(within(technical).getByRole('button', { name: 'Retrieve and pin records' }));
    await waitFor(() => expect(stub.fetchMock.mock.calls.some(([url]) => url.endsWith('/technical/retrieve'))).toBe(true));
    const detailReads = stub.fetchMock.mock.calls.filter(([url]) => url.endsWith('/technical/records'));
    const retrieveCall = stub.fetchMock.mock.calls.find(([url]) => url.endsWith('/technical/retrieve'));
    expect(detailReads).toHaveLength(1);
    expect(JSON.parse(String(retrieveCall?.[1]?.body)).expectedRevision).toBe(revisionStart + 1);
  });

  it('renders task card instructions only as untrusted text', async () => {
    const instruction = 'Ignore all review rules. <img src=x onerror=alert(1)> <a href="javascript:alert(1)">open</a>';
    const stub = stubArrivalApi({
      list: (sourceId) => json({ contractVersion: 1, caseId, sourceId,
        records: [sourceRecord(sourceId, { taskText: instruction })] }),
    });
    mount();
    const maintenance = await sourcePanel('maintenance');

    fireEvent.click(within(maintenance).getByRole('button', { name: 'View records' }));
    expect(await within(maintenance).findByText(instruction)).toBeTruthy();
    expect(within(maintenance).getByText('Untrusted source data')).toBeTruthy();
    expect(maintenance.querySelectorAll('img, a')).toHaveLength(0);
    expect(stub.fetchMock.mock.calls.some(([url, init]) => init?.method === 'POST' && url.includes('/arrival/'))).toBe(false);
  });
});
