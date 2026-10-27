import { webDarkTheme, webLightTheme, type Theme } from '@fluentui/react-components';

const brand = {
  navy: '#19223F',
  green: '#639A5E',
  greenText: '#3D7339',
  textSecondary: '#565C71',
  lightPage: '#F7F7F8',
  card: '#FFFFFF',
  accentRule: '#71B06C',
  darkPage: '#1B1F2B',
  darkText: '#E6E8EE',
  darkGreen: '#8CC487',
} as const;

export const altivaneLightTheme: Theme = {
  ...webLightTheme,
  colorNeutralForeground1: brand.navy,
  colorNeutralForeground2: brand.textSecondary,
  colorNeutralBackground1: brand.lightPage,
  colorNeutralBackground2: brand.card,
  colorBrandBackground: brand.green,
  colorBrandBackgroundHover: brand.greenText,
  colorBrandBackgroundPressed: brand.greenText,
  colorBrandForeground1: brand.greenText,
  colorBrandStroke1: brand.accentRule,
  colorNeutralStroke1: brand.accentRule,
};

export const altivaneDarkTheme: Theme = {
  ...webDarkTheme,
  colorNeutralForeground1: brand.darkText,
  colorNeutralForeground2: '#C6CAD8',
  colorNeutralBackground1: brand.darkPage,
  colorNeutralBackground2: '#232937',
  colorBrandBackground: brand.darkGreen,
  colorBrandBackgroundHover: '#78B273',
  colorBrandBackgroundPressed: '#639A5E',
  colorBrandForeground1: brand.darkGreen,
  colorBrandStroke1: brand.darkGreen,
  colorNeutralStroke1: '#71B06C',
};

export type AltivaneThemeChoice = 'light' | 'dark' | 'system';

export function resolveAltivaneThemeChoice(value: string | null): AltivaneThemeChoice {
  return value === 'light' || value === 'dark' || value === 'system' ? value : 'system';
}

export function resolveAltivaneTheme(choice: AltivaneThemeChoice, dark = false) {
  return choice === 'dark' || (choice === 'system' && dark) ? altivaneDarkTheme : altivaneLightTheme;
}

export function relativeLuminance(hex: string): number {
  const normalized = hex.replace('#', '').trim();
  const channels = normalized.length === 3
    ? normalized.split('').map((char) => char + char)
    : [normalized.slice(0, 2), normalized.slice(2, 4), normalized.slice(4, 6)];
  const [r, g, b] = channels.map((channel) => {
    const value = parseInt(channel, 16) / 255;
    return value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrastRatio(first: string, second: string): number {
  const light = Math.max(relativeLuminance(first), relativeLuminance(second));
  const dark = Math.min(relativeLuminance(first), relativeLuminance(second));
  return (light + 0.05) / (dark + 0.05);
}
