import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { join, relative } from 'node:path';
const JAVASCRIPT_GZIP_BUDGET_BYTES = 1_572_864;
const ASSET_BUDGET_BYTES = 15_728_640;
function isAttributed(value) {
  return value === true || value === 'hangar-only' || value === 'hangar' || value === false || value === 'shared';
}
function validate(input) {
  if (!input || typeof input !== 'object') throw new Error('Hangar build budget metadata is missing');
  if (!Array.isArray(input.chunks) || !Array.isArray(input.assets)) throw new Error('Hangar build budget metadata is missing');
  if (!input.dependencyChunkMappings || typeof input.dependencyChunkMappings !== 'object') throw new Error('Hangar build budget metadata is missing');
  let js = 0;
  for (const chunk of input.chunks) {
    const attribution = chunk.hangarOnly ?? chunk.hangarOnlyAttribution ?? chunk.attribution ?? chunk.classification;
    if (!isAttributed(attribution)) throw new Error('Hangar build attribution is missing');
    if (attribution !== false && attribution !== 'shared') {
      if (!Number.isSafeInteger(chunk.gzipBytes ?? chunk.gzipSize ?? chunk.bytes ?? chunk.size) || (chunk.gzipBytes ?? chunk.gzipSize ?? chunk.bytes ?? chunk.size) < 0) throw new Error('Hangar build budget metadata is missing');
      js += chunk.gzipBytes ?? chunk.gzipSize ?? chunk.bytes ?? chunk.size;
    }
  }
  let assets = 0;
  for (const asset of input.assets) {
    const bytes = asset.bytes ?? asset.size;
    if (!Number.isSafeInteger(bytes) || bytes < 0) throw new Error('Hangar build budget metadata is missing');
    assets += bytes;
  }
  for (const value of Object.values(input.dependencyChunkMappings)) {
    const classification = value && typeof value === 'object' ? value.hangarOnly ?? value.hangarOnlyAttribution ?? value.attribution ?? value.classification : value;
    if (!isAttributed(classification)) throw new Error('Hangar build attribution is missing');
  }
  if (js > JAVASCRIPT_GZIP_BUDGET_BYTES || assets > ASSET_BUDGET_BYTES) throw new Error('Hangar build budget exceeded or build attribution is missing');
}

const root = process.argv[2] ? new URL(process.argv[2], `file://${process.cwd()}/`).pathname : join(process.cwd(), 'dist');
const metadataPath = join(root, 'hangar-build-budget.json');
const viteManifestPath = join(root, '.vite', 'manifest.json');

if (!existsSync(root)) {
  throw new Error('Hangar build budget metadata is missing');
}

let input;
if (existsSync(metadataPath)) {
  input = JSON.parse(readFileSync(metadataPath, 'utf8'));
} else {
  const manifest = JSON.parse(readFileSync(viteManifestPath, 'utf8'));
  const entries = Object.entries(manifest);
  const chunks = [];
  const assets = [];
  const dependencyChunkMappings = {};
  const walk = (directory) => {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) walk(path);
      else {
        const rel = relative(root, path).replaceAll('\\', '/');
        if (/\.js$/i.test(name)) {
          const record = entries.find(([, value]) => value.file === rel);
          const src = record?.[0] ?? '';
          const hangarOnly = src.includes('hangar') || rel.includes('hangar');
          chunks.push({ file: rel, gzipBytes: gzipSync(readFileSync(path)).length, hangarOnly });
          dependencyChunkMappings[rel] = { hangarOnly };
        }
        if (/\.(glb|png|jpe?g|webp|ktx2|basis)$/i.test(name)) assets.push({ file: rel, bytes: statSync(path).size });
      }
    }
  };
  walk(root);
  input = { chunks, assets: assets.filter((asset) => asset.file.includes('hangar') || asset.file.includes('.vite/')), dependencyChunkMappings };
}

validate(input);
console.log('Hangar build budgets passed');
