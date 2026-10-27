import type { ReactNode } from 'react';
import { getRoleHome, isRouteAllowedForRole } from '../routing/returnTo';

export type NavigationEntry = { label: string; path: string; };

export const roleNavigationItems: Record<string, NavigationEntry[]> = {
  'technical-reviewer': [
    { label: 'My work', path: '/work' },
    { label: 'Cases', path: '/cases' },
    { label: 'Hangar', path: '/hangar' },
  ],
  'finance-reviewer': [
    { label: 'My work', path: '/work' },
    { label: 'Cases', path: '/cases' },
    { label: 'Hangar', path: '/hangar' },
  ],
  'legal-reviewer': [
    { label: 'My work', path: '/work' },
    { label: 'Cases', path: '/cases' },
    { label: 'Hangar', path: '/hangar' },
  ],
  executive: [
    { label: 'Portfolio', path: '/portfolio' },
    { label: 'Cases', path: '/cases' },
    { label: 'Hangar', path: '/hangar' },
  ],
  presenter: [
    { label: 'My work', path: '/work' },
    { label: 'Portfolio', path: '/portfolio' },
    { label: 'Demo control', path: '/demo' },
    { label: 'Cases', path: '/cases' },
    { label: 'Hangar', path: '/hangar' },
  ],
};

export function RoleNavigation({ role, currentPath, onNavigate, collapsed = false, children }: {
  role: string;
  currentPath: string;
  onNavigate: (path: string) => void;
  collapsed?: boolean;
  children?: ReactNode;
}) {
  const items = roleNavigationItems[role] ?? [];
  const home = getRoleHome(role);
  return (
    <nav aria-label="Primary" style={{ width: collapsed ? 48 : 240, minWidth: collapsed ? 48 : 240, transition: 'width 0.2s ease' }}>
      <ul style={{ listStyle: 'none', margin: 0, padding: 8 }}>
        {items.map((item) => {
          const active = currentPath === item.path || (item.path === '/cases' && currentPath.startsWith('/cases')) || (currentPath === '/' && item.path === home) || (item.path === home && currentPath === home);
          return (
            <li key={item.path} style={{ marginBottom: 4 }}>
              <button
                type="button"
                onClick={() => onNavigate(item.path)}
                aria-current={active ? 'page' : undefined}
                style={{
                  width: '100%',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: collapsed ? 'center' : 'flex-start',
                  border: 'none',
                  background: active ? '#eaf2ff' : 'transparent',
                  color: '#19223F',
                  padding: '10px 12px',
                  borderRadius: 8,
                  fontWeight: active ? 600 : 500,
                  cursor: 'pointer',
                }}
              >
                {collapsed ? item.label.slice(0, 1) : item.label}
              </button>
            </li>
          );
        })}
      </ul>
      {!isRouteAllowedForRole(role, currentPath) && <p>This page is not part of your workspace</p>}
      {children}
    </nav>
  );
}
