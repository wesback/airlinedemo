import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { validateHangarDependencyAudit } from '../src/hangar/dependencyPins.ts';

function runSeverityGate(severity, dotnetLines = '') {
  const workflow = readFileSync('../.github/workflows/build.yml', 'utf8');
  const gateStart = workflow.indexOf('      - name: Severity gate\n');
  const scriptStart = workflow.indexOf("          node <<'EOF'\n", gateStart);
  const scriptEnd = workflow.indexOf('\n          EOF', scriptStart);
  expect(gateStart).toBeGreaterThanOrEqual(0);
  expect(scriptStart).toBeGreaterThanOrEqual(0);
  expect(scriptEnd).toBeGreaterThan(scriptStart);
  const script = workflow.slice(scriptStart + "          node <<'EOF'\n".length, scriptEnd);

  const root = mkdtempSync(join(tmpdir(), 'dependency-audit-gate-'));
  try {
    mkdirSync(join(root, 'docs/security'), { recursive: true });
    mkdirSync(join(root, 'artifacts/audit'), { recursive: true });
    writeFileSync(join(root, 'docs/security/dependency-audit-exceptions.json'), '{"exceptions":[]}');
    writeFileSync(join(root, 'artifacts/audit/npm-audit.json'), JSON.stringify({
      metadata: {},
      vulnerabilities: { vite: { severity } },
    }));
    writeFileSync(join(root, 'artifacts/audit/dotnet-vulnerable.txt'), dotnetLines);
    return spawnSync(process.execPath, ['-e', script], { cwd: root, encoding: 'utf8' });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

describe('dependency audit workflow', () => {
  it('keeps the dependency audit exception allowlist empty', () => {
    const exceptions = JSON.parse(readFileSync('../docs/security/dependency-audit-exceptions.json', 'utf8'));

    expect(exceptions.exceptions).toEqual([]);
  });

  it.each(['high', 'critical'])('blocks an unexcepted npm %s finding at runtime', severity => {
    const result = runSeverityGate(severity);

    expect(result.status).toBe(1);
    expect(result.stderr.trim()).toBe(`::error::npm vite has a ${severity} finding with no valid exception`);
  });

  it.each([
    ['High', 'high'],
    ['Critical', 'critical'],
  ])('blocks an unexcepted .NET %s finding at runtime', (severity, expectedSeverity) => {
    const result = runSeverityGate('low', `> Example.Package 1.0.0 1.0.0 ${severity} advisory`);

    expect(result.status).toBe(1);
    expect(result.stderr.trim()).toBe(`::error::dotnet Example.Package has a ${expectedSeverity} finding with no valid exception`);
  });

  it('hangar packages are covered by the existing npm audit evidence contract', () => {
    const manifest = JSON.parse(readFileSync('package.json', 'utf8'));
    const lock = JSON.parse(readFileSync('package-lock.json', 'utf8'));
    const workflow = readFileSync('../.github/workflows/build.yml', 'utf8');

    for (const packageName of ['three', '@react-three/fiber']) {
      expect(manifest.dependencies[packageName]).toBeTruthy();
      expect(lock.packages[`node_modules/${packageName}`]?.version).toBe(manifest.dependencies[packageName]);
    }
    const auditStepStart = workflow.indexOf('      - name: Audit (npm)\n');
    expect(auditStepStart).toBeGreaterThanOrEqual(0);
    const auditStepEnd = workflow.indexOf('\n      - name:', auditStepStart + 1);
    const auditStep = workflow.slice(auditStepStart, auditStepEnd === -1 ? undefined : auditStepEnd);
    expect(auditStep).toContain('working-directory: frontend');
    expect(auditStep).toContain('npm audit --json > ../artifacts/audit/npm-audit.json');

    const auditInput = { vulnerabilities: { three: { severity: 'low' } } };
    expect(() => validateHangarDependencyAudit(manifest, lock, auditInput))
      .toThrow('Hangar audit contract missing package: @react-three/fiber');

    const lockPackageInventory = Object.fromEntries(
      Object.entries(lock.packages)
        .filter(([name]) => name.startsWith('node_modules/'))
        .map(([name]) => [name.slice('node_modules/'.length), {}]),
    );
    expect(validateHangarDependencyAudit(manifest, lock, {
      installed: lockPackageInventory,
      vulnerabilities: {},
    })).toBe(true);

    for (const packageName of ['three', '@react-three/fiber']) {
      const missingPackageInventory = { ...lockPackageInventory };
      delete missingPackageInventory[packageName];
      expect(() => validateHangarDependencyAudit(manifest, lock, {
        installed: missingPackageInventory,
        vulnerabilities: {},
      })).toThrow(`Hangar audit contract missing package: ${packageName}`);
    }
  });

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
