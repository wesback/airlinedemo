import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { WorkspaceShell } from '../src/components/WorkspaceShell';

afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

describe('WorkspaceShell', () => {
  it('AC_001_signed_in_shell_contract', () => {
    render(
      <WorkspaceShell
        nav={<nav aria-label="Primary">Primary nav</nav>}
        search={<div>Search</div>}
        userMenu={<div>User menu</div>}
        breadcrumb={<><span>Home</span> / <span>Cases</span></>}
      >
        <div>Workspace content</div>
      </WorkspaceShell>,
    );

    expect(screen.getByText('ALTIVANE')).toBeTruthy();
    expect(screen.getByText('Asset Transitions')).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    expect(screen.getByText('Workspace content')).toBeTruthy();
    expect(screen.getByRole('banner')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Collapse navigation|Expand navigation/ })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' }).getAttribute('aria-label')).toBe('Primary');
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' }).getAttribute('aria-label')).toBe('Breadcrumb');
    const aside = screen.getByRole('navigation', { name: 'Primary' }).closest('aside') as HTMLElement;
    expect(aside.style.width).toBe('240px');
  });

  it('AC_002_navigation_persistence_contract', () => {
    window.localStorage.setItem('altivane.navCollapsed', 'true');
    const { unmount } = render(
      <WorkspaceShell nav={<nav aria-label="Primary">Primary nav</nav>}>
        <div>Body</div>
      </WorkspaceShell>,
    );

    const collapsedAside = screen.getByRole('navigation', { name: 'Primary' }).closest('aside') as HTMLElement;
    expect(collapsedAside.style.width).toBe('48px');
    expect(screen.getAllByRole('button', { name: /Collapse navigation|Expand navigation/ })).toHaveLength(1);

    unmount();
    window.localStorage.setItem('altivane.navCollapsed', 'false');
    render(
      <WorkspaceShell nav={<nav aria-label="Primary">Primary nav</nav>}>
        <div>Body</div>
      </WorkspaceShell>,
    );
    const expandedAside = screen.getByRole('navigation', { name: 'Primary' }).closest('aside') as HTMLElement;
    expect(expandedAside.style.width).toBe('240px');
  });
});
