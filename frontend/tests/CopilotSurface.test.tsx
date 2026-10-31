import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import CopilotPanel, { COPILOT_NOTICE } from '../src/journey/CopilotPanel';

const reviewer = { id: 'reviewer', displayName: 'Riley Reviewer', role: 'technical-reviewer', caseScopes: ['case-001', 'cp-case-c'], csrfToken: 'csrf-r' };
const presenter = { id: 'presenter', displayName: 'Demo Presenter', role: 'presenter', caseScopes: ['case-001'], csrfToken: 'csrf-p' };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const hostileAnswer = 'Check <script>alert(1)</script> and <img src=x onerror="alert(2)"> then [x](javascript:alert(3)).';
const record = (recordId: string, payload: unknown) => ({
  sourceId: 'technical', recordId, recordVersion: 2, asOf: '2026-10-30T10:00:00Z', caseId: 'case-001', kind: 'task', stale: false, payload, synthetic: true,
});
const answer = {
  contractVersion: 1, answer: hostileAnswer,
  citations: [{ sourceId: 'technical', recordId: 'rec-2', recordVersion: 2 }],
  records: [record('rec-1', { note: 'first' }), record('rec-2', { note: '<b>bold</b><img src=x onerror=alert(4)>' })],
};
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it('CopilotSurface_EscapedAnswerRecordAndIndependentNavigation', async () => {
  const fetcher = vi.fn(async (_url: string, _init?: RequestInit) => json(answer));
  vi.stubGlobal('fetch', fetcher);
  const { container } = render(<CopilotPanel session={reviewer} caseId="case-001" />);
  fireEvent.change(screen.getByRole('textbox', { name: 'Your question' }), { target: { value: 'What is open?' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  const text = await screen.findByText(hostileAnswer);
  expect(text.tagName).toBe('P');
  expect(text.childElementCount).toBe(0);
  expect(container.querySelectorAll('script, img, a, b, iframe')).toHaveLength(0);
  expect(fetcher.mock.calls).toHaveLength(1);
  const [url, init] = fetcher.mock.calls[0];
  expect(url).toBe('/api/v1/copilot/questions');
  expect(init?.method).toBe('POST');
  expect(init?.headers).toEqual({ 'Content-Type': 'application/json', 'X-CSRF-Token': 'csrf-r' });
  expect(init?.body).toBe(JSON.stringify({ question: 'What is open?', selectedCaseId: 'case-001' }));

  const pane = screen.getByRole('region', { name: 'Source records (untrusted data)' });
  const hostile = within(pane).getByRole('article', { name: 'technical record rec-2 v2' });
  expect(hostile.querySelector('pre')?.textContent).toBe(JSON.stringify({ note: '<b>bold</b><img src=x onerror=alert(4)>' }, null, 2));
  expect(hostile.getAttribute('aria-current')).toBeNull();
  const citation = screen.getByRole('button', { name: 'Show technical record rec-2 v2' });
  expect(pane.contains(citation)).toBe(false);
  fireEvent.click(citation);
  expect(hostile.getAttribute('aria-current')).toBe('true');
  expect(document.activeElement).toBe(hostile);
  expect(citation.getAttribute('aria-pressed')).toBe('true');
  expect(within(pane).getByRole('article', { name: 'technical record rec-1 v2' }).getAttribute('aria-current')).toBeNull();
});

it('CopilotSurface_SingleQuestionNoticeNoPersistentHistory', async () => {
  const writes = vi.spyOn(Storage.prototype, 'setItem');
  const fetcher = vi.fn(async (_url: string, _init?: RequestInit) => json(answer));
  vi.stubGlobal('fetch', fetcher);
  const { rerender } = render(<CopilotPanel session={reviewer} caseId="case-001" />);
  expect(screen.getByText(COPILOT_NOTICE)).toBeTruthy();
  expect(COPILOT_NOTICE).toBe('Synthetic demo only. Do not enter personal, confidential, live aircraft or customer data. Your question is sent to the governed model.');
  const box = screen.getByRole('textbox', { name: 'Your question' });
  fireEvent.change(box, { target: { value: 'First question' } });
  rerender(<CopilotPanel session={reviewer} caseId="case-001" />);
  expect(fetcher).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  await screen.findByText(hostileAnswer);
  expect(fetcher).toHaveBeenCalledTimes(1);

  rerender(<CopilotPanel session={reviewer} caseId="cp-case-c" />);
  expect(screen.queryByText(hostileAnswer)).toBeNull();
  expect(screen.queryByRole('region', { name: 'Source records (untrusted data)' })).toBeNull();
  expect((screen.getByRole('textbox', { name: 'Your question' }) as HTMLTextAreaElement).value).toBe('');
  expect(fetcher).toHaveBeenCalledTimes(1);

  fireEvent.change(screen.getByRole('textbox', { name: 'Your question' }), { target: { value: 'Second question' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  await screen.findByText(hostileAnswer);
  rerender(<CopilotPanel session={{ ...reviewer, id: 'fin-1', role: 'finance-reviewer' }} caseId="cp-case-c" />);
  expect(screen.queryByText(hostileAnswer)).toBeNull();
  expect((screen.getByRole('textbox', { name: 'Your question' }) as HTMLTextAreaElement).value).toBe('');

  rerender(<CopilotPanel session={reviewer} caseId={null} />);
  fireEvent.change(screen.getByRole('textbox', { name: 'Your question' }), { target: { value: 'Which cases are mine?' } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  await screen.findByText(hostileAnswer);
  expect(fetcher.mock.calls[2][1]?.body).toBe(JSON.stringify({ question: 'Which cases are mine?' }));
  expect(writes).not.toHaveBeenCalled();
});

it('CopilotSurface_KeyboardLabelsAndLiveState', async () => {
  const user = userEvent.setup();
  let release: (response: Response) => void = () => undefined;
  const fetcher = vi.fn((_url: string, _init?: RequestInit) => new Promise<Response>((resolve) => { release = resolve; }));
  vi.stubGlobal('fetch', fetcher);
  const { rerender } = render(<CopilotPanel session={reviewer} caseId="case-001" />);
  expect(screen.getByRole('complementary', { name: 'Case copilot' })).toBeTruthy();
  expect(screen.queryByRole('textbox', { name: 'Release note (optional, presenter only)' })).toBeNull();
  rerender(<CopilotPanel session={presenter} caseId="case-001" />);
  const status = screen.getByRole('status');
  expect(status.getAttribute('aria-live')).toBe('polite');
  expect(status.textContent).toBe('');

  await user.tab();
  expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Your question' }));
  await user.keyboard('Is it ready?');
  await user.tab();
  expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Release note (optional, presenter only)' }));
  await user.keyboard('Ready to go');
  await user.tab();
  const send = screen.getByRole('button', { name: 'Send' });
  expect(document.activeElement).toBe(send);
  await user.keyboard('{Enter}');
  expect(status.textContent).toBe('Asking the copilot…');
  expect(send.getAttribute('aria-busy')).toBe('true');
  expect((send as HTMLButtonElement).disabled).toBe(true);
  expect(fetcher.mock.calls[0][1]?.body).toBe(JSON.stringify({ question: 'Is it ready?', selectedCaseId: 'case-001', releaseNote: 'Ready to go' }));
  await act(async () => { release(json({ contractVersion: 1, error: { code: 'model_unavailable' } }, 503)); });
  expect((await screen.findByRole('alert')).textContent).toBe('The governed model is unavailable. Try again later.');
  expect(status.textContent).toBe('');
  expect(send.getAttribute('aria-busy')).toBe('false');

  send.focus();
  await user.keyboard(' ');
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
  await act(async () => { release(json({ contractVersion: 1, error: { code: 'case_selection_required' } }, 409)); });
  expect((await screen.findByRole('alert')).textContent).toBe('Open a case first. Without a selected case the copilot can only list your cases.');
  send.focus();
  await user.keyboard('{Enter}');
  await act(async () => { release(json({ ...answer, records: [], citations: [] })); });
  expect(status.textContent).toBe('Answer received.');
  expect(screen.queryByRole('alert')).toBeNull();
  expect(screen.getByText(hostileAnswer)).toBeTruthy();
});
