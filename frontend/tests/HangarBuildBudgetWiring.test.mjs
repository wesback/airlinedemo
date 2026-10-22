import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

describe('Hangar build budget wiring', () => {
  it('Hangar build script wires supplied-artifact budget check', () => {
    const manifest = JSON.parse(readFileSync('package.json', 'utf8'));
    const build = manifest.scripts.build;
    const viteIndex = build.indexOf('vite build');
    const budgetIndex = build.indexOf('check-hangar-budgets.mjs dist');
    expect(viteIndex).toBeGreaterThanOrEqual(0);
    expect(budgetIndex).toBeGreaterThan(viteIndex);

    const app = readFileSync('src/App.tsx', 'utf8');
    expect(app).toContain("await import('./hangar/entryController')");
    expect(app).toContain('selectHangarEntry');
    expect(app).toContain('hangarLazyLoader ??');
    expect(app).not.toMatch(/@react-three\/fiber.*(?:import|from)/s);

    const checker = readFileSync('scripts/check-hangar-budgets.mjs', 'utf8');
    expect(checker).toContain('gzipSync');
    expect(checker).toContain('hangar-build-budget.json');
  });
});
