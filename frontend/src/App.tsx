import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import './styles/tokens.css';
import './styles/app.css';
import type { Session } from './journey/api';
import SignInScreen from './journey/SignInScreen';
import TopBar, { type Mode } from './journey/TopBar';
import PoolBoard from './journey/PoolBoard';
import CaseJourney from './journey/CaseJourney';
import { useFleet } from './journey/useFleet';
import { useThemeControl } from './theme/themeControl';
import type { HangarEntryAppProps } from './hangar/HangarMode';

export type { HangarEntryAppProps } from './hangar/HangarMode';

const HangarMode = lazy(() => import('./hangar/HangarMode'));

type Route = { view: 'board' } | { view: 'case'; caseId: string } | { view: 'hangar' };

export function parseRoute(path: string): Route {
  if (path === '/hangar' || path.startsWith('/hangar/')) return { view: 'hangar' };
  const match = /^\/cases\/([^/]+)\/?$/.exec(path);
  if (match) return { view: 'case', caseId: decodeURIComponent(match[1]) };
  return { view: 'board' };
}

const pathFor = (route: Route) => route.view === 'hangar' ? '/hangar' : route.view === 'case' ? `/cases/${encodeURIComponent(route.caseId)}` : '/';

export default function App(hangarProps: HangarEntryAppProps = {}) {
  const [apiState, setApiState] = useState<'checking' | 'ready' | 'unavailable'>('checking');
  const [session, setSession] = useState<Session | null>(null);
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.pathname));
  const [notice, setNotice] = useState('');

  useEffect(() => {
    let active = true;
    fetch('/api/health')
      .then((response) => { if (!response.ok) throw new Error('health'); return response.json(); })
      .then(() => { if (active) setApiState('ready'); })
      .catch(() => { if (active) setApiState('unavailable'); });
    const onPop = () => setRoute(parseRoute(window.location.pathname));
    window.addEventListener('popstate', onPop);
    return () => { active = false; window.removeEventListener('popstate', onPop); };
  }, []);

  const navigate = useCallback((next: Route) => {
    const path = pathFor(next);
    if (window.location.pathname !== path) window.history.pushState({}, '', path);
    setRoute(next);
    window.scrollTo?.({ top: 0 });
  }, []);

  async function switchPersona(userId: string) {
    if (!session) return;
    try {
      const response = await fetch('/api/auth/switch-user', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
        body: JSON.stringify({ userId }),
      });
      if (!response.ok) throw new Error('switch');
      const next = (await response.json()) as Session;
      window.localStorage.setItem('altivane.lastUserId', next.id);
      setSession(next);
      setNotice(`You are now signed in as ${next.displayName}.`);
      if (route.view === 'case' && !next.caseScopes.includes(route.caseId)) navigate({ view: 'board' });
    } catch {
      setNotice('The persona could not be switched. Try again.');
    }
  }

  async function signOut() {
    if (session) {
      await fetch('/api/auth/sign-out', { method: 'POST', headers: { 'X-CSRF-Token': session.csrfToken } }).catch(() => undefined);
    }
    setSession(null);
    setNotice('');
    navigate({ view: 'board' });
  }

  if (!session) return <SignInScreen apiState={apiState} onSignedIn={(next) => { setSession(next); }} />;

  return <SignedInShell session={session} route={route} navigate={navigate} notice={notice} onSwitchPersona={switchPersona} onSignOut={signOut} hangarProps={hangarProps} />;
}

type ShellProps = {
  session: Session;
  route: Route;
  navigate: (route: Route) => void;
  notice: string;
  onSwitchPersona: (userId: string) => void;
  onSignOut: () => void;
  hangarProps: HangarEntryAppProps;
};

function SignedInShell({ session, route, navigate, notice, onSwitchPersona, onSignOut, hangarProps }: ShellProps) {
  const { theme, toggle } = useThemeControl();
  const fleet = useFleet(session);
  const mode: Mode = route.view === 'hangar' ? 'hangar' : 'board';

  return (
    <div className={`av-app${mode === 'hangar' ? ' av-app-hangar' : ''}`}>
      <a className="av-skip" href="#main">Skip to content</a>
      <TopBar session={session} mode={mode} theme={theme} onToggleTheme={toggle}
        onMode={(next) => navigate(next === 'hangar' ? { view: 'hangar' } : { view: 'board' })}
        onHome={() => navigate({ view: 'board' })} onSwitchPersona={onSwitchPersona} onSignOut={onSignOut} />
      <p className="av-visually-hidden" role="status">{notice}</p>
      {route.view === 'board' && (
        <PoolBoard session={session} entries={fleet.entries} state={fleet.state}
          onOpen={(caseId) => navigate({ view: 'case', caseId })} onOpenHangar={() => navigate({ view: 'hangar' })} />
      )}
      {route.view === 'case' && (
        <CaseJourney session={session} caseId={route.caseId} onBack={() => navigate({ view: 'board' })}
          onSwitchPersona={onSwitchPersona} onChanged={() => void fleet.reload()} />
      )}
      {route.view === 'hangar' && (
        <Suspense fallback={<main className="av-page" id="main"><p className="av-loading" role="status">Opening the hangar…</p></main>}>
          <main className="av-page" id="main">
            <HangarMode {...hangarProps} session={session} onExit={() => navigate({ view: 'board' })}
              onOpenCase={(caseId) => navigate({ view: 'case', caseId })}
              releasedCaseIds={fleet.entries.filter((entry) => entry.progress.released).map((entry) => entry.record.caseId)} />
          </main>
        </Suspense>
      )}
    </div>
  );
}
