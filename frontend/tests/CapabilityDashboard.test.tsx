import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CapabilityDashboard, { type Capability, type DashboardData } from '../src/CapabilityDashboard';

afterEach(() => cleanup());

const design = (id: string, name: string): Capability => ({
  id,
  name,
  label: 'Design only',
  designOnly: true,
  openItems: null,
  nextDecisionOwner: null,
  evidence: [],
  actions: [],
  design: {
    purpose: `${name} purpose`,
    intendedAutonomy: 'Proposes only',
    humanAuthority: 'A named human approves',
    notice: 'Not implemented in this demo',
  },
});

const baseData = (): DashboardData => ({
  capabilities: [
    {
      id: 'records-investigation',
      name: 'Records investigation',
      label: 'AI agent',
      designOnly: false,
      openItems: null,
      nextDecisionOwner: null,
      evidence: [{ label: 'Investigation trace', url: '/api/runs/run-1/trace' }],
      actions: [],
      design: null,
    },
    {
      id: 'technical-review',
      name: 'Technical review',
      label: 'Human decision',
      designOnly: false,
      openItems: 2,
      nextDecisionOwner: 'Technical reviewer',
      evidence: [],
      actions: [
        { id: 'accept', label: 'Accept finding' },
        { id: 'reject', label: 'Reject finding' },
      ],
      design: null,
    },
    design('transition-planning', 'Transition planning'),
  ],
  activity: [
    { sequence: 2, actor: 'reviewer', label: 'Human decision', outcome: 'accepted', traceUrl: null },
    { sequence: 1, actor: 'agent', label: 'AI agent', outcome: 'found', traceUrl: '/api/runs/run-1/trace' },
  ],
});

describe('capability labels', () => {
  it('shows backend labels and links only the records-investigation trace', () => {
    render(<CapabilityDashboard data={baseData()} />);

    expect(screen.getAllByText('AI agent', { selector: 'span' })).toHaveLength(2);
    expect(screen.getByRole('link', { name: 'Investigation trace' }).getAttribute('href')).toBe('/api/runs/run-1/trace');
    expect(screen.getByRole('link', { name: 'Trace' }).getAttribute('href')).toBe('/api/runs/run-1/trace');
    expect(screen.getAllByRole('link')).toHaveLength(2);
  });

  it('renders unrecognized or case-altered labels as Unknown, never upgrading them', () => {
    const data = baseData();
    data.capabilities[0].label = 'ai agent';
    data.activity = [{ sequence: 1, actor: 'agent', label: 'Robot', outcome: 'found', traceUrl: '/api/runs/run-1/trace' }];
    render(<CapabilityDashboard data={data} />);

    expect(screen.getAllByText('Unknown', { selector: 'span' })).toHaveLength(2);
    expect(screen.queryByRole('link', { name: 'Trace' })).toBeNull();
  });

  it('drops non-relative trace and evidence URLs', () => {
    const data = baseData();
    data.activity[1].traceUrl = 'javascript:alert(1)';
    data.capabilities[0].evidence = [{ label: 'Evidence', url: '//evil.example/x' }];
    render(<CapabilityDashboard data={data} />);

    expect(screen.queryByRole('link', { name: 'Evidence' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Trace' })).toBeNull();
  });
});

describe('design-only cards', () => {
  it('state purpose, autonomy, authority and the not-implemented notice without actions or counts', () => {
    render(<CapabilityDashboard data={baseData()} />);

    const card = screen.getByRole('article', { name: 'Transition planning' });
    expect(within(card).getByText('Transition planning purpose', { exact: false })).toBeTruthy();
    expect(within(card).getByText(/Intended autonomy: Proposes only/)).toBeTruthy();
    expect(within(card).getByText(/Human authority: A named human approves/)).toBeTruthy();
    expect(within(card).getByText('Not implemented in this demo')).toBeTruthy();
    expect(within(card).queryByRole('button')).toBeNull();
    expect(within(card).queryByText(/Open items/)).toBeNull();
    expect(within(card).queryByRole('link')).toBeNull();
  });
});

describe('role-permitted decisions', () => {
  it('offers only the actions the backend supplied and reports the chosen decision', () => {
    const onDecision = vi.fn();
    render(<CapabilityDashboard data={baseData()} onDecision={onDecision} />);

    fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));
    expect(onDecision).toHaveBeenCalledWith('technical-review', 'accept');
    expect(screen.getAllByRole('button')).toHaveLength(2);
  });

  it('shows no decision action when the role has none', () => {
    const data = baseData();
    data.capabilities[1].actions = [];
    render(<CapabilityDashboard data={data} />);

    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.getByText('Open items: 2')).toBeTruthy();
  });
});

describe('inert rendering of document and model content', () => {
  it('renders script, handler and markdown payloads as encoded text', () => {
    const payload = '<img src=x onerror="alert(1)"><script>alert(1)</script> [go](javascript:alert(1))';
    const data = baseData();
    data.activity[0].outcome = payload;
    data.capabilities[1].nextDecisionOwner = payload;
    const { container } = render(<CapabilityDashboard data={data} />);

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('a[href^="javascript"]')).toBeNull();
    expect(screen.getByText(/Next decision owner:/).textContent).toContain(payload);
    expect(container.textContent).toContain(payload);
  });
});
