import { fireEvent, screen } from '@testing-library/react';
import { vi } from 'vitest';

/** Stubs the API calls App makes for one signed-in technical reviewer; a model (.glb/.gltf) request calls `onModelRequest`. */
export function stubCaseApi(onModelRequest: () => void = () => {}) {
  const envelope = (caseId: string, name: string, category: string, revision: number) => {
    const letter = caseId.slice(-1).toUpperCase();
    return { contractVersion: 1, packVersion: '1.0.2', case: {
      caseId, caseRevision: revision, status: { assessment: 'ready', review: null, request: null, finance: null, legal: null, acceptance: null },
      owner: 'Technical reviewer', nextAction: { actionId: 'review', text: 'Review evidence' }, name, aircraftName: name, aircraftCategory: category,
      capabilityLabel: 'Rules-based', permission: true,
    }, canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [], objects: [
      { objectId: `SYN-CP-${letter}-AIR-001`, kind: 'aircraft', caseId },
      { objectId: `SYN-CP-${letter}-ENG-001`, kind: 'engine', caseId },
      { objectId: `SYN-CP-${letter}-CMP-001`, kind: 'component', caseId },
      { objectId: `SYN-CP-${letter}-CMP-002`, kind: 'component', caseId },
    ] };
  };
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    if (/\.(?:glb|gltf)(?:\?|$)/i.test(url)) onModelRequest();
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (url === '/api/health') return json({ status: 'ok' });
    if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
    if (url === '/api/auth/users') return json([{ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer' }]);
    if (url === '/api/auth/sign-in') return json({ id: 'reviewer-1', displayName: 'Ada Reviewer', role: 'technical-reviewer', caseScopes: ['case-001'], csrfToken: 'token' });
    if (url === '/api/cases/case-001' && method === 'GET') return json({ revision: 7, basis: [{ version: 4 }] });
    if (url === '/api/cases/case-001/dashboard' && method === 'GET') return json({ allowedDecisions: [], capabilities: [{ id: 'technical-review', name: 'Technical review', label: 'Human decision', designOnly: false, openItems: 0, nextDecisionOwner: 'Technical reviewer', evidence: [], design: null }], queue: [], activity: [] });
    if (url === '/api/cases/case-001/technical-reviews' && method === 'GET') return json({ basisVersion: 4, decisions: [] });
    if (url === '/api/cases/case-001/readiness' && method === 'GET') return json({ monthsUntilReturn: 9, gapsFound: 1, requestsSent: 1, heldForReview: 1, notProcessed: 2, sentence: 'Review is ready.' });
    if (url === '/api/v1/cases' && method === 'GET') return json({ contractVersion: 1, packVersion: '1.0.2', cases: [
      { caseId: 'cp-case-a', name: 'Air One', aircraftCategory: 'narrow-body', caseRevision: 7 },
      { caseId: 'cp-case-b', name: 'Air Two', aircraftCategory: 'regional', caseRevision: 7 },
      { caseId: 'cp-case-c', name: 'Air Three', aircraftCategory: 'wide-body', caseRevision: 7 },
    ] });
    const sceneMatch = url.match(/^\/api\/v1\/cases\/([^/]+)\/scene$/);
    if (sceneMatch && method === 'GET') {
      const metadata: Record<string, [string,string]> = { 'cp-case-a': ['Air One','narrow-body'], 'cp-case-b': ['Air Two','regional'], 'cp-case-c': ['Air Three','wide-body'] };
      const caseId = sceneMatch[1]; const details = metadata[caseId];
      if (!details) throw new Error(`Unexpected request: ${method} ${url}`);
      return json(envelope(caseId, details[0], details[1], 7));
    }
    throw new Error(`Unexpected request: ${method} ${url}`);
  }));
}

/** Signs in as the stubbed reviewer and waits for the case dashboard. */
export async function signInAsReviewer() {
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await screen.findByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' });
  fireEvent.click(screen.getByRole('radio', { name: 'Ada Reviewer (technical-reviewer)' }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByText('Revision: 7');
}
