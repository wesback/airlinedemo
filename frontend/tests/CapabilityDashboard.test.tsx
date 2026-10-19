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
  design: {
    purpose: `${name} purpose`,
    intendedAutonomy: 'Proposes only',
    humanAuthority: 'A named human approves',
    notice: 'Not implemented in this demo',
  },
  caseStudyAutonomy: 'L2, recommend',
  demoAutonomy: 'design-only: not implemented in this demo',
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
      design: null,
      caseStudyAutonomy: 'L4, autonomous',
      demoAutonomy: 'L3 demo',
    },
    {
      id: 'technical-review',
      name: 'Technical review',
      label: 'Human decision',
      designOnly: false,
      openItems: 2,
      nextDecisionOwner: 'Technical reviewer',
      evidence: [],
      design: null,
      caseStudyAutonomy: 'L2, review',
      demoAutonomy: 'L2 demo',
    },
    design('transition-planning', 'Transition planning'),
  ],
  allowedDecisions: [
    { id: 'accept', label: 'Accept finding' },
    { id: 'reject', label: 'Reject finding' },
  ],
  queue: [
    { itemId: 'finding-1', label: 'Human decision' },
    { itemId: 'finding-deterministic', label: 'Deterministic workflow' },
  ],
  activity: [
    { sequence: 2, actor: 'reviewer', label: 'Human decision', outcome: 'accepted' },
    { sequence: 1, actor: 'agent', label: 'AI agent', outcome: 'found' },
  ],
});

describe('capability labels', () => {
  it('Renders_backend_labels_and_rejects_client_overrides', () => {
    render(<CapabilityDashboard data={baseData()} />);

    expect(screen.getAllByText('AI agent', { selector: 'span' })).toHaveLength(2);
    expect(screen.getByText('Deterministic workflow', { selector: 'span' })).toBeTruthy();
    expect(screen.getAllByText('Human decision', { selector: 'span' })).toHaveLength(3);
    expect(screen.getByText('Design only', { selector: 'span' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Investigation trace' }).getAttribute('href')).toBe('/api/runs/run-1/trace');
    expect(screen.queryByRole('link', { name: 'Trace' })).toBeNull();
    expect(screen.getAllByRole('link')).toHaveLength(1);
    const data = baseData();
    data.capabilities[0].label = 42;
    data.capabilities[2].label = undefined;
    data.capabilities[1].evidence = [{ label: 'Not permitted evidence', url: '/private/evidence' }];
    data.queue.push({ itemId: 'finding-2', label: 'Not a known role' });
    data.activity = [
      { sequence: 1, actor: 'agent', label: 'Robot', outcome: 'found' },
      { sequence: 2, actor: 'reviewer', label: null, outcome: 'reviewed' },
    ];
    render(<CapabilityDashboard data={data} />);

    expect(screen.getAllByText('Unknown', { selector: 'span' })).toHaveLength(5);
    expect(screen.queryByRole('link', { name: 'Trace' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Not permitted evidence' })).toBeNull();
  });

  it('labels queue items with their backend label or Unknown when unrecognized', () => {
    const data = baseData();
    data.queue = [
      { itemId: 'finding-1', label: 'Human decision' },
      { itemId: 'finding-2', label: 'Robot' },
    ];
    render(<CapabilityDashboard data={data} />);

    const queue = screen.getByRole('region', { name: 'Queue' });
    expect(within(queue).getByText('Human decision', { selector: 'span' })).toBeTruthy();
    expect(within(queue).getByText('Unknown', { selector: 'span' })).toBeTruthy();
    expect(within(queue).queryByRole('link')).toBeNull();
  });

  it('drops backslash and tab/newline-smuggled protocol-relative URLs', () => {
    const data = baseData();
    data.capabilities[0].evidence = [{ label: 'Evidence', url: '/\t/evil.example/x' }];
    render(<CapabilityDashboard data={data} />);

    expect(screen.queryByRole('link', { name: 'Evidence' })).toBeNull();
  });

  it('drops non-relative trace and evidence URLs', () => {
    const data = baseData();
    data.capabilities[0].evidence = [{ label: 'Evidence', url: '//evil.example/x' }];
    render(<CapabilityDashboard data={data} />);

    expect(screen.queryByRole('link', { name: 'Evidence' })).toBeNull();
  });
});

describe('design-only cards', () => {
  it('Design_only_cards_and_roles_have_no_actions', () => {
    const data = baseData();
    data.capabilities[2].openItems = 3;
    data.capabilities[2].nextDecisionOwner = 'Someone';
    data.capabilities[2].evidence = [{ label: 'Design evidence', url: '/design' }];
    const { rerender } = render(<CapabilityDashboard data={data} />);

    const card = screen.getByRole('article', { name: 'Transition planning' });
    expect(within(card).getByText('Purpose: Transition planning purpose')).toBeTruthy();
    expect(within(card).getByText('Intended autonomy: Proposes only')).toBeTruthy();
    expect(within(card).getByText('Human authority: A named human approves')).toBeTruthy();
    expect(within(card).getByText('Not implemented in this demo')).toBeTruthy();
    expect(within(card).getByText('Case study: L2, recommend')).toBeTruthy();
    expect(within(card).getByText('Demo: design-only: not implemented in this demo')).toBeTruthy();
    expect(within(card).queryByRole('button')).toBeNull();
    expect(within(card).queryByText(/Open items/)).toBeNull();
    expect(within(card).queryByRole('link')).toBeNull();
    expect(within(card).queryByText(/Someone/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Accept finding' })).toBeTruthy();
    data.allowedDecisions = [];
    rerender(<CapabilityDashboard data={data} />);
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('role-permitted decisions', () => {
  it('offers only the server-projected technical-review decisions', () => {
    const onDecision = vi.fn();
    render(<CapabilityDashboard data={baseData()} onDecision={onDecision} />);

    fireEvent.click(screen.getByRole('button', { name: 'Accept finding' }));
    expect(onDecision).toHaveBeenCalledWith('technical-review', 'accept');
    expect(screen.getAllByRole('button')).toHaveLength(2);
  });

  it('shows no decision action when the role has none', () => {
    const data = baseData();
    data.allowedDecisions = [];
    render(<CapabilityDashboard data={data} />);

    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.getByText('Open items: 2')).toBeTruthy();
  });
});

describe('inert rendering of document and model content', () => {
  it('Renders_untrusted_content_as_text', () => {
    const payload = '<img src=x onerror="alert(1)"><script>alert(1)</script><button onclick="alert(2)">x</button> [go](javascript:alert(1))';
    const data = baseData();
    data.activity[0].outcome = payload;
    data.capabilities[1].nextDecisionOwner = payload;
    const { container } = render(<CapabilityDashboard data={data} />);

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('button[onclick]')).toBeNull();
    expect(container.querySelector('a[href^="javascript"]')).toBeNull();
    expect(screen.getByText(/Next decision owner:/).textContent).toContain(payload);
    expect(container.textContent).toContain(payload);
  });
});

it('Renders_server_autonomy_lines_for_built_and_design_only_capabilities', () => {
  const data = baseData();
  data.capabilities.push({
    ...data.capabilities[1],
    id: 'utilisation-reserves',
    name: 'Utilisation and reserves',
    caseStudyAutonomy: 'L3, acts (invoices) with finance review',
    demoAutonomy: 'L2: drafts a reserve calculation; a simulated finance review decides; no invoice',
  });
  data.capabilities[0].caseStudyAutonomy = undefined;
  data.capabilities[0].demoAutonomy = undefined;
  render(<CapabilityDashboard data={data} />);

  const reserves = screen.getByRole('article', { name: 'Utilisation and reserves' });
  expect(within(reserves).getByText('Case study: L3, acts (invoices) with finance review')).toBeTruthy();
  expect(within(reserves).getByText('Demo: L2: drafts a reserve calculation; a simulated finance review decides; no invoice')).toBeTruthy();
  const transition = screen.getByRole('article', { name: 'Transition planning' });
  expect(within(transition).getByText('Case study: L2, recommend')).toBeTruthy();
  expect(within(transition).getByText('Demo: design-only: not implemented in this demo')).toBeTruthy();
  const records = screen.getByRole('article', { name: 'Records investigation' });
  expect(within(records).getByText('Case study: Unknown')).toBeTruthy();
  expect(within(records).getByText('Demo: Unknown')).toBeTruthy();
});
