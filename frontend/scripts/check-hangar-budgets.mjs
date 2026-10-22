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
    const attribution = asset.hangarOnly ?? asset.hangarOnlyAttribution ?? asset.attribution ?? asset.classification;
    if (!isAttributed(attribution)) throw new Error('Hangar build attribution is missing');
    const bytes = asset.bytes ?? asset.size;
    if (!Number.isSafeInteger(bytes) || bytes < 0) throw new Error('Hangar build budget metadata is missing');
    if (attribution !== false && attribution !== 'shared') assets += bytes;
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
if (!existsSync(viteManifestPath)) throw new Error('Hangar build budget metadata is missing');
const manifest = JSON.parse(readFileSync(viteManifestPath, 'utf8'));
const entries = Object.entries(manifest);
const hangarEntryKeys = new Set(entries
  .filter(([key, value]) => key.toLowerCase().includes('hangar') || value?.src?.toLowerCase?.().includes('hangar'))
  .map(([key]) => key));
const hangarFiles = new Set();
const hangarAssets = new Set();
const visited = new Set();
const visit = (key) => {
  if (visited.has(key)) return;
  visited.add(key);
  const value = manifest[key];
  if (!value || typeof value !== 'object') return;
  if (typeof value.file === 'string') hangarFiles.add(value.file);
  for (const asset of [...(value.assets ?? []), ...(value.css ?? []), ...(typeof value.file === 'string' ? [value.file] : [])]) if (typeof asset === 'string') hangarAssets.add(asset);
  for (const dependency of [...(value.imports ?? []), ...(value.dynamicImports ?? [])]) visit(dependency);
};
for (const key of hangarEntryKeys) visit(key);

const chunks = [];
const assets = [];
const dependencyChunkMappings = {};
const walk = (directory) => {
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) walk(path);
    else {
      const rel = relative(root, path).replaceAll('\\\\', '/');
      if (/\.js$/i.test(name)) {
        const record = entries.find(([, value]) => value.file === rel);
        const hangarOnly = hangarEntryKeys.has(record?.[0]) || hangarFiles.has(rel);
        chunks.push({ file: rel, gzipBytes: gzipSync(readFileSync(path)).length, hangarOnly });
        dependencyChunkMappings[rel] = { hangarOnly };
      }
      const isModelOrTexture = /\.(glb|gltf|png|jpe?g|webp|ktx2|basis)$/i.test(name);
      const manifestAsset = entries.find(([, value]) => {
        const candidates = [...(value.assets ?? []), ...(value.file ? [value.file] : []), ...(value.src ? [value.src] : [])];
        return candidates.some((candidate) => typeof candidate === 'string' && (candidate === rel || candidate.includes(rel) || rel.includes(candidate)));
      });
      const canonicalAssetPaths = (() => {
        try {
          const provenance = JSON.parse(readFileSync(join(process.cwd(), 'src/assets/hangar/hangar-provenance.json'), 'utf8'));
          const manifest = JSON.parse(readFileSync(join(process.cwd(), 'src/hangar/sceneManifest.json'), 'utf8'));
          const paths = [
            ...(provenance.assets ?? []).map((asset) => asset.path),
            manifest.rack?.path, manifest.avatar?.path,
            ...(manifest.aircraft?.models ?? []).map((asset) => asset.path),
          ];
          return new Set(paths.filter((path) => typeof path === 'string'));
        } catch { return new Set(); }
      })();
      const canonicalAsset = [...canonicalAssetPaths].some((canonical) => rel.endsWith(canonical.split('/').at(-1)) || rel.includes(canonical));
      const hangarAsset = isModelOrTexture && (rel.toLowerCase().includes('/hangar/') || hangarAssets.has(rel) || hangarFiles.has(rel) || canonicalAsset || manifestAsset?.[0]?.toLowerCase().includes('hangar'));
      if (isModelOrTexture) {
        const hangarOnly = hangarAsset;
        assets.push({ file: rel, bytes: statSync(path).size, hangarOnly });
      }
    }
  }
};
walk(root);
input = { chunks, assets, dependencyChunkMappings };
}

validate(input);
console.log('Hangar build budgets passed');
