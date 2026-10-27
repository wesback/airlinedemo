import { useEffect, useState, type ReactNode } from 'react';

export const NAV_EXPANDED_WIDTH = 240;
export const NAV_COLLAPSED_WIDTH = 48;

export type WorkspaceShellProps = {
  children?: ReactNode;
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  userLabel?: string;
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
}: WorkspaceShellProps) {
  const [, setNavRenderTick] = useState(0);

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
        <div aria-label="User menu">
          <button type="button">{userLabel}</button>
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
