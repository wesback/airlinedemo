import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
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
    expect(app).not.toContain('assets/hangar/');
    expect(app).not.toMatch(/(?:fetch\(|new URL\(|import)[^\n]*(?:\.glb|\.gltf|\.ktx2|\.basis)/);

    const entry = readFileSync('src/hangar/entryController.ts', 'utf8');
    expect(entry).toContain("await import('@react-three/fiber')");
    expect(entry).toContain('selectHangarEntry');

    const checker = readFileSync('scripts/check-hangar-budgets.mjs', 'utf8');
    expect(checker).toContain('gzipSync');
    expect(checker).toContain('hangar-build-budget.json');
    expect(checker).toContain('value.imports ?? []');
    expect(checker).toContain('value.file ? [value.file] : []');
    expect(checker).toContain('hangarFiles.has(rel)');

    const runtimeLazyLoad = readFileSync('tests/HangarEntryLazyLoad.test.tsx', 'utf8');
    expect(runtimeLazyLoad).toContain('HangarEntry lazy loads only after selection');
    expect(runtimeLazyLoad).toContain('modelRequest).not.toHaveBeenCalled()');
    expect(runtimeLazyLoad).toContain('hangarLazyLoader');
    const runtime = spawnSync(process.execPath, ['node_modules/vitest/vitest.mjs', '--run', 'tests/HangarEntryLazyLoad.test.tsx'], {
      cwd: process.cwd(), encoding: 'utf8', timeout: 120_000,
    });
    expect(runtime.status).toBe(0);
  });
});
