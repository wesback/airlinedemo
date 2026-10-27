import { useEffect, useRef, useState, type FormEvent } from 'react';

type SeededUser = { id: string; displayName: string; role: string; description?: string };
export type SignedInSession = { id: string; displayName: string; role: string; caseScopes: string[]; csrfToken: string };

const GENERIC_FAILURE = 'Sign-in failed. Check your details and try again.';
const THROTTLED = 'Too many attempts. Wait a few minutes before you try again.';
const SIGN_OUT_FIRST = 'Sign out before switching users.';
const UNAVAILABLE = 'Service unavailable. Try again later.';
const LAST_USER_ID_KEY = 'altivane.lastUserId';

const PERSONAS: Record<string, { displayName: string; role: string; description: string }> = {
  presenter: { displayName: 'Demo presenter', role: 'presenter', description: 'Guides the aircraft story and demo flow.' },
  'technical-reviewer': { displayName: 'Technical reviewer', role: 'technical-reviewer', description: 'Reviews findings and decides case actions.' },
  'finance-reviewer': { displayName: 'Finance reviewer', role: 'finance-reviewer', description: 'Checks reserve inputs and approvals.' },
  'legal-reviewer': { displayName: 'Legal reviewer', role: 'legal-reviewer', description: 'Reviews obligations and drafting notices.' },
  executive: { displayName: 'Executive observer', role: 'executive', description: 'Tracks portfolio health and decisions.' },
};

function readLastUserId(): string | null {
  if (typeof window === 'undefined') return null;
  const stored = window.localStorage.getItem(LAST_USER_ID_KEY);
  return stored && stored.length > 0 ? stored : null;
}

export default function SignIn({ onSessionChange }: { onSessionChange?: (session: SignedInSession | null) => void }) {
  const [step, setStep] = useState<'passcode' | 'picker'>('passcode');
  const [passcode, setPasscode] = useState('');
  const [users, setUsers] = useState<SeededUser[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [session, setSession] = useState<SignedInSession | null>(null);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const alertRef = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    if (message) alertRef.current?.focus();
  }, [message]);

  useEffect(() => {
    if (!users.length) return;
    const preferredId = readLastUserId();
    if (preferredId && users.some((user) => user.id === preferredId)) {
      setSelectedId(preferredId);
      return;
    }
    setSelectedId(users[0]?.id ?? '');
  }, [users]);

  async function continueWithPasscode(event: FormEvent) {
    event.preventDefault();
    if (!passcode) {
      setMessage('Enter the demo passcode.');
      return;
    }
    setBusy(true);
    try {
      const check = await fetch('/api/auth/passcode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ passcode }),
      });
      if (check.status === 429) {
        setMessage(THROTTLED);
        return;
      }
      if (!check.ok) {
        setMessage(GENERIC_FAILURE);
        return;
      }
      const response = await fetch('/api/auth/users');
      if (!response.ok) throw new Error('user list failed');
      const nextUsers = (await response.json()) as SeededUser[];
      setUsers(nextUsers.map((user) => ({
        ...user,
        description: PERSONAS[user.role]?.description ?? `${user.displayName} works in this demo workflow.`,
      })));
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
        const body: SignedInSession = await response.json();
        setSession(body);
        onSessionChange?.(body);
        if (typeof window !== 'undefined') {
          window.localStorage.setItem(LAST_USER_ID_KEY, selectedId);
        }
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
      onSessionChange?.(null);
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
    <section aria-labelledby="signin-heading" style={{ display: 'grid', gridTemplateColumns: 'minmax(220px, 1.1fr) minmax(320px, 1.7fr)', gap: '1.5rem', background: '#f5f7fb', border: '1px solid #d9e2f2', borderRadius: '16px', padding: '1.5rem', maxWidth: '980px', margin: '0 auto' }}>
      <aside aria-label="Altivane brand" style={{ background: 'linear-gradient(180deg, #0c2746 0%, #173c66 100%)', color: '#fff', borderRadius: '12px', padding: '1.5rem', display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
        <div>
          <div style={{ fontSize: '0.8rem', letterSpacing: '0.2rem', fontWeight: 700 }}>ALTIVANE</div>
          <h2 id="signin-heading" style={{ margin: '0.5rem 0 1rem', fontSize: '2rem' }}>Asset Transitions</h2>
          <p style={{ margin: 0, lineHeight: 1.5, opacity: 0.88 }}>Review aircraft readiness through the role-based workspace your stakeholders use every day.</p>
        </div>
        <div style={{ marginTop: '1.5rem', display: 'grid', gap: '0.75rem' }}>
          {Object.values(PERSONAS).map((persona) => (
            <div key={persona.role} style={{ background: 'rgba(255,255,255,0.08)', padding: '0.75rem 0.9rem', borderRadius: '10px' }}>
              <div style={{ fontWeight: 700 }}>{persona.displayName}</div>
              <div style={{ opacity: 0.85, fontSize: '0.9rem' }}>{persona.role}</div>
            </div>
          ))}
        </div>
      </aside>

      <div>
        {step === 'passcode' ? (
          <form onSubmit={continueWithPasscode}>
            <h2 style={{ marginTop: 0 }}>Welcome back</h2>
            <label htmlFor="demo-passcode">Demo passcode</label>
            <input
              id="demo-passcode"
              type="password"
              autoComplete="off"
              value={passcode}
              onChange={(event) => setPasscode(event.target.value)}
              style={{ display: 'block', width: '100%', marginTop: '0.5rem', marginBottom: '1rem', padding: '0.75rem' }}
            />
            <button type="submit" disabled={busy}>Continue</button>
          </form>
        ) : (
          <form onSubmit={signIn}>
            <fieldset style={{ border: 'none', padding: 0, margin: 0 }}>
              <legend style={{ fontSize: '1.2rem', fontWeight: 700, marginBottom: '1rem' }}>Choose a user</legend>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '1rem' }}>
                {users.map((user) => (
                  <div key={user.id} style={{ border: selectedId === user.id ? '2px solid #214d8d' : '1px solid #d9e2f2', borderRadius: '12px', padding: '1rem', background: selectedId === user.id ? '#edf4ff' : '#fff' }}>
                    <input
                      id={`user-${user.id}`}
                      type="radio"
                      name="seeded-user"
                      value={user.id}
                      checked={selectedId === user.id}
                      onChange={() => setSelectedId(user.id)}
                    />
                    <label htmlFor={`user-${user.id}`} style={{ display: 'block', marginTop: '0.75rem', cursor: 'pointer' }}>
                      <div style={{ fontWeight: 700 }}>{user.displayName}</div>
                      <div style={{ color: '#456', marginTop: '0.15rem' }}>({user.role})</div>
                      <div aria-hidden="true" style={{ marginTop: '0.5rem', color: '#556', fontSize: '0.9rem', lineHeight: 1.4 }}>{user.description}</div>
                    </label>
                  </div>
                ))}
              </div>
            </fieldset>
            <button type="submit" disabled={busy || !selectedId} style={{ marginTop: '1rem' }}>Sign in</button>
          </form>
        )}
        {alert}
      </div>
    </section>
  );
}
