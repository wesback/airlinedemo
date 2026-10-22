import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const readmePath = '../README.md';
const readme = readFileSync(readmePath, 'utf8');

describe('public-safe readiness README', () => {
  it('README_has_the_required_sections_in_order', () => {
    const required = [
      'Purpose', 'Fictional business context', 'Architecture', 'Run locally',
      'Azure prerequisites', 'Configuration', 'Deploy and release',
      'Smoke test, run and reset', 'Teardown', 'Evidence', 'Limitations',
    ];
    let previous = -1;
    for (const section of required) {
      const index = readme.indexOf(`## ${section}`);
      expect(index, `missing or reordered section: ${section}`).toBeGreaterThan(previous);
      previous = index;
    }

    expect(readme).toContain('scripts/bootstrap-deployment.sh');
    expect(readme).toContain('scripts/destroy-deployment.sh --yes');
    expect(readme).toContain('docs/aircraft-return-readiness/README.md');

    const commands = [
      'ASPNETCORE_URLS=http://localhost:5080 dotnet run --project backend/AirlineDemo.Api',
      'npm ci --prefix frontend',
      'npm --prefix frontend run dev',
      'npm --prefix frontend run build',
      'npm --prefix frontend test -- --run',
      'dotnet test AirlineDemo.sln --no-restore -m:1',
      'python3 scripts/tests/test_deployment_scripts.py',
    ];
    for (const command of commands) expect(readme, `missing local command: ${command}`).toContain(command);
  });

  it('README_links_resolve_and_name_the_evidence', () => {
    const requiredLinks = [
      'docs/aircraft-return-readiness/README.md',
      'docs/aircraft-return-readiness/rehearsal-evidence.md',
      'docs/aircraft-return-readiness/cost-model.md',
      'docs/evidence/aircraft-return-readiness-rehearsal-checklist.md',
    ];
    for (const link of requiredLinks) {
      expect(readme, `missing evidence link: ${link}`).toContain(link);
    }

    const evidence = readme.match(/## Evidence\n([\s\S]*?)(?=\n## |$)/)?.[1] ?? '';
    expect(evidence).toContain('versioned evaluation');
    expect(evidence).toContain('cost report');
    expect(evidence).toContain('rehearsal run');
    expect(evidence).toContain('rehearsal evidence page');
    expect(evidence).toContain('not committed as results');

    const links = [...readme.matchAll(/\[[^\]]*\]\(([^)]+)\)/g)].map(match => match[1]);
    for (const link of links) {
      if (/^(?:https?:|mailto:|#)/.test(link)) continue;
      const path = link.split('#')[0].split('?')[0];
      const absolute = resolve(dirname(readmePath), path);
      expect(existsSync(absolute), `broken README link: ${link}`).toBe(true);
    }
  });

  it('README_contains_no_secret_shaped_values', () => {
    const patterns = [
      [/\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b/, 'GUID'],
      [/AccountKey\s*=|SharedAccessSignature\s*=/, 'account key or shared access signature'],
      [/InstrumentationKey\s*=\s*[^<\s]/i, 'InstrumentationKey value'],
      [/\b(?:password|passcode|secret)\s*=\s*(?!<[^>]+>)/i, 'password, passcode, or secret value'],
      [/\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b/, 'RFC 1918 address'],
    ];
    for (const [pattern, label] of patterns) {
      expect(pattern.test(readme), `forbidden secret shape found: ${label}`).toBe(false);
    }

    const config = readme.match(/## Configuration\n([\s\S]*?)(?=\n## |$)/)?.[1] ?? '';
    expect(config).toContain('operator environment');
    expect(config).toContain('Key Vault');
  });
});
