export function validateReturnTo(value: string | null | undefined): string | null {
  if (typeof value !== 'string') return null;

  const candidate = value.trim();
  if (!candidate) return null;
  if (!candidate.startsWith('/')) return null;
  if (candidate.startsWith('//')) return null;
  if (candidate.includes('\\')) return null;
  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(candidate)) return null;

  try {
    const parsed = new URL(candidate, 'http://localhost');
    if (parsed.origin !== 'http://localhost') return null;
  } catch {
    return null;
  }

  return candidate;
}

export function isValidReturnTo(value: string | null | undefined): boolean {
  return validateReturnTo(value) !== null;
}

export function resolveReturnTo(value: string | null | undefined, fallback = '/work'): string {
  return validateReturnTo(value) ?? fallback;
}
