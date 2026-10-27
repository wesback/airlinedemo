import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { RoleNavigation, roleNavigationItems } from '../src/components/RoleNavigation';

describe('RoleNavigation', () => {
  it('AC_007_role_navigation_contract', () => {
    const cases = {
      'technical-reviewer': '/work',
      'finance-reviewer': '/work',
      'legal-reviewer': '/work',
      executive: '/portfolio',
      presenter: '/demo',
    } as const;

    for (const [role, home] of Object.entries(cases)) {
      const { unmount } = render(<RoleNavigation role={role} currentPath={home} onNavigate={() => {}} />);
      const items = roleNavigationItems[role].map((item) => item.label);
      expect(items).toEqual(
        role === 'technical-reviewer' || role === 'finance-reviewer' || role === 'legal-reviewer'
          ? ['My work', 'Cases', 'Hangar']
          : role === 'executive'
            ? ['Portfolio', 'Cases', 'Hangar']
            : ['My work', 'Portfolio', 'Demo control', 'Cases', 'Hangar'],
      );
      const rendered = screen.getAllByRole('button');
      rendered.forEach((button) => expect(items).toContain(button.textContent ?? ''));
      expect(screen.getAllByRole('navigation', { name: 'Primary' })).toHaveLength(1);
      unmount();
    }

    render(<RoleNavigation role="technical-reviewer" currentPath="/portfolio" onNavigate={() => {}} />);
    expect(screen.getByText('This page is not part of your workspace')).toBeTruthy();
  });
});
