import { afterEach, describe, expect, it, vi } from 'vitest';
import { altivaneDarkTheme, altivaneLightTheme, contrastRatio, resolveAltivaneThemeChoice } from '../src/theme/altivaneTheme';
import { useThemeClassName } from '@fluentui/react-components';
import { cleanup, render } from '@testing-library/react';
import { AltivaneRoot } from '../src/main';

function ThemeProbe() {
  const themeClass = useThemeClassName();
  return <div data-testid="theme-probe" className={themeClass}>theme</div>;
}

function setSystemDark(dark: boolean) {
  vi.stubGlobal('matchMedia', vi.fn().mockImplementation((query: string) => ({
    matches: query === '(prefers-color-scheme: dark)' ? dark : false,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); localStorage.clear(); document.documentElement.removeAttribute('data-theme'); });

describe('Altivane theme contract', () => {
  it('Altivane_theme_contract', () => {
    expect(altivaneLightTheme.colorNeutralBackground1).toBe('#F7F7F8');
    expect(altivaneLightTheme.colorNeutralForeground1).toBe('#19223F');
    expect(altivaneLightTheme.colorNeutralForeground2).toBe('#565C71');
    expect(altivaneLightTheme.colorBrandForeground1).toBe('#3D7339');
    expect(altivaneLightTheme.colorBrandBackground).toBe('#639A5E');
    expect(altivaneLightTheme.colorNeutralBackground2).toBe('#FFFFFF');
    expect(altivaneLightTheme.colorBrandStroke1).toBe('#71B06C');
    expect(altivaneDarkTheme.colorNeutralBackground1).toBe('#1B1F2B');
    expect(altivaneDarkTheme.colorNeutralForeground1).toBe('#E6E8EE');
    expect(altivaneDarkTheme.colorBrandBackground).toBe('#8CC487');
    for (const theme of [altivaneLightTheme, altivaneDarkTheme]) {
      for (const key of ['colorNeutralForeground1', 'colorNeutralForeground2', 'colorBrandForeground1'] as const) {
        expect(contrastRatio(theme[key], theme.colorNeutralBackground1)).toBeGreaterThanOrEqual(4.5);
      }
      expect(contrastRatio(theme.colorBrandBackground, theme.colorNeutralBackground1)).toBeGreaterThanOrEqual(3);
    }
    expect(resolveAltivaneThemeChoice('purple')).toBe('system');
  });

  it('FluentProvider_theme_selection_contract', () => {
    for (const [stored, dark, expectedChoice, expectedTheme] of [
      ['light', true, 'light', 'light'],
      ['dark', false, 'dark', 'dark'],
      ['system', true, 'system', 'dark'],
      ['purple', false, 'system', 'light'],
    ] as const) {
      cleanup();
      setSystemDark(dark);
      localStorage.setItem('altivane.theme', stored);
      const view = render(<AltivaneRoot><ThemeProbe /></AltivaneRoot>);
      expect(document.documentElement.dataset.theme).toBe(expectedTheme);
      expect(localStorage.getItem('altivane.theme')).toBe(expectedChoice);
      const probe = view.getByTestId('theme-probe');
      expect(probe.className.length).toBeGreaterThan(0);
      const computed = window.getComputedStyle(probe);
      expect(computed.getPropertyValue('--colorNeutralBackground1').trim()).toBe(expectedTheme === 'light' ? '#F7F7F8' : '#1B1F2B');
      expect(computed.getPropertyValue('--colorNeutralForeground1').trim()).toBe(expectedTheme === 'light' ? '#19223F' : '#E6E8EE');
      expect(computed.getPropertyValue('--colorBrandBackground').trim()).toBe(expectedTheme === 'light' ? '#639A5E' : '#8CC487');
      if (expectedChoice === 'system') expect(resolveAltivaneThemeChoice(stored)).toBe('system');
      localStorage.removeItem('altivane.theme');
    }
  });
});
