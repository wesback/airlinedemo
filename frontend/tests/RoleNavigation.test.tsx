import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import RoleNavigation, { getRoleNavigationItems } from '../src/components/RoleNavigation';

describe('RoleNavigation contract', () => {
  it('AC_007_role_navigation_contract', () => {
    const expected = {
      'technical-reviewer': ['My work', 'Cases', 'Hangar'],
      'finance-reviewer': ['My work', 'Cases', 'Hangar'],
      'legal-reviewer': ['My work', 'Cases', 'Hangar'],
      executive: ['Portfolio', 'Cases', 'Hangar'],
      presenter: ['My work', 'Portfolio', 'Demo control', 'Cases', 'Hangar'],
    } as const;

    for (const [role, items] of Object.entries(expected)) {
      const { unmount } = render(<RoleNavigation role={role} />);
      const nav = document.querySelector('nav[aria-label="Primary"]');
      expect(nav).not.toBeNull();
      const rendered = Array.from(nav!.querySelectorAll('a')).map((node) => node.textContent);
      expect(rendered).toEqual(items);
      expect(getRoleNavigationItems(role)).toEqual(items);
      unmount();
    }
  });
});
