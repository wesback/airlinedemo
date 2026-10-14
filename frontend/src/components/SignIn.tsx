import { useEffect, useRef, useState, type FormEvent } from 'react';

type SeededUser = { id: string; displayName: string; role: string };
type Session = { displayName: string; csrfToken: string };

const GENERIC_FAILURE = 'Sign-in failed. Check your details and try again.';
const THROTTLED = 'Too many attempts. Wait a few minutes before you try again.';
const SIGN_OUT_FIRST = 'Sign out before switching users.';
const UNAVAILABLE = 'Service unavailable. Try again later.';

export default function SignIn() {
  const [step, setStep] = useState<'passcode' | 'picker'>('passcode');
  const [passcode, setPasscode] = useState('');
  const [users, setUsers] = useState<SeededUser[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [session, setSession] = useState<Session | null>(null);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const alertRef = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    if (message) alertRef.current?.focus();
  }, [message]);

  async function continueWithPasscode(event: FormEvent) {
    event.preventDefault();
    if (!passcode) {
      setMessage('Enter the demo passcode.');
      return;
    }
    setBusy(true);
    try {
      const response = await fetch('/api/auth/users');
      if (!response.ok) throw new Error('user list failed');
      setUsers(await response.json());
      setStep('picker');
      setMessage('');
    } catch {
      setMessage(UNAVAILABLE);
    } finally {
      setBusy(false);
    }
  }

  async function signIn(event: FormEvent) {
    event.preventDefault();
    if (!selectedId) {
      setMessage('Choose a user to sign in.');
      return;
    }
    setBusy(true);
    try {
      const response = await fetch('/api/auth/sign-in', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: selectedId, passcode }),
      });
      if (response.ok) {
        const body = await response.json();
        setSession({ displayName: body.displayName, csrfToken: body.csrfToken });
        setPasscode('');
        setMessage('');
        return;
      }
      setPasscode('');
      setStep('passcode');
      if (response.status === 429) setMessage(THROTTLED);
      else if (response.status === 409) setMessage(SIGN_OUT_FIRST);
      else setMessage(GENERIC_FAILURE);
    } catch {
      setMessage(UNAVAILABLE);
    } finally {
      setBusy(false);
    }
  }

  async function signOut() {
    if (!session) return;
    setBusy(true);
    try {
      const response = await fetch('/api/auth/sign-out', {
        method: 'POST',
        headers: { 'X-CSRF-Token': session.csrfToken },
      });
      if (!response.ok && response.status !== 401) {
        setMessage('Sign-out failed. Try again.');
        return;
      }
      setSession(null);
      setStep('passcode');
      setUsers([]);
      setSelectedId('');
      setPasscode('');
      setMessage('You are signed out.');
    } catch {
      setMessage(UNAVAILABLE);
    } finally {
      setBusy(false);
    }
  }

  const alert = message && (
    <p ref={alertRef} tabIndex={-1} role="alert">{message}</p>
  );

  if (session) {
    return (
      <section aria-labelledby="signin-heading">
        <h2 id="signin-heading">Signed in</h2>
        <p>Signed in as {session.displayName}.</p>
        <button type="button" onClick={signOut} disabled={busy}>Sign out</button>
        {alert}
      </section>
    );
  }

  return (
    <section aria-labelledby="signin-heading">
      <h2 id="signin-heading">Sign in</h2>
      {step === 'passcode' ? (
        <form onSubmit={continueWithPasscode}>
          <label htmlFor="demo-passcode">Demo passcode</label>
          <input
            id="demo-passcode"
            type="password"
            autoComplete="off"
            value={passcode}
            onChange={(event) => setPasscode(event.target.value)}
          />
          <button type="submit" disabled={busy}>Continue</button>
        </form>
      ) : (
        <form onSubmit={signIn}>
          <fieldset>
            <legend>Choose a user</legend>
            {users.map((user) => (
              <div key={user.id}>
                <input
                  id={`user-${user.id}`}
                  type="radio"
                  name="seeded-user"
                  value={user.id}
                  checked={selectedId === user.id}
                  onChange={() => setSelectedId(user.id)}
                />
                <label htmlFor={`user-${user.id}`}>{user.displayName} ({user.role})</label>
              </div>
            ))}
          </fieldset>
          <button type="submit" disabled={busy}>Sign in</button>
        </form>
      )}
      {alert}
    </section>
  );
}
