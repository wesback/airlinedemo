import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import DataFlowWalkthrough from '../src/journey/DataFlowWalkthrough';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('DataFlowWalkthrough', () => {
  it('shows the five simulated source categories and ordered retrieval stages', () => {
    render(<DataFlowWalkthrough />);
    const main = screen.getByRole('main');
    expect(within(main).getByRole('heading', { name: 'Data flows', level: 1 })).toBeTruthy();
    expect(within(main).getByText('Illustrative walkthrough · not live system status')).toBeTruthy();
    expect(within(main).getByRole('button', { name: 'Simulated source retrieval' }).getAttribute('aria-pressed')).toBe('true');
    const sourceFlow = within(main).getByRole('region', { name: 'Simulated source retrieval' });
    expect(sourceFlow.textContent).not.toMatch(/live|available/i);
    expect(within(main).getByRole('list', { name: 'Source systems' }).querySelectorAll('li')).toHaveLength(5);
    expect([...within(main).getByRole('list', { name: 'Source systems' }).querySelectorAll('li')].map((item) => item.textContent))
      .toEqual(['technical', 'maintenance', 'finance', 'records', 'contracts']);
    const stages = within(main).getByRole('list', { name: 'Source retrieval stages' });
    expect([...stages.querySelectorAll('li')].map((item) => item.textContent)).toEqual([
      'Check the signed-in user’s authorization and case scope.',
      'Read permitted records from simulated fixture adapters.',
      'Pin permitted evidence references to the case evidence basis.',
      'Record an audit receipt and updated case revision.',
    ]);
  });

  it('switches to the PDF intake flow using keyboard controls', async () => {
    const user = userEvent.setup();
    render(<DataFlowWalkthrough />);

    await user.tab();
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Uploaded PDF intake' }));
    await user.keyboard('{Enter}');

    const main = screen.getByRole('main');
    expect(within(main).getByRole('button', { name: 'Uploaded PDF intake' }).getAttribute('aria-pressed')).toBe('true');
    expect(within(main).getByRole('button', { name: 'Simulated source retrieval' }).getAttribute('aria-pressed')).toBe('false');
    expect(within(main).queryByRole('list', { name: 'Source systems' })).toBeNull();
    expect([...within(main).getByRole('list', { name: 'PDF intake stages' }).querySelectorAll('li')].map((item) => item.textContent))
      .toEqual([
        'Upload a PDF records package for the case.',
        'The server validates and parses the documents.',
        'Accepted documents are pinned as evidence.',
        'Only registered synthetic document text is sent to the records agent for investigation.',
        'Create a required human technical-review work item whether the records agent returns findings or not.',
      ]);
  });

  it('switching flows makes no content request', () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);
    render(<DataFlowWalkthrough />);

    fireEvent.click(screen.getByRole('button', { name: 'Uploaded PDF intake' }));
    fireEvent.click(screen.getByRole('button', { name: 'Simulated source retrieval' }));

    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('renders no case identifiers or record references', () => {
    render(<DataFlowWalkthrough />);

    expect(screen.getByRole('heading', { name: 'Data flows', level: 1 })).toBeTruthy();
    expect(document.body.textContent).not.toContain('cp-case-c');
    expect(document.body.textContent).not.toContain('case-c/engine-serial-record.txt');
  });
});
