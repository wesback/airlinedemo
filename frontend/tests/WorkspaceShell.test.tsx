import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import WorkspaceShell from '../src/components/WorkspaceShell';

describe('WorkspaceShell contract', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  afterEach(() => {
    cleanup();
  });

  it('AC_001_signed_in_shell_contract', () => {
    render(
      <WorkspaceShell userLabel="Ada Reviewer">
        <div>Shell content</div>
      </WorkspaceShell>
    );

    expect(screen.getByLabelText('Altivane Aviation Capital')).toBeTruthy();
    expect(screen.getByText('ALTIVANE')).toBeTruthy();
    expect(screen.getByText('Asset Transitions')).toBeTruthy();
    expect(screen.getByRole('searchbox', { name: 'Case search' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Ada Reviewer' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Collapse navigation' })).toBeTruthy();
    expect(document.querySelectorAll('button[aria-label="Collapse navigation"]').length).toBe(1);

    const nav = screen.getByRole('navigation', { name: 'Primary' }).closest('aside') as HTMLElement | null;
    expect(nav?.style.width).toBe('240px');
  });

  it('AC_002_navigation_persistence_contract', () => {
    window.localStorage.setItem('altivane.navCollapsed', 'true');
    const { rerender } = render(
      <WorkspaceShell userLabel="Ada Reviewer">
        <div>Shell content</div>
      </WorkspaceShell>
    );
    const collapsed = document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null;
    expect(collapsed?.style.width).toBe('48px');

    window.localStorage.setItem('altivane.navCollapsed', 'false');
    rerender(
      <WorkspaceShell userLabel="Ada Reviewer">
        <div>Shell content</div>
      </WorkspaceShell>
    );
    expect((document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null)?.style.width).toBe('240px');

    window.localStorage.removeItem('altivane.navCollapsed');
    rerender(
      <WorkspaceShell userLabel="Ada Reviewer">
        <div>Shell content</div>
      </WorkspaceShell>
    );
    expect((document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null)?.style.width).toBe('240px');

    window.localStorage.setItem('altivane.navCollapsed', 'yes');
    rerender(
      <WorkspaceShell userLabel="Ada Reviewer">
        <div>Shell content</div>
      </WorkspaceShell>
    );
    expect((document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null)?.style.width).toBe('240px');

    fireEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }));
    expect((document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null)?.style.width).toBe('48px');
    fireEvent.click(screen.getByRole('button', { name: 'Expand navigation' }));
    expect((document.querySelector('aside[aria-label="Navigation"]') as HTMLElement | null)?.style.width).toBe('240px');
  });
});
