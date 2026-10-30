import { StrictMode, useEffect, useState, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { createDOMRenderer, FluentProvider, RendererProvider } from '@fluentui/react-components';
import App from './App';
import { altivaneDarkTheme, altivaneLightTheme, resolveAltivaneThemeChoice, type AltivaneThemeChoice } from './theme/altivaneTheme';
import { getRendererAttributes } from './theme/altivaneRenderer';
import { ThemeControlContext } from './theme/themeControl';

export function AltivaneRoot({ children = <App /> }: { children?: ReactNode }) {
  const [choice, setChoice] = useState<AltivaneThemeChoice>(() => {
    const stored = localStorage.getItem('altivane.theme');
    const resolved = resolveAltivaneThemeChoice(stored);
    if (stored !== resolved) localStorage.setItem('altivane.theme', resolved);
    return resolved;
  });
  const [systemDark, setSystemDark] = useState(() => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false);

  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const update = () => setSystemDark(media?.matches ?? false);
    media?.addEventListener?.('change', update);
    return () => media?.removeEventListener?.('change', update);
  }, []);

  const resolvedChoice = choice === 'dark' || (choice === 'system' && systemDark) ? 'dark' : 'light';
  useEffect(() => {
    document.documentElement.dataset.theme = resolvedChoice;
    localStorage.setItem('altivane.theme', choice);
  }, [choice, resolvedChoice]);

  const [renderer] = useState(() => {
    const attributes = getRendererAttributes(document);
    return createDOMRenderer(document, attributes.nonce ? { styleElementAttributes: { nonce: attributes.nonce } } : {});
  });
  const theme = resolvedChoice === 'dark' ? altivaneDarkTheme : altivaneLightTheme;

  return (
    <RendererProvider renderer={renderer}>
      <FluentProvider theme={theme}>
        <ThemeControlContext.Provider value={{ theme: resolvedChoice, toggle: () => setChoice(resolvedChoice === 'dark' ? 'light' : 'dark') }}>
          {children}
        </ThemeControlContext.Provider>
      </FluentProvider>
    </RendererProvider>
  );
}

const rootElement = document.getElementById('root');
if (rootElement) {
  createRoot(rootElement).render(
    <StrictMode><AltivaneRoot /></StrictMode>,
  );
}
