import { useEffect, useState, type ReactNode } from 'react';

export const NAV_EXPANDED_WIDTH = 240;
export const NAV_COLLAPSED_WIDTH = 48;

export type WorkspaceShellRoleOption = {
  id: string;
  label: string;
  role: string;
};

export type WorkspaceShellProps = {
  children?: ReactNode;
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  userLabel?: string;
  role?: string;
  roleOptions?: WorkspaceShellRoleOption[];
  onRoleSelect?: (userId: string) => void;
};

function readNavCollapsed(): boolean {
  if (typeof window === 'undefined') return false;
  const stored = window.localStorage.getItem('altivane.navCollapsed');
  return stored === 'true';
}

export default function WorkspaceShell({
  children,
  searchValue = '',
  onSearchChange,
  userLabel = 'User',
  role,
  roleOptions = [],
  onRoleSelect,
}: WorkspaceShellProps) {
  const [, setNavRenderTick] = useState(0);
  const [menuOpen, setMenuOpen] = useState(false);

  useEffect(() => {
    if (typeof window === 'undefined') return;

    const stored = window.localStorage.getItem('altivane.navCollapsed');
    if (stored !== 'true' && stored !== 'false' && stored !== null) {
      window.localStorage.setItem('altivane.navCollapsed', 'false');
    }
  }, []);

  const navCollapsed = readNavCollapsed();

  const handleToggle = () => {
    if (typeof window === 'undefined') return;

    const next = !navCollapsed;
    window.localStorage.setItem('altivane.navCollapsed', String(next));
    setNavRenderTick((value) => value + 1);
  };

  const menuOptions = roleOptions.filter((option) => option.role !== role || option.label !== userLabel);

  return (
    <div>
      <header>
        <div role="img" aria-label="Altivane Aviation Capital">
          <span>ALTIVANE</span>
          <span>Asset Transitions</span>
        </div>
        <div>
          <label htmlFor="workspace-search">Case search</label>
          <input
            id="workspace-search"
            aria-label="Case search"
            type="search"
            value={searchValue}
            onChange={(event) => onSearchChange?.(event.target.value)}
          />
        </div>
        <div aria-label="User menu" style={{ position: 'relative' }}>
          <button type="button" onClick={() => setMenuOpen((value) => !value)}>{userLabel}</button>
          {menuOpen && menuOptions.length > 0 && (
            <div role="menu" aria-label="Switch role" style={{ position: 'absolute', right: 0, top: 'calc(100% + 0.5rem)', background: '#fff', border: '1px solid #d9e2f2', borderRadius: '8px', padding: '0.5rem', minWidth: '220px', zIndex: 1 }}>
              {menuOptions.map((option) => (
                <button
                  key={option.id}
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setMenuOpen(false);
                    onRoleSelect?.(option.id);
                  }}
                  style={{ display: 'block', width: '100%', textAlign: 'left', padding: '0.5rem 0.75rem', border: 'none', background: 'transparent', cursor: 'pointer' }}
                >
                  Switch role: {option.label}
                </button>
              ))}
            </div>
          )}
        </div>
      </header>

      <nav aria-label="Breadcrumb" data-testid="breadcrumb-nav" />

      <div style={{ display: 'flex' }}>
        <aside
          aria-label="Navigation"
          style={{ width: navCollapsed ? `${NAV_COLLAPSED_WIDTH}px` : `${NAV_EXPANDED_WIDTH}px`, transition: 'width 0.2s ease' }}
        >
          <button
            type="button"
            aria-label={navCollapsed ? 'Expand navigation' : 'Collapse navigation'}
            onClick={handleToggle}
          >
            {navCollapsed ? '›' : '‹'}
          </button>
          <nav aria-label="Primary" />
        </aside>

        <main>{children}</main>
      </div>
    </div>
  );
}
