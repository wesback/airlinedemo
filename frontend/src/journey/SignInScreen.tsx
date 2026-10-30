import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { Session } from './api';
import { PERSONAS } from './model';
import { ActorGlyph, AircraftArt, AltivaneLogo } from './Art';
import { initials } from './TopBar';

type SeededUser = { id: string; displayName: string; role: string };

const LAST_USER_ID_KEY = 'altivane.lastUserId';
const GENERIC_FAILURE = 'Sign-in failed. Check your details and try again.';
const THROTTLED = 'Too many attempts. Wait a few minutes before you try again.';
const UNAVAILABLE = 'Service unavailable. Try again later.';

export default function SignInScreen({ onSignedIn, apiState }: { onSignedIn: (session: Session) => void; apiState: 'checking' | 'ready' | 'unavailable' }) {
  const [step, setStep] = useState<'passcode' | 'picker'>('passcode');
  const [passcode, setPasscode] = useState('');
  const [users, setUsers] = useState<SeededUser[]>([]);
  const [selectedId, setSelectedId] = useState('');
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
      const check = await fetch('/api/auth/passcode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ passcode }),
      });
      if (check.status === 429) { setMessage(THROTTLED); return; }
      if (!check.ok) { setMessage(GENERIC_FAILURE); return; }
      const response = await fetch('/api/auth/users');
      if (!response.ok) throw new Error('user list failed');
      const nextUsers = (await response.json()) as SeededUser[];
      setUsers(nextUsers);
      const remembered = window.localStorage.getItem(LAST_USER_ID_KEY);
      setSelectedId(nextUsers.some((user) => user.id === remembered) ? remembered! : nextUsers[0]?.id ?? '');
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
    if (!selectedId) { setMessage('Choose who you are signing in as.'); return; }
    setBusy(true);
    try {
      const response = await fetch('/api/auth/sign-in', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: selectedId, passcode }),
      });
      if (response.ok) {
        const session = (await response.json()) as Session;
        window.localStorage.setItem(LAST_USER_ID_KEY, selectedId);
        setPasscode('');
        onSignedIn(session);
        return;
      }
      setPasscode('');
      setStep('passcode');
      setMessage(response.status === 429 ? THROTTLED : response.status === 409 ? 'Sign out before switching users.' : GENERIC_FAILURE);
    } catch {
      setMessage(UNAVAILABLE);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="av-signin">
      <section className="av-signin-hero" aria-label="Altivane asset transitions">
        <AltivaneLogo inverse />
        <div className="av-signin-copy">
          <h1>Making aircraft transitions predictable</h1>
          <p>Follow every aircraft from the moment it returns to the pool until it is released to its next lessee. AI does the reading, people make the calls.</p>
        </div>
        <div className="av-signin-art" aria-hidden="true">
          <svg className="av-signin-path" viewBox="0 0 600 160" preserveAspectRatio="none">
            <path d="M0 130 C 160 130 220 60 360 70 S 560 30 600 20" />
          </svg>
          <AircraftArt category="narrow-body" className="av-signin-plane" />
        </div>
        <ol className="av-signin-points">
          <li><ActorGlyph actor="rules" /> Records arrive and are pinned as evidence</li>
          <li><ActorGlyph actor="ai" /> An AI agent investigates and cites every source</li>
          <li><ActorGlyph actor="human" /> Reviewers decide at every gate</li>
        </ol>
      </section>

      <section className="av-signin-panel" aria-labelledby="signin-heading">
        <div className="av-signin-card">
          <h2 id="signin-heading">{step === 'passcode' ? 'Sign in to the demo' : 'Who are you today?'}</h2>
          <p className="av-muted">
            {step === 'passcode' ? 'Enter the passcode you received for this demo environment.' : 'Each persona sees the steps they own. The presenter can act as anyone.'}
          </p>
          {apiState === 'unavailable' && <p className="av-notice av-notice-alert" role="alert">The service is not reachable right now.</p>}
          {message && <p ref={alertRef} tabIndex={-1} className="av-notice av-notice-alert" role="alert">{message}</p>}

          {step === 'passcode' ? (
            <form onSubmit={continueWithPasscode} className="av-form">
              <label className="av-field">
                <span>Demo passcode</span>
                <input type="password" autoComplete="current-password" value={passcode} onChange={(event) => setPasscode(event.target.value)} autoFocus />
              </label>
              <button className="av-button av-button-primary av-button-block" type="submit" disabled={busy}>{busy ? 'Checking…' : 'Continue'}</button>
            </form>
          ) : (
            <form onSubmit={signIn} className="av-form">
              <fieldset className="av-persona-grid">
                <legend className="av-visually-hidden">Persona</legend>
                {users.map((user) => {
                  const persona = PERSONAS.find((item) => item.id === user.id || item.role === user.role);
                  return (
                    <label key={user.id} className={`av-persona${selectedId === user.id ? ' is-selected' : ''}`}>
                      <input type="radio" name="persona" value={user.id} checked={selectedId === user.id} onChange={() => setSelectedId(user.id)} />
                      <span className={`av-persona-avatar av-role-${user.role}`} aria-hidden="true">{initials(user.displayName)}</span>
                      <span className="av-persona-text">
                        <strong>{user.displayName}</strong>
                        <span>{persona?.description ?? 'Works in this demo workflow.'}</span>
                      </span>
                    </label>
                  );
                })}
              </fieldset>
              <div className="av-form-row">
                <button className="av-button av-button-ghost" type="button" onClick={() => { setStep('passcode'); setPasscode(''); }}>Back</button>
                <button className="av-button av-button-primary" type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
              </div>
            </form>
          )}
        </div>
        <p className="av-signin-foot">Synthetic demo data. Aircraft registrations, lessees and records are fictional.</p>
      </section>
    </div>
  );
}
