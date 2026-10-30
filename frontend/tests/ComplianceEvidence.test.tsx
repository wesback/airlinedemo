import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import ComplianceEvidence from '../src/journey/ComplianceEvidence';

const decision = { sequence: 3, auditEventId: 'evt-01', actorId: 'reviewer', role: 'technical-reviewer', at: '2026-10-30T14:00:00Z', decision: 'accept' };
const reference = { sourceId: 'records', recordId: 'record-01', recordVersion: 1, access: 'not_permitted' };
const requirement = {
  requirementId: 'REQ-GDPR', requirementVersion: 1, responsibleParty: 'lessor', status: 'not_assessed',
  applicability: { sourceId: 'technical', recordId: 'tag-01', recordVersion: 1, access: 'not_permitted' },
  evidence: [reference], decision: null,
};
const data = {
  basisVersion: 1, currentBasis: false, applicabilityAccess: 'permitted',
  regimes: [{ regime: 'GDPR', requirements: [requirement] }, { regime: 'UK sanctions', requirements: [{ ...requirement, requirementId: 'REQ-UK', status: 'needs_review' }] }],
  untagged: [], basisDecision: decision, decisions: [decision, { ...decision, sequence: 7 }],
  responsibilities: {
    platform: ['Access control', 'Audit trail', 'Pinned evidence'],
    customer: ['Determine applicable obligations', 'Review evidence and record decisions', 'Obtain specialist advice'],
    limitation: 'Evidence only. Not a certification, approval or legal conclusion. The customer remains responsible for its decisions.',
  },
};
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('uses only server regimes, shows pinned metadata and responsibility, and links the exact activity entry', async () => {
  const fetcher = vi.fn(async (_url: string) => json(data));
  vi.stubGlobal('fetch', fetcher);
  const { container } = render(<><ComplianceEvidence caseId="case-001" onClose={() => undefined} /><ol><li id="case-activity-3" tabIndex={-1}>Review activity</li></ol></>);
  expect(await screen.findByText('Evidence basis v1 · Superseded basis, retained for this decision')).toBeTruthy();
  const select = screen.getByRole('combobox', { name: 'Regulatory regime' });
  expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual(['GDPR', 'UK sanctions']);
  const gdpr = screen.getByRole('article', { name: 'REQ-GDPR' });
  expect(within(gdpr).getByText('Not assessed')).toBeTruthy();
  expect(within(gdpr).getByText('record-01')).toBeTruthy();
  expect(gdpr.textContent).toContain('v1');
  expect(within(gdpr).getAllByText('· Source access not permitted')).toHaveLength(2);
  expect(within(gdpr).getByText('No requirement-specific decision recorded.')).toBeTruthy();
  const responsibility = screen.getByRole('region', { name: 'Shared responsibility' });
  expect(within(responsibility).getAllByRole('listitem').map((item) => item.textContent))
    .toEqual([...data.responsibilities.platform, ...data.responsibilities.customer]);
  fireEvent.click(screen.getByRole('link', { name: 'Activity #3' }));
  expect(document.activeElement?.id).toBe('case-activity-3');
  const basis = screen.getByRole('region', { name: 'Recorded basis review' });
  expect(basis.textContent).toContain('reviewer · technical-reviewer');
  expect(within(basis).getByText('evt-01')).toBeTruthy();
  expect(basis.querySelector('time')?.dateTime).toBe(decision.at);
  for (const phrase of ['compliant', 'clearance granted', 'legally approved', 'legal advice', 'compliance guaranteed'])
    expect(container.textContent?.toLowerCase()).not.toContain(phrase);
  fireEvent.change(select, { target: { value: 'UK sanctions' } });
  expect(screen.queryByRole('article', { name: 'REQ-GDPR' })).toBeNull();
  expect(within(screen.getByRole('article', { name: 'REQ-UK' })).getByText('Needs review')).toBeTruthy();
  fireEvent.change(screen.getByRole('combobox', { name: 'Decision basis' }), { target: { value: '7' } });
  await screen.findByText('REQ-UK');
  expect(fetcher.mock.calls.map((call) => call[0])).toEqual([
    '/api/v1/cases/case-001/compliance-evidence', '/api/v1/cases/case-001/compliance-evidence?decisionId=7',
  ]);
});

it('fails closed for missing evidence or tags even if a response incorrectly says met by evidence', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => json({
    ...data, regimes: [{ regime: 'GDPR', requirements: [{ ...requirement, status: 'met_by_evidence', evidence: [] }] }],
    untagged: [{ ...requirement, requirementId: 'REQ-UNTAGGED', applicability: null, responsibleParty: null, status: 'met_by_evidence' }],
  })));
  render(<ComplianceEvidence caseId="case-001" onClose={() => undefined} />);
  const gdpr = await screen.findByRole('article', { name: 'REQ-GDPR' });
  expect(within(gdpr).getByText('Needs review')).toBeTruthy();
  expect(within(gdpr).getByText('No evidence reference recorded.')).toBeTruthy();
  expect(within(screen.getByRole('article', { name: 'REQ-UNTAGGED' })).getByText('Needs review')).toBeTruthy();
  expect(screen.queryByText('Met by evidence')).toBeNull();
});

it('shows restricted references without inventing identities and performs no mutations', async () => {
  const fetcher = vi.fn(async () => json({
    ...data, regimes: [{ regime: 'GDPR', requirements: [{ ...requirement, status: 'met_by_evidence', evidence: [{ ...reference, recordId: null, recordVersion: null }] }] }],
  }));
  vi.stubGlobal('fetch', fetcher);
  const close = vi.fn();
  render(<ComplianceEvidence caseId="case-001" onClose={close} />);
  const article = await screen.findByRole('article', { name: 'REQ-GDPR' });
  expect(article.textContent).not.toContain('record-01');
  expect(article.textContent).not.toContain('vnull');
  expect(article.textContent).toContain('Source access not permitted');
  expect(within(article).getByText('Needs review')).toBeTruthy();
  expect(screen.queryByRole('button', { name: /approve|accept|save|export/i })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Back to journey' }));
  expect(close).toHaveBeenCalledOnce();
  expect(fetcher.mock.calls).toEqual([['/api/v1/cases/case-001/compliance-evidence']]);
});

it('labels navigation to the separate source case without representing its records as aircraft evidence', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => json(data)));
  const open = vi.fn();
  render(<ComplianceEvidence caseId="cp-case-a" onClose={() => undefined} sourceCase={{ id: 'case-001', open }} />);
  const link = screen.getByRole('link', { name: 'Open source-system case' });
  expect(link.getAttribute('href')).toBe('/cases/case-001');
  expect(screen.getByText('The specialist source records belong to a different synthetic case. They are not evidence for this aircraft.')).toBeTruthy();
  fireEvent.click(link);
  expect(open).toHaveBeenCalledOnce();
  await screen.findByRole('article', { name: 'REQ-GDPR' });
});

it('does not retain another case response and does not present a failed read as an assessment', async () => {
  let resolveFirst!: (response: Response) => void;
  vi.stubGlobal('fetch', vi.fn((url: string) => url.includes('case-001')
    ? new Promise<Response>((resolve) => { resolveFirst = resolve; }) : Promise.resolve(json({}, 404))));
  const { rerender } = render(<ComplianceEvidence caseId="case-001" onClose={() => undefined} />);
  rerender(<ComplianceEvidence caseId="case-002" onClose={() => undefined} />);
  expect((await screen.findByRole('alert')).textContent).toBe('This evidence view is not permitted or no longer available.');
  resolveFirst(json(data));
  expect(screen.queryByRole('combobox', { name: 'Regulatory regime' })).toBeNull();
});
