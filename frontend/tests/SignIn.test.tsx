import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import SignIn from '../src/components/SignIn';

const users = [
  { id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer' },
  { id: 'agent-2', displayName: 'Ben Approver', role: 'Approver' },
];

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubApi(handlers: Record<string, (init?: RequestInit) => Response>) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const handler = handlers[`${init?.method ?? 'GET'} ${url}`];
    if (!handler) throw new Error(`unexpected request ${init?.method ?? 'GET'} ${url}`);
    return handler(init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

async function enterPasscodeAndPick(passcode: string, userName: string) {
  fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: passcode } });
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  fireEvent.click(await screen.findByRole('radio', { name: new RegExp(userName) }));
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('sign-in page', () => {
  it('shows the seeded users only after the passcode step', async () => {
    stubApi({ 'GET /api/auth/users': () => json(users) });
    render(<SignIn />);

    expect(screen.queryByRole('radio')).toBeNull();
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('radio', { name: 'Ada Reviewer (Reviewer)' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Ben Approver (Approver)' })).toBeTruthy();
  });

  it('shows one generic message for a wrong passcode without saying which part was wrong', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ error: 'Sign-in failed.' }, 401),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('wrong', 'Ada');

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('Sign-in failed. Check your details and try again.');
    expect(alert.textContent).not.toMatch(/user|passcode|password/i);
  });

  it('tells the user to wait when the response is throttled', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ error: 'Sign-in failed.' }, 429),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('anything', 'Ada');

    expect((await screen.findByRole('alert')).textContent).toContain('Wait a few minutes');
  });

  it('sends the anti-forgery token in the header on sign-out', async () => {
    const fetchMock = stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer', csrfToken: 'csrf-123' }),
      'POST /api/auth/sign-out': () => new Response(null, { status: 204 }),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ada');
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    await waitFor(() => expect(screen.getByRole('alert').textContent).toBe('You are signed out.'));
    const signOutCall = fetchMock.mock.calls.find(([url]) => url === '/api/auth/sign-out');
    expect((signOutCall?.[1]?.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-123');
  });

  it('forces sign-out before switching users: no picker or passcode is shown while signed in', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer', csrfToken: 'csrf-123' }),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ada');

    expect(await screen.findByText('Signed in as Ada Reviewer.')).toBeTruthy();
    expect(screen.queryByRole('radio')).toBeNull();
    expect(screen.queryByLabelText('Demo passcode')).toBeNull();
  });

  it('shows the sign-out-first message when the server refuses a switch', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ error: 'Sign out before switching users.' }, 409),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ben');

    expect((await screen.findByRole('alert')).textContent).toBe('Sign out before switching users.');
  });

  it('moves focus to the error message when it appears', async () => {
    stubApi({ 'GET /api/auth/users': () => new Response(null, { status: 503 }) });
    render(<SignIn />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    const alert = await screen.findByRole('alert');
    await waitFor(() => expect(document.activeElement).toBe(alert));
  });
});
