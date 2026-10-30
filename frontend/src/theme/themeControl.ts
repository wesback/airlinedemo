import { createContext, useContext } from 'react';

export type ThemeControl = { theme: 'light' | 'dark'; toggle: () => void };

export const ThemeControlContext = createContext<ThemeControl>({ theme: 'light', toggle: () => undefined });

export const useThemeControl = () => useContext(ThemeControlContext);
