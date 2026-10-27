import { cloneElement, isValidElement, useEffect, useState, type ReactNode } from 'react';

export function WorkspaceShell({
  children,
  nav,
  search,
  userMenu,
  breadcrumb,
  collapsed,
  onToggleCollapse,
}: {
  children: ReactNode;
  nav?: ReactNode;
  search?: ReactNode;
  userMenu?: ReactNode;
  breadcrumb?: ReactNode;
  collapsed?: boolean;
  onToggleCollapse?: () => void;
}) {
  const [isCollapsed, setIsCollapsed] = useState<boolean>(() => {
    if (typeof window === 'undefined') return false;
    const stored = window.localStorage.getItem('altivane.navCollapsed');
    return stored === 'true';
  });

  useEffect(() => {
    const value = collapsed ?? isCollapsed;
    if (typeof window !== 'undefined') {
      window.localStorage.setItem('altivane.navCollapsed', String(value));
    }
  }, [collapsed, isCollapsed]);

  const navState = collapsed ?? isCollapsed;
  const handleToggle = () => {
    if (typeof collapsed === 'boolean') {
      onToggleCollapse?.();
      return;
    }
    setIsCollapsed((current) => !current);
  };

  const navContent = isValidElement(nav)
    ? cloneElement(nav, {
        style: {
          ...(nav.props.style ?? {}),
          width: navState ? 48 : 240,
          minWidth: navState ? 48 : 240,
          transition: 'width 0.2s ease',
        },
      })
    : nav;

  return (
    <div data-nav-collapsed={navState ? 'true' : 'false'} style={{ minHeight: '100vh', background: '#f7f7f8', color: '#19223F' }}>
      <header style={{ height: 48, background: '#19223F', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '0 16px', boxSizing: 'border-box' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <div aria-label="Altivane Aviation Capital" style={{ fontWeight: 700, letterSpacing: 1.2 }}>ALTIVANE</div>
          <div style={{ fontSize: 18, fontWeight: 600 }}>Asset Transitions</div>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          {search}
          {userMenu}
          <button type="button" onClick={handleToggle} aria-label={navState ? 'Expand navigation' : 'Collapse navigation'} style={{ background: 'transparent', border: '1px solid rgba(255,255,255,0.35)', color: '#fff', width: 32, height: 32, borderRadius: 6, cursor: 'pointer' }}>
            {navState ? '›' : '‹'}
          </button>
        </div>
      </header>
      <div style={{ display: 'flex', minHeight: 'calc(100vh - 48px)' }}>
        {navContent && <aside style={{ width: navState ? 48 : 240, background: '#eef3f8', borderRight: '1px solid #dfe7ef', flexShrink: 0 }}>{navContent}</aside>}
        <main style={{ flex: 1, padding: 20 }}>
          {breadcrumb && (
            <nav aria-label="Breadcrumb" style={{ marginBottom: 16 }}>
              {breadcrumb}
            </nav>
          )}
          {children}
        </main>
      </div>
    </div>
  );
}
