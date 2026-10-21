import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

describe('dependency audit workflow', () => {
  it('retains the strict npm and .NET high/critical severity gate', () => {
    const workflow = readFileSync('../.github/workflows/build.yml', 'utf8');
    const gateStart = workflow.indexOf('      - name: Severity gate\n');
    expect(gateStart).toBeGreaterThanOrEqual(0);
    const nextStep = workflow.indexOf('\n      - name:', gateStart + 1);
    const gate = workflow.slice(gateStart, nextStep === -1 ? undefined : nextStep);

    expect(gate).toContain("if (npm.error) throw new Error('npm audit failed: ' + (npm.error.summary || npm.error.code));");
    expect(gate).toContain("if (!npm.metadata) throw new Error('npm audit output has no metadata');");
    expect(gate).toContain("if (v.severity === 'high' || v.severity === 'critical')");
    expect(gate).toContain("findings.push({ source: 'npm', package: name, severity: v.severity });");
    expect(gate).toContain("/^\\s*>\\s+(\\S+)\\s+\\S+\\s+\\S+\\s+(High|Critical)\\b/i");
    expect(gate).toContain("findings.push({ source: 'dotnet', package: m[1], severity: m[2].toLowerCase() });");
    expect(gate).toContain("f => !exceptions.some(e => e.package === f.package && e.expires > today)");
    expect(gate).toContain('if (blocked.length)');
    expect(gate).toContain('process.exit(1);');
  });
});
