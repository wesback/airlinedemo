export const roleNavigationItems: Record<string, string[]> = {
  'technical-reviewer': ['My work', 'Cases', 'Hangar'],
  'finance-reviewer': ['My work', 'Cases', 'Hangar'],
  'legal-reviewer': ['My work', 'Cases', 'Hangar'],
  executive: ['Portfolio', 'Cases', 'Hangar'],
  presenter: ['My work', 'Portfolio', 'Demo control', 'Cases', 'Hangar'],
};

export function getRoleNavigationItems(role: string): string[] {
  return roleNavigationItems[role] ?? [];
}

export type RoleNavigationProps = {
  role?: string;
  items?: string[];
};

export default function RoleNavigation({ role = 'technical-reviewer', items }: RoleNavigationProps) {
  const resolvedItems = items ?? getRoleNavigationItems(role);

  return (
    <nav aria-label="Primary" data-role={role}>
      <ul>
        {resolvedItems.map((item) => (
          <li key={item} role="presentation">
            <a href="#" tabIndex={-1}>{item}</a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
