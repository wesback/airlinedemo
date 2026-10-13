// Fails when a package source outside nuget.org or npmjs.org is configured or resolved.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const NUGET_ALLOWED = 'https://api.nuget.org/v3/index.json';
const NPM_ALLOWED_HOST = 'registry.npmjs.org';

const problems = [];

const listing = execFileSync('dotnet', ['nuget', 'list', 'source'], { encoding: 'utf8' });
const lines = listing.split(/\r?\n/);
for (let i = 0; i < lines.length; i++) {
  const header = lines[i].match(/^\s*\d+\.\s+(.+?)\s+\[(Enabled|Disabled)\]\s*$/);
  if (!header || header[2] !== 'Enabled') continue;
  const url = (lines[i + 1] ?? '').trim();
  if (url !== NUGET_ALLOWED) {
    problems.push(`NuGet source outside nuget.org: "${header[1]}" ${url}`);
  }
}

const lock = JSON.parse(readFileSync('frontend/package-lock.json', 'utf8'));
let npmResolved = 0;
const visit = (node) => {
  if (Array.isArray(node)) return node.forEach(visit);
  if (node === null || typeof node !== 'object') return;
  for (const [key, value] of Object.entries(node)) {
    if (key === 'resolved' && typeof value === 'string') {
      npmResolved++;
      let parsed;
      try {
        parsed = new URL(value);
      } catch {
        problems.push(`npm package resolved to an invalid URL: ${value}`);
        continue;
      }
      if (parsed.protocol !== 'https:' || parsed.hostname !== NPM_ALLOWED_HOST || parsed.username || parsed.password || parsed.search || parsed.hash) {
        problems.push(`npm package resolved outside npmjs.org: ${value}`);
      }
    } else {
      visit(value);
    }
  }
};
visit(lock);
// Installable entries must name a registry URL; links and bundled entries are not fetched from a registry.
for (const [key, entry] of Object.entries(lock.packages ?? {})) {
  if (key === '' || entry?.link === true || entry?.inBundle === true) continue;
  if (typeof entry?.resolved !== 'string') {
    problems.push(`npm package has no resolved URL, so its source is unchecked: ${key}`);
  }
}
if (npmResolved === 0) {
  problems.push('frontend/package-lock.json has no resolved npm packages');
}

if (problems.length > 0) {
  for (const p of problems) console.error(`::error::${p}`);
  process.exit(1);
}
console.log('Package sources are limited to nuget.org and npmjs.org.');
