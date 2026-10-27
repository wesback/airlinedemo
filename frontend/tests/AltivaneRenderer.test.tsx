import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { createDOMRenderer, FluentProvider, RendererProvider } from '@fluentui/react-components';
import { altivaneLightTheme } from '../src/theme/altivaneTheme';
import { getRendererAttributes, nonceMetaToRendererAttributes } from '../src/theme/altivaneRenderer';

afterEach(() => { cleanup(); document.querySelectorAll('meta[name="csp-nonce"]').forEach((meta) => meta.remove()); document.querySelectorAll('style').forEach((style) => style.remove()); });

describe('Altivane renderer contract', () => {
  it('nonce_meta_to_renderer_contract', () => {
    const meta = document.createElement('meta'); meta.name = 'csp-nonce';
    meta.content = 'q83vEjRWeJq83vEjRWeJqw=='; document.head.append(meta);
    expect(getRendererAttributes(document)).toEqual({ nonce: 'q83vEjRWeJq83vEjRWeJqw==' });
    meta.content = '__CSP_NONCE__'; expect(nonceMetaToRendererAttributes(meta)).toEqual({});
    meta.content = ''; expect(nonceMetaToRendererAttributes(meta)).toEqual({});
    meta.remove(); expect(getRendererAttributes(document)).toEqual({});
  });

  it('signed_in_shell_marks_every_griffel_style', () => {
    const renderer = createDOMRenderer(document, { styleElementAttributes: { nonce: 'test-nonce' } });
    render(<RendererProvider renderer={renderer}><FluentProvider theme={altivaneLightTheme}><button>Signed in</button></FluentProvider></RendererProvider>);
    const styles = Array.from(document.querySelectorAll('style'));
    const unmarked = styles.filter((style) => style.getAttribute('nonce') !== 'test-nonce');
    expect(styles.length).toBeGreaterThan(0);
    expect(unmarked).toEqual([]);
  });
});
