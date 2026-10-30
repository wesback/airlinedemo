import { useEffect, useRef, useState } from 'react';
import type { Session } from './api';
import { PERSONAS, personaForRole } from './model';
import { AltivaneLogo } from './Art';

export type Mode = 'board' | 'hangar';

type Props = {
  session: Session;
  mode: Mode;
  onMode: (mode: Mode) => void;
  onHome: () => void;
  onSwitchPersona: (userId: string) => void;
  onSignOut: () => void;
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
};

export default function TopBar({ session, mode, onMode, onHome, onSwitchPersona, onSignOut, theme, onToggleTheme }: Props) {
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const persona = personaForRole(session.role);

  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent ? event.key === 'Escape' : !menuRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', close); };
  }, [open]);

  return (
    <header className="av-topbar">
      <a className="av-topbar-brand" href="/" onClick={(event) => { event.preventDefault(); onHome(); }} aria-label="Altivane home">
        <AltivaneLogo inverse />
      </a>
      <span className="av-topbar-product">Asset transitions</span>

      <nav className="av-modes" aria-label="View">
        <button type="button" className={mode === 'board' ? 'is-active' : ''} aria-pressed={mode === 'board'} onClick={() => onMode('board')}>
          <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M2 3h3v10H2zM6.5 3h3v6h-3zM11 3h3v8h-3z" /></svg>
          Journey board
        </button>
        <button type="button" className={mode === 'hangar' ? 'is-active' : ''} aria-pressed={mode === 'hangar'} onClick={() => onMode('hangar')}>
          <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M1.5 7.5L8 2.5l6.5 5V14h-3V9.5h-7V14h-3z" /></svg>
          3D hangar
        </button>
      </nav>

      <div className="av-topbar-actions">
        <button type="button" className="av-icon-button" onClick={onToggleTheme} aria-label={theme === 'dark' ? 'Use light theme' : 'Use dark theme'}>
          {theme === 'dark'
            ? <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true"><circle cx="10" cy="10" r="3.6" /><path d="M10 1.5v2.5M10 16v2.5M1.5 10H4M16 10h2.5M4 4l1.8 1.8M14.2 14.2L16 16M4 16l1.8-1.8M14.2 5.8L16 4" strokeWidth="1.6" strokeLinecap="round" /></svg>
            : <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true"><path d="M16.5 12.5A7 7 0 0 1 7.5 3.5a7 7 0 1 0 9 9z" /></svg>}
        </button>
        <div className="av-persona-menu" ref={menuRef}>
          <button type="button" className="av-persona-trigger" aria-haspopup="true" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
            <span className={`av-persona-avatar av-role-${session.role}`} aria-hidden="true">{initials(session.displayName)}</span>
            <span className="av-persona-trigger-text">
              <strong>{session.displayName}</strong>
              <span>{persona?.label ?? session.role}</span>
            </span>
            <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d="M3 4.5l3 3 3-3" fill="none" strokeWidth="1.6" strokeLinecap="round" /></svg>
          </button>
          {open && (
            <div className="av-menu" role="menu" aria-label="Switch persona">
              <p className="av-menu-heading">Switch persona</p>
              {PERSONAS.map((item) => (
                <button key={item.id} type="button" role="menuitemradio" aria-checked={item.id === session.id}
                  className={item.id === session.id ? 'is-current' : ''}
                  onClick={() => { setOpen(false); if (item.id !== session.id) onSwitchPersona(item.id); }}>
                  <span className={`av-persona-avatar av-role-${item.role}`} aria-hidden="true">{initials(item.label)}</span>
                  <span><strong>{item.label}</strong><span>{item.description}</span></span>
                </button>
              ))}
              <hr />
              <button type="button" role="menuitem" className="av-menu-signout" onClick={() => { setOpen(false); onSignOut(); }}>Sign out</button>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

export function initials(name: string) {
  return name.split(/\s+/).map((part) => part[0]).join('').slice(0, 2).toUpperCase();
}
