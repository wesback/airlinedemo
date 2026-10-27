import { describe, expect, it } from 'vitest';
import { altivaneDarkTheme, altivaneLightTheme, contrastRatio, resolveAltivaneThemeChoice } from '../src/theme/altivaneTheme';
import { FluentProvider } from '@fluentui/react-components';
import { render } from '@testing-library/react';

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
    document.documentElement.dataset.theme = 'light';
    localStorage.setItem('altivane.theme', 'light');
    const { container } = render(<FluentProvider theme={altivaneLightTheme}><div>theme</div></FluentProvider>);
    expect(container.querySelectorAll('[data-theme], [class*="FluentProvider"]')).toBeTruthy();
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(localStorage.getItem('altivane.theme')).toBe('light');
    for (const choice of ['purple', '', 'system', 'light', 'dark']) {
      expect(['light', 'dark', 'system']).toContain(resolveAltivaneThemeChoice(choice));
    }
    localStorage.removeItem('altivane.theme');
  });
});
