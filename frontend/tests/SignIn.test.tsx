import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
  const all: Record<string, (init?: RequestInit) => Response> = {
    'POST /api/auth/passcode': () => new Response(null, { status: 204 }),
    ...handlers,
  };
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const handler = all[`${init?.method ?? 'GET'} ${url}`];
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
  localStorage.clear();
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
    const fetchMock = stubApi({
      'POST /api/auth/passcode': () => json({ error: 'Sign-in failed.' }, 401),
      'GET /api/auth/users': () => json(users),
    });
    render(<SignIn />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('Sign-in failed. Check your details and try again.');
    expect(alert.textContent).not.toMatch(/user|passcode|password/i);
    expect(screen.queryByRole('radio')).toBeNull();
    expect(screen.getByLabelText('Demo passcode')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/auth/users')).toBe(false);
  });

  it('checks the passcode before loading users, with no session token', async () => {
    const fetchMock = stubApi({ 'GET /api/auth/users': () => json(users) });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ada');
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === '/api/auth/users')).toBe(true));

    const urls = fetchMock.mock.calls.map(([url]) => url);
    expect(urls.indexOf('/api/auth/passcode')).toBeGreaterThanOrEqual(0);
    expect(urls.indexOf('/api/auth/passcode')).toBeLessThan(urls.indexOf('/api/auth/users'));
    const passcodeCall = fetchMock.mock.calls.find(([url]) => url === '/api/auth/passcode');
    const headers = passcodeCall?.[1]?.headers as Record<string, string>;
    expect(headers['X-CSRF-Token']).toBeUndefined();
    expect(JSON.parse(String(passcodeCall?.[1]?.body))).toEqual({ passcode: 'demo-pass' });
  });

  it('keeps the picker hidden and tells the user to wait when the passcode check is throttled', async () => {
    const fetchMock = stubApi({
      'POST /api/auth/passcode': () => json({ error: 'Sign-in failed.' }, 429),
      'GET /api/auth/users': () => json(users),
    });
    render(<SignIn />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'anything' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Wait a few minutes');
    expect(screen.queryByRole('radio')).toBeNull();
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/auth/users')).toBe(false);
  });

  it('does not request or show the seeded users until a passcode is entered', async () => {
    const fetchMock = stubApi({ 'GET /api/auth/users': () => json(users) });
    render(<SignIn />);

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Enter the demo passcode.');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.queryByRole('radio')).toBeNull();
  });

  it('sends sign-in as JSON with no session token and keeps the picker hidden after a rejected sign-in', async () => {
    const fetchMock = stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ error: 'Sign-in failed.' }, 401),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('wrong', 'Ada');
    await screen.findByRole('alert');

    const signInCall = fetchMock.mock.calls.find(([url]) => url === '/api/auth/sign-in');
    const headers = signInCall?.[1]?.headers as Record<string, string>;
    expect(headers['Content-Type']).toBe('application/json');
    expect(headers['X-CSRF-Token']).toBeUndefined();
    expect(screen.queryByRole('radio')).toBeNull();
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
    expect(screen.queryByText(/Signed in as/)).toBeNull();
    expect((screen.getByLabelText('Demo passcode') as HTMLInputElement).value).toBe('');
    expect(screen.queryByRole('radio')).toBeNull();
  });

  it('provides App the signed-in identity and CSRF token only after sign-in succeeds', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({
        id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer',
        caseScopes: ['case-001'], csrfToken: 'csrf-123',
      }),
    });
    const onSessionChange = vi.fn();
    render(<SignIn onSessionChange={onSessionChange} />);
    await enterPasscodeAndPick('demo-pass', 'Ada');

    expect(await screen.findByText('Signed in as Ada Reviewer.')).toBeTruthy();
    expect(onSessionChange).toHaveBeenCalledTimes(1);
    expect(onSessionChange).toHaveBeenCalledWith({
      id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer',
      caseScopes: ['case-001'], csrfToken: 'csrf-123',
    });
    expect(screen.queryByRole('radio')).toBeNull();
    expect(screen.queryByLabelText('Demo passcode')).toBeNull();
  });

  it('AC_005_branded_signin_persona_contract', async () => {
    const fixtureUsers = [
      { id: 'presenter', displayName: 'Demo presenter', role: 'presenter' },
      { id: 'reviewer', displayName: 'Technical reviewer', role: 'technical-reviewer' },
      { id: 'fin-1', displayName: 'Finance reviewer', role: 'finance-reviewer' },
      { id: 'legal-1', displayName: 'Legal reviewer', role: 'legal-reviewer' },
      { id: 'exec-1', displayName: 'Executive observer', role: 'executive' },
    ];
    localStorage.setItem('altivane.lastUserId', 'legal-1');
    stubApi({
      'GET /api/auth/users': () => json(fixtureUsers),
    });
    render(<SignIn />);

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('heading', { name: 'Asset Transitions' })).toBeTruthy();
    for (const user of fixtureUsers) {
      expect(screen.getByRole('radio', { name: `${user.displayName} (${user.role})` })).toBeTruthy();
    }
    await waitFor(() => {
      expect((screen.getByRole('radio', { name: 'Legal reviewer (legal-reviewer)' }) as HTMLInputElement).checked).toBe(true);
    });
    expect(screen.getByText('Review aircraft readiness through the role-based workspace your stakeholders use every day.')).toBeTruthy();
    expect(screen.getByText('Guides the aircraft story and demo flow.')).toBeTruthy();
    expect(screen.getByText('Reviews findings and decides case actions.')).toBeTruthy();
  });

  it('local_storage_and_sign_in_secrets', async () => {
    const fetchMock = stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ id: 'agent-1', displayName: 'Ada Reviewer', role: 'Reviewer', caseScopes: ['case-001'], csrfToken: 'csrf-123' }),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ada');

    expect(await screen.findByText('Signed in as Ada Reviewer.')).toBeTruthy();
    expect(Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index))).toEqual(['altivane.lastUserId']);
    expect(localStorage.getItem('altivane.lastUserId')).toBe('agent-1');
    const scan = Array.from({ length: localStorage.length }, (_, index) => localStorage.getItem(localStorage.key(index) ?? '') ?? '').join('\n');
    expect(scan).not.toContain('demo-pass');
    expect(scan).not.toContain('csrf-123');
    expect(scan).not.toContain('session');
    expect(fetchMock.mock.calls.some(([url]) => url === '/api/auth/sign-in')).toBe(true);
  });

  it('shows the sign-out-first message when the server refuses a switch', async () => {
    stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ error: 'Sign out before switching users.' }, 409),
    });
    render(<SignIn />);
    await enterPasscodeAndPick('demo-pass', 'Ben');

    expect((await screen.findByRole('alert')).textContent).toBe('Sign out before switching users.');
    expect(screen.getByRole('button', { name: 'Continue' })).toBeTruthy();
    expect(screen.getByLabelText('Demo passcode')).toBeTruthy();
    expect(screen.queryByRole('radio')).toBeNull();
  });

  it('moves focus to the error message when it appears', async () => {
    stubApi({ 'GET /api/auth/users': () => new Response(null, { status: 503 }) });
    render(<SignIn />);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    const alert = await screen.findByRole('alert');
    await waitFor(() => expect(document.activeElement).toBe(alert));
  });

  it('labels every control on both steps', async () => {
    stubApi({ 'GET /api/auth/users': () => json(users) });
    render(<SignIn />);

    expect(screen.getByLabelText('Demo passcode').tagName).toBe('INPUT');
    expect(screen.getByRole('button', { name: 'Continue' }).tagName).toBe('BUTTON');

    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('group', { name: 'Choose a user' })).toBeTruthy();
    for (const radio of screen.getAllByRole('radio')) {
      expect(radio.tagName).toBe('INPUT');
      expect(radio.getAttribute('type')).toBe('radio');
      expect(radio.getAttribute('name')).toBe('seeded-user');
      expect(radio.getAttribute('id')).toBeTruthy();
      expect(radio.getAttribute('aria-label')).toBeNull();
    }
    expect(screen.getByRole('button', { name: 'Sign in' }).getAttribute('type')).toBe('submit');
  });

  it('is keyboard-operable: Enter submits the passcode, Space selects a user, Enter signs in', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      'GET /api/auth/users': () => json(users),
      'POST /api/auth/sign-in': () => json({ id: 'agent-2', displayName: 'Ben Approver', role: 'Approver', csrfToken: 'csrf-456' }),
    });
    render(<SignIn />);

    await user.type(screen.getByLabelText('Demo passcode'), 'demo-pass{Enter}');
    const radio = await screen.findByRole('radio', { name: 'Ben Approver (Approver)' });
    radio.focus();
    await user.keyboard(' ');
    expect((radio as HTMLInputElement).checked).toBe(true);

    screen.getByRole('button', { name: 'Sign in' }).focus();
    await user.keyboard('{Enter}');

    expect(await screen.findByText('Signed in as Ben Approver.')).toBeTruthy();
    const signInCall = fetchMock.mock.calls.find(([url]) => url === '/api/auth/sign-in');
    expect(JSON.parse(String(signInCall?.[1]?.body))).toEqual({ userId: 'agent-2', passcode: 'demo-pass' });
  });

  it('keeps errors as text, not colour alone, and announces them in an alert region', async () => {
    stubApi({ 'GET /api/auth/users': () => json(users) });
    render(<SignIn />);

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('Enter the demo passcode.');
    expect(alert.getAttribute('role')).toBe('alert');
  });
});
