import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PresenterDemoControl from '../src/workspaces/PresenterDemoControl';

afterEach(() => {
  cleanup();
});

describe('PresenterDemoControl', () => {
  it('presenter_scoped_reset_actions', () => {
    const onReset = vi.fn();
    render(
      <PresenterDemoControl
        caseId="cp-case-c"
        cases={[
          { id: 'cp-case-a', name: 'Case A', canReset: false },
          { id: 'cp-case-c', name: 'Case C', canReset: true, resetLabel: 'Reset blocked variant' },
        ]}
        onResetCase={onReset}
      />,
    );

    expect(screen.getByText('Case A')).toBeTruthy();
    expect(screen.getByText('Case C')).toBeTruthy();
    expect(screen.getByText('Not eligible')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Reset blocked variant' })).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Reset blocked variant' }));
    expect(onReset).toHaveBeenCalledWith('cp-case-c');
    expect(onReset).toHaveBeenCalledTimes(1);
  });

  it('AC_004_presenter_view_as_contract', () => {
    const onViewAs = vi.fn();
    render(<PresenterDemoControl caseId="cp-case-c" onViewAs={onViewAs} />);

    fireEvent.click(screen.getAllByRole('button', { name: /View as Demo presenter/i })[0]);
    expect(onViewAs).toHaveBeenCalledWith('presenter');

    fireEvent.click(screen.getAllByRole('button', { name: /View as Finance reviewer/i })[0]);
    expect(onViewAs).toHaveBeenCalledWith('fin-1');
    expect(onViewAs.mock.calls).toEqual([['presenter'], ['fin-1']]);
  });

  it('presenter_guided_path_contract', () => {
    render(<PresenterDemoControl caseId="cp-case-c" />);

    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(5);
    expect(links.map((link) => link.textContent)).toEqual(['My work', 'Portfolio', 'Demo control', 'Case', 'Hangar']);
    expect(screen.getByRole('link', { name: 'My work' }).getAttribute('href')).toBe('/work');
    expect(screen.getByRole('link', { name: 'Portfolio' }).getAttribute('href')).toBe('/portfolio');
    expect(screen.getByRole('link', { name: 'Demo control' }).getAttribute('href')).toBe('/demo');
    expect(screen.getByRole('link', { name: 'Case' }).getAttribute('href')).toBe('/cases/cp-case-c/overview');
    expect(screen.getByRole('link', { name: 'Hangar' }).getAttribute('href')).toBe('/hangar');
  });
});
