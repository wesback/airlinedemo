import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
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
    const switches = within(screen.getByLabelText('Presenter persona switches'));
    expect(switches.getAllByRole('button').map((button) => button.textContent)).toEqual([
      'View as Demo presenter', 'View as Technical reviewer', 'View as Finance reviewer', 'View as Legal reviewer', 'View as Executive observer',
    ]);
    fireEvent.click(switches.getByRole('button', { name: 'View as Technical reviewer' }));
    fireEvent.click(switches.getByRole('button', { name: 'View as Legal reviewer' }));
    fireEvent.click(switches.getByRole('button', { name: 'View as Executive observer' }));
    expect(onViewAs.mock.calls).toEqual([['presenter'], ['fin-1'], ['reviewer'], ['legal-1'], ['exec-1']]);
  });

  it('presenter_guided_path_contract', () => {
    render(<PresenterDemoControl caseId="cp-case-c" />);

    const expected = [
      { label: 'My work', href: '/work' },
      { label: 'Portfolio', href: '/portfolio' },
      { label: 'Demo control', href: '/demo' },
      { label: 'Case', href: '/cases/cp-case-c/overview' },
      { label: 'Hangar', href: '/hangar' },
      { label: 'Technical findings', href: '/cases/cp-case-c/findings' },
      { label: 'Finance reserve', href: '/cases/cp-case-c/reserve' },
      { label: 'Legal obligations', href: '/cases/cp-case-c/obligations' },
      { label: 'Executive portfolio', href: '/portfolio' },
      { label: 'Presenter demo', href: '/demo' },
    ];

    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(expected.length);
    expect(links.map((link) => link.textContent)).toEqual(expected.map(({ label }) => label));
    for (const { label, href } of expected) {
      expect(screen.getByRole('link', { name: label }).getAttribute('href')).toBe(href);
    }
  });
});
