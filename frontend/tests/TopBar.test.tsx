import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TopBar from '../src/journey/TopBar';
import { PERSONAS } from '../src/journey/model';

afterEach(cleanup);

describe('TopBar', () => {
  it('shows Data flows navigation for every persona', () => {
    for (const persona of PERSONAS) {
      const { unmount } = render(<TopBar
        session={{ id: persona.id, displayName: persona.label, role: persona.role, caseScopes: [], csrfToken: 'csrf' }}
        mode="board" dataFlowsActive={false} onMode={vi.fn()} onHome={vi.fn()} onDataFlows={vi.fn()}
        onSwitchPersona={vi.fn()} onSignOut={vi.fn()} theme="light" onToggleTheme={vi.fn()}
      />);
      expect(screen.getByRole('button', { name: 'Data flows' })).toBeTruthy();
      unmount();
    }
  });
});
