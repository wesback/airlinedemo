import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../src/App';
import ExecutivePortfolio from '../src/workspaces/ExecutivePortfolio';

describe('ExecutivePortfolio', () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    window.history.replaceState({}, '', '/');
  });

  it.each([
    { empty: false, expected: ['2', '1', '3', '1d 1h'] },
    { empty: true, expected: ['0', '0', '0', '—'] },
  ])('AC_016_portfolio_kpis_contract (empty=$empty)', async ({ empty, expected }) => {
    const cases = empty ? [] : [
      { caseId: 'case-001', openItemsByRole: { 'technical-reviewer': 2, 'finance-reviewer': 1 }, nextDecisionOwner: 'exec-1', oldestOpenAgeSeconds: 90000, latestActivity: null },
      { caseId: 'case-002', openItemsByRole: { 'technical-reviewer': 0 }, nextDecisionOwner: null, oldestOpenAgeSeconds: 3600, latestActivity: null },
    ];
    const session = { id: 'exec-1', displayName: 'Executive observer', role: 'executive', caseScopes: cases.map((row) => row.caseId), csrfToken: 'csrf-fixture' };
    const portfolioMethods: string[] = [];
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/health') return json({ status: 'ok' });
      if (url === '/api/auth/passcode') return new Response(null, { status: 204 });
      if (url === '/api/auth/users') return json([session]);
      if (url === '/api/auth/sign-in') return json(session);
      if (url === '/api/v1/workspace/portfolio') {
        portfolioMethods.push(init?.method ?? 'GET');
        return json({ contractVersion: 1, cases, unavailableCaseIds: [] });
      }
      if (url === '/api/cases/case-001') return json({ revision: 7, basis: [] });
      if (url === '/api/cases/case-001/dashboard') return json({ allowedDecisions: [], capabilities: [], queue: [], activity: [] });
      if (url === '/api/cases/case-001/technical-reviews') return json({ basisVersion: 1, decisions: [] });
      if (url === '/api/cases/case-001/readiness') return json({ sentence: 'Portfolio case readiness' });
      throw new Error(`Unexpected request: ${url}`);
    }));
    window.history.replaceState({}, '', '/portfolio');
    render(<App />);
    expect(portfolioMethods).toEqual([]);
    fireEvent.change(screen.getByLabelText('Demo passcode'), { target: { value: 'demo-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('radio', { name: 'Executive observer (executive)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('Signed in as Executive observer.');
    const tiles = screen.getByLabelText('Portfolio KPI tiles');
    await waitFor(() => expect(Array.from(tiles.querySelectorAll('strong'), (tile) => tile.textContent)).toEqual(expected));
    expect(tiles.children).toHaveLength(4);
    expect(portfolioMethods).toEqual(['GET']);
    expect(window.location.pathname).toBe('/portfolio');
    if (empty) {
      expect(screen.getByText('No cases in your scope.').textContent).toBe('No cases in your scope.');
      expect(screen.queryByRole('table')).toBeNull();
    } else {
      expect(screen.queryByText('No cases in your scope.')).toBeNull();
    }
  });

  it('portfolio_component_renders_kpis_and_opens_selected_case', () => {
    const onOpenCase = vi.fn();
    render(<ExecutivePortfolio
      rows={[
        { caseId: 'case-001', openItemsByRole: { 'technical-reviewer': 2, 'finance-reviewer': 1 }, nextDecisionOwner: 'exec-1', oldestOpenAgeSeconds: 90000, latestActivity: { sequence: 1, actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' } },
        { caseId: 'case-002', openItemsByRole: { 'technical-reviewer': 0 }, nextDecisionOwner: null, oldestOpenAgeSeconds: 3600, latestActivity: null },
      ]}
      onOpenCase={onOpenCase}
    />);

    expect(screen.getByText('Cases in scope')).toBeTruthy();
    expect(screen.getByText('2')).toBeTruthy();
    expect(screen.getByText('Cases with open items')).toBeTruthy();
    expect(screen.getByText('1')).toBeTruthy();
    expect(screen.getByText('Total open items')).toBeTruthy();
    expect(screen.getByText('3')).toBeTruthy();
    expect(screen.getAllByText('Oldest age')).toHaveLength(2);
    expect(screen.getAllByText('1d 1h')).toHaveLength(2);

    const rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('case-001');
    expect(rows[1].textContent).toContain('technical-reviewer: 2, finance-reviewer: 1');
    expect(rows[1].textContent).toContain('exec-1');
    expect(rows[1].textContent).toContain('1d 1h');
    expect(rows[1].textContent).toContain('Ada Reviewer: Human decision — technical-review.decided');

    fireEvent.click(screen.getAllByRole('cell')[0]);
    expect(onOpenCase).toHaveBeenCalledWith('case-001');
  });

  it('executive_portfolio_table_contract', () => {
    const onOpenCase = vi.fn();
    const rowWithSensitiveExtraProjection = {
      caseId: 'case-001',
      openItemsByRole: { 'technical-reviewer': 2, 'finance-reviewer': 1 },
      nextDecisionOwner: 'exec-1',
      oldestOpenAgeSeconds: 90000,
      latestActivity: { sequence: 1, actor: 'Ada Reviewer', label: 'Human decision', outcome: 'technical-review.decided' },
      itemId: 'SECRET-ITEM-1',
      evidence: 'SECRET-EVIDENCE',
      draft: 'SECRET-DRAFT',
      decisionText: 'SECRET-DECISION',
    } as any;
    render(<ExecutivePortfolio
      rows={[
        rowWithSensitiveExtraProjection,
        { caseId: 'case-002', openItemsByRole: { 'technical-reviewer': 0 }, nextDecisionOwner: null, oldestOpenAgeSeconds: 3600, latestActivity: null },
      ]}
      onOpenCase={onOpenCase}
    />);

    const rows = screen.getAllByRole('row');
    expect(rows[1].textContent).toContain('case-001');
    expect(rows[1].textContent).toContain('technical-reviewer: 2, finance-reviewer: 1');
    expect(rows[1].textContent).toContain('exec-1');
    expect(rows[1].textContent).toContain('1d 1h');
    expect(rows[1].textContent).toContain('Ada Reviewer: Human decision — technical-review.decided');
    expect(rows[1].textContent).not.toContain('SECRET-ITEM-1');
    expect(rows[1].textContent).not.toContain('SECRET-EVIDENCE');
    expect(rows[1].textContent).not.toContain('SECRET-DRAFT');
    expect(rows[1].textContent).not.toContain('SECRET-DECISION');

    fireEvent.click(rows[1]);
    expect(onOpenCase).toHaveBeenCalledWith('case-001');
  });

  it('renders zeroed KPI values and empty scope state', () => {
    render(<ExecutivePortfolio rows={[]} />);

    expect(screen.getByText('No cases in your scope.')).toBeTruthy();
    expect(screen.getAllByText('0').length).toBeGreaterThanOrEqual(3);
    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('portfolio_server_text_remains_inert', () => {
    render(<ExecutivePortfolio
      rows={[{
        caseId: 'case-003',
        openItemsByRole: { 'technical-reviewer': 1 },
        nextDecisionOwner: 'analyst-1',
        oldestOpenAgeSeconds: 7200,
        latestActivity: { sequence: 2, actor: '<img src=x onerror=alert(1)>', label: '<b>AI agent</b>', outcome: 'javascript:alert(1)' },
      }]}
    />);

    expect(screen.getByText('case-003')).toBeTruthy();
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.getByText('<img src=x onerror=alert(1)>: <b>AI agent</b> — javascript:alert(1)')).toBeTruthy();
  });
});
