import { fireEvent, screen } from '@testing-library/react';
import { vi } from 'vitest';

/** Stubs the API calls App makes for one signed-in technical reviewer; a model (.glb/.gltf) request calls `onModelRequest`. */
export function stubCaseApi(onModelRequest: () => void = () => {}) {
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
