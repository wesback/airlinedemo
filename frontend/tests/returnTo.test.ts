import { describe, expect, it } from 'vitest';
import { getRoleHome, resolveReturnTo, validateReturnTo } from '../src/routing/returnTo';

describe('returnTo', () => {
  it('AC_006_returnTo_contract_and_sign_in_flow', () => {
    expect(validateReturnTo('/cases/cp-case-a/findings')).toBe('/cases/cp-case-a/findings');
    expect(validateReturnTo('/work?x=1')).toBe('/work?x=1');
    expect(validateReturnTo('//evil.example/x')).toBeNull();
    expect(validateReturnTo('https://evil.example')).toBeNull();
    expect(validateReturnTo('/\\evil.example')).toBeNull();
    expect(validateReturnTo('javascript:alert(1)')).toBeNull();
    expect(validateReturnTo('')).toBeNull();

    expect(resolveReturnTo('/cases/cp-case-a/findings', 'technical-reviewer')).toBe('/cases/cp-case-a/findings');
    expect(resolveReturnTo('/portfolio', 'technical-reviewer')).toBe(getRoleHome('technical-reviewer'));
    expect(resolveReturnTo('javascript:alert(1)', 'finance-reviewer')).toBe(getRoleHome('finance-reviewer'));
    expect(resolveReturnTo('', 'legal-reviewer')).toBe(getRoleHome('legal-reviewer'));
  });
});
