import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('reviewer workspace service status', () => {
  it('reports when the API is reachable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"status":"ok"}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })));

    render(<App />);

    expect((await screen.findByRole('status')).textContent).toContain('Service connected');
  });

  it('reports an unavailable API instead of showing a false connected state', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 503 })));

    render(<App />);

    expect((await screen.findByRole('status')).textContent).toContain('Service unavailable');
  });
});
