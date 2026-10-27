import { describe, expect, it } from 'vitest';
import { resolveReturnTo, validateReturnTo } from '../src/routing/returnTo';

describe('returnTo contract', () => {
  it('AC_006_returnTo_contract', () => {
    expect(validateReturnTo('/cases/cp-case-a/findings')).toBe('/cases/cp-case-a/findings');
    expect(validateReturnTo('/work?x=1')).toBe('/work?x=1');
    expect(validateReturnTo('//evil.example/x')).toBeNull();
    expect(validateReturnTo('https://evil.example')).toBeNull();
    expect(validateReturnTo('/\\evil.example')).toBeNull();
    expect(validateReturnTo('javascript:alert(1)')).toBeNull();
    expect(validateReturnTo('')).toBeNull();
    expect(resolveReturnTo('/cases/cp-case-a/findings')).toBe('/cases/cp-case-a/findings');
    expect(resolveReturnTo('', '/portfolio')).toBe('/portfolio');
    expect(resolveReturnTo('//evil.example/x', '/work')).toBe('/work');
  });
});
