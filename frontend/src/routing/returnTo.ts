export type RoleName =
  | 'technical-reviewer'
  | 'finance-reviewer'
  | 'legal-reviewer'
  | 'executive'
  | 'presenter';

export const roleHomeRoutes: Record<RoleName, string> = {
  'technical-reviewer': '/work',
  'finance-reviewer': '/work',
  'legal-reviewer': '/work',
  executive: '/portfolio',
  presenter: '/demo',
};

const roleRouteSets: Record<RoleName, Set<string>> = {
  'technical-reviewer': new Set(['/work', '/cases', '/hangar', '/cases/cp-case-a/overview', '/cases/cp-case-a/findings', '/cases/cp-case-a/history', '/cases/cp-case-b/overview', '/cases/cp-case-b/findings', '/cases/cp-case-b/history', '/cases/cp-case-c/overview', '/cases/cp-case-c/findings', '/cases/cp-case-c/history']),
  'finance-reviewer': new Set(['/work', '/cases', '/hangar', '/cases/cp-case-a/overview', '/cases/cp-case-a/reserve', '/cases/cp-case-a/history', '/cases/cp-case-b/overview', '/cases/cp-case-b/reserve', '/cases/cp-case-b/history', '/cases/cp-case-c/overview', '/cases/cp-case-c/reserve', '/cases/cp-case-c/history']),
  'legal-reviewer': new Set(['/work', '/cases', '/hangar', '/cases/cp-case-a/overview', '/cases/cp-case-a/obligations', '/cases/cp-case-a/history', '/cases/cp-case-b/overview', '/cases/cp-case-b/obligations', '/cases/cp-case-b/history', '/cases/cp-case-c/overview', '/cases/cp-case-c/obligations', '/cases/cp-case-c/history']),
  executive: new Set(['/portfolio', '/cases', '/hangar', '/cases/cp-case-a/overview', '/cases/cp-case-a/findings', '/cases/cp-case-a/reserve', '/cases/cp-case-a/obligations', '/cases/cp-case-a/history', '/cases/cp-case-b/overview', '/cases/cp-case-b/findings', '/cases/cp-case-b/reserve', '/cases/cp-case-b/obligations', '/cases/cp-case-b/history', '/cases/cp-case-c/overview', '/cases/cp-case-c/findings', '/cases/cp-case-c/reserve', '/cases/cp-case-c/obligations', '/cases/cp-case-c/history']),
  presenter: new Set(['/work', '/portfolio', '/demo', '/cases', '/hangar', '/cases/cp-case-a/overview', '/cases/cp-case-a/findings', '/cases/cp-case-a/reserve', '/cases/cp-case-a/obligations', '/cases/cp-case-a/history', '/cases/cp-case-b/overview', '/cases/cp-case-b/findings', '/cases/cp-case-b/reserve', '/cases/cp-case-b/obligations', '/cases/cp-case-b/history', '/cases/cp-case-c/overview', '/cases/cp-case-c/findings', '/cases/cp-case-c/reserve', '/cases/cp-case-c/obligations', '/cases/cp-case-c/history']),
};

export function isSameOriginPath(value: string): boolean {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.includes('\\') && !/^(?:[a-zA-Z][a-zA-Z0-9+.-]*:)/.test(value);
}

export function validateReturnTo(value: string | null | undefined): string | null {
  if (typeof value !== 'string' || value.length === 0) return null;
  if (!value.startsWith('/')) return null;
  if (!isSameOriginPath(value)) return null;
  return value;
}

export function getRoleHome(role: string | null | undefined): string {
  if (!role || !(role in roleHomeRoutes)) return '/work';
  return roleHomeRoutes[role as RoleName];
}

export function resolveReturnTo(value: string | null | undefined, role: string | null | undefined): string {
  const validated = validateReturnTo(value);
  if (!validated) return getRoleHome(role);
  if (role && !(role in roleHomeRoutes)) return getRoleHome(role);
  if (role && !isRouteAllowedForRole(role, validated)) return getRoleHome(role);
  return validated;
}

export function isRouteAllowedForRole(role: string | null | undefined, target: string): boolean {
  if (!role || !(role in roleRouteSets)) return false;
  const path = target.split('?')[0].split('#')[0];
  if (path === '/') return false;
  const clean = path === '' ? '/' : path;
  if (clean === roleHomeRoutes[role as RoleName]) return true;
  return roleRouteSets[role as RoleName].has(clean);
}

export function getSafeReturnTarget(value: string | null | undefined, role: string | null | undefined): string {
  return resolveReturnTo(value, role);
}
