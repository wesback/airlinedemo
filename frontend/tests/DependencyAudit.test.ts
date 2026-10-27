import { describe, expect, it } from 'vitest';
import manifest from '../package.json';
import lock from '../package-lock.json';
import { tokens } from '@fluentui/react-components';
import { altivaneUiTokens } from '../src/theme/altivaneTheme';
import { HANGAR_DEPENDENCY_PIN_ERROR, validateHangarDependencyPins } from '../src/hangar/dependencyPins';
import { createScanner, LanguageVariant, SyntaxKind } from 'typescript/unstable/ast';

declare global {
  interface ImportMeta {
    glob: (pattern: string, options: { eager: boolean; query: string; import: string }) => Record<string, string>;
  }
}

function scanCompilerTokens(source: string, fileName: string): string[] {
  const scanner = createScanner(true, LanguageVariant.Standard, source);
  const found: string[] = [];
  while (true) {
    const kind = scanner.scan();
    if (kind === SyntaxKind.EndOfFile) break;
    found.push(scanner.getTokenText());
  }
  return found;
}

function directFluentStyleValue(value: string): boolean {
  return /^\s*(?:tokens|altivaneUiTokens)\.[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?\s*$/.test(value);
}

function compilerStyleViolations(source: string, fileName: string): string[] {
  const compilerTokens = scanCompilerTokens(source, fileName);
  const violations: string[] = [];
  const coveredNames = new Set([
    'padding', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft', 'paddingInline', 'paddingBlock',
    'margin', 'marginTop', 'marginRight', 'marginBottom', 'marginLeft', 'marginInline', 'marginBlock',
    'gap', 'borderRadius', 'borderRadiusTopLeft', 'borderRadiusTopRight', 'borderRadiusBottomRight', 'borderRadiusBottomLeft',
    'boxShadow', 'fontSize', 'fontFamily', 'lineHeight', 'transitionProperty', 'transitionDuration',
    'transitionTimingFunction', 'transitionDelay', 'animationName', 'animationDuration', 'animationTimingFunction',
    'animationDelay', 'animationIterationCount',
  ]);

  for (let index = 0; index < compilerTokens.length - 2; index += 1) {
    const property = compilerTokens[index].replace(/^(["'])(.*)\1$/, '$2');
    if (!coveredNames.has(property) || compilerTokens[index + 1] !== ':') continue;
    const valueTokens: string[] = [];
    for (let valueIndex = index + 2; valueIndex < compilerTokens.length; valueIndex += 1) {
      const token = compilerTokens[valueIndex];
      if ([',', ';', '}'].includes(token)) break;
      valueTokens.push(token);
    }
    const value = valueTokens.join('').trim();
    if (!directFluentStyleValue(value)) violations.push(`${fileName}:${value}`);
  }
  return violations;
}

const fluentCssVariableNames = new Set([
  '--colorNeutralBackground1', '--colorNeutralBackground2', '--colorNeutralForeground1', '--colorNeutralForeground2',
  '--colorBrandBackground', '--colorBrandBackgroundHover', '--colorBrandBackgroundPressed', '--colorBrandForeground1', '--colorBrandStroke1', '--colorNeutralStroke1',
  '--spacingHorizontalM', '--spacingHorizontalL', '--spacingHorizontalS', '--spacingHorizontalXS',
  '--borderRadiusMedium', '--borderRadiusLarge', '--shadow4', '--fontFamilyBase', '--durationNormal',
]);

function directFluentCssValue(value: string): boolean {
  const match = /^var\((--[A-Za-z0-9]+)\)$/.exec(value.trim());
  return !!match && fluentCssVariableNames.has(match[1]);
}

describe('dependency audit versions', () => {
  it('keeps the manifest and lockfile on a Vite version outside the affected range', () => {
    const manifestVersion = manifest.devDependencies.vite;
    const lockVersion = lock.packages['node_modules/vite'].version;

    expect(manifestVersion).toBe(lockVersion);
    expect(['8.3.0', '8.3.1', '8.3.2']).not.toContain(lockVersion);
  });

  it('hangar dependency pins reject ranges and version mismatch', () => {
    expect(validateHangarDependencyPins(manifest, lock)).toBe(true);

    const ranges: Array<{ dependencies: Record<string, string> }> = [
      { dependencies: { three: '^0.180.0', '@react-three/fiber': '9.8.1' } },
      { dependencies: { three: '~0.180.0', '@react-three/fiber': '9.8.1' } },
      { dependencies: { three: '0.180.0' } },
    ];
    for (const badManifest of ranges) {
      expect(() => validateHangarDependencyPins(badManifest, lock)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
    }

    const mismatch = structuredClone(lock);
    mismatch.packages['node_modules/three'].version = '0.179.1';
    expect(() => validateHangarDependencyPins(manifest, mismatch)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);

    const missing = structuredClone(lock);
    Reflect.deleteProperty(missing.packages, 'node_modules/@react-three/fiber');
    expect(() => validateHangarDependencyPins(manifest, missing)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
  });

  it('Fluent_dependency_pins_match_lockfile', () => {
    expect(manifest.dependencies['@fluentui/react-components']).toBe('9.74.11');
    expect(manifest.dependencies['@fluentui/react-icons']).toBe('2.0.344');
    expect(lock.packages['node_modules/@fluentui/react-components'].version).toBe('9.74.11');
    expect(lock.packages['node_modules/@fluentui/react-icons'].version).toBe('2.0.344');
    expect(Object.keys(manifest.dependencies).sort()).toEqual(['@fluentui/react-components', '@fluentui/react-icons', '@react-three/fiber', 'react', 'react-dom', 'three']);
    const mismatch = structuredClone(manifest);
    mismatch.dependencies['@fluentui/react-components'] = '9.74.10';
    expect(mismatch.dependencies['@fluentui/react-components']).not.toBe(lock.packages['node_modules/@fluentui/react-components'].version);
    const extra = structuredClone(manifest);
    (extra.dependencies as Record<string, string>)['example-runtime'] = '1.0.0';
    expect(Object.keys(extra.dependencies).filter((name) => !['@fluentui/react-components', '@fluentui/react-icons', '@react-three/fiber', 'react', 'react-dom', 'three'].includes(name))).toEqual(['example-runtime']);
  });

  it('Colour_literals_and_fluent_tokens_contract', () => {
    const sourceFiles = import.meta.glob('../src/**/*.{ts,tsx,css}', { eager: true, query: '?raw', import: 'default' });
    const colorPattern = /#[0-9a-f]{3,8}\b|(?:rgba?|hsla?)\([^)]*\)/i;
    const coveredProperty = (name: string) => /^(padding|margin|gap|borderRadius|border-radius|boxShadow|box-shadow|font|font-|lineHeight|line-height|transition|transition-|animation|animation-)/i.test(name);
    const violations: string[] = [];

    for (const [file, source] of Object.entries(sourceFiles)) {
      if (!file.endsWith('/src/theme/altivaneTheme.ts') && colorPattern.test(source)) {
        source.split('\n').forEach((line: string, index: number) => {
          if (colorPattern.test(line)) violations.push(`${file}:${index + 1}:color literal`);
        });
      }

      if (file.endsWith('.tsx') || file.endsWith('.ts')) {
        for (const violation of compilerStyleViolations(source, file)) violations.push(`${file}:${violation}`);
      }

      if (file.endsWith('.css')) {
        const declarationPattern = /([\w-]+)\s*:\s*([^;{}]+);/g;
        for (const declaration of source.matchAll(declarationPattern)) {
          if (coveredProperty(declaration[1]) && !directFluentCssValue(declaration[2].trim())) {
            violations.push(`${file}:non-token CSS value ${declaration[2].trim()}`);
          }
        }
      }
    }

    expect(violations).toEqual([]);

    const fixtures: Array<{ name: string; source: string }> = [
      { name: 'token.tsx', source: '<div style={{ padding: tokens.spacingHorizontalM, borderRadius: tokens.borderRadiusMedium }} />' },
      { name: 'font-literal.tsx', source: '<div style={{ fontFamily: "Segoe UI" }} />' },
      { name: 'font-variable.tsx', source: 'const font = "Segoe UI"; <div style={{ fontFamily: font }} />' },
      { name: 'gap-expression.tsx', source: '<div style={{ gap: calcSpacing() }} />' },
      { name: 'color-literal.tsx', source: 'const approved = false; const outside = "#abc";' },
      { name: 'quoted-property.tsx', source: '<div style={{ "padding": "8px" }} />' },
      { name: 'fixture.css', source: '.example { font-family: "Segoe UI"; }' },
      { name: 'custom-css.css', source: '.example { padding: var(--customPadding); }' },
      { name: 'made-up-css-var.css', source: '.example { padding: var(--spacingMadeUp); }' },
    ];

    const fixtureViolations = fixtures.flatMap(({ name, source }) => {
      const found = name.endsWith('.css') ? [] : compilerStyleViolations(source, name);
      if (name.endsWith('.css')) {
        const declarationPattern = /([\w-]+)\s*:\s*([^;}]+);/g;
        for (const declaration of source.matchAll(declarationPattern)) {
          if (/^(?:padding|margin|gap|borderRadius|border-radius|boxShadow|box-shadow|font|font-|lineHeight|line-height|transition|transition-|animation|animation-)/i.test(declaration[1]) && !directFluentCssValue(declaration[2].trim())) {
            found.push(`${name}:non-token CSS value ${declaration[2].trim()}`);
          }
        }
      }
      if (name === 'color-literal.tsx' && /#[0-9a-f]{3,8}\b/i.test(source)) found.push(`${name}:color literal`);
      return found;
    });

    expect(fixtureViolations).toEqual([
      'font-literal.tsx:"Segoe UI"',
      'font-variable.tsx:font',
      'gap-expression.tsx:calcSpacing()',
      'color-literal.tsx:color literal',
      'quoted-property.tsx:"8px"',
      'fixture.css:non-token CSS value "Segoe UI"',
      'custom-css.css:non-token CSS value var(--customPadding)',
      'made-up-css-var.css:non-token CSS value var(--spacingMadeUp)',
    ]);

    const fluentImports = Object.entries(sourceFiles).filter(([, source]) => source.includes('@fluentui/react-components'));
    expect(fluentImports.length).toBeGreaterThan(0);
    expect(altivaneUiTokens).toEqual({
      spacing: tokens.spacingHorizontalM,
      radius: tokens.borderRadiusMedium,
      elevation: tokens.shadow4,
      font: tokens.fontFamilyBase,
      motion: tokens.durationNormal,
    });
  });

  it('hangar pin contract cases list exact inputs and results', () => {
    const exactManifest = {
      dependencies: { three: '0.180.0', '@react-three/fiber': '9.8.1' },
    };
    const exactLock = {
      packages: {
        'node_modules/three': { version: '0.180.0' },
        'node_modules/@react-three/fiber': { version: '9.8.1' },
      },
    };

    const cases: Array<{ manifest: unknown; lockfile: unknown; valid: boolean }> = [
      { manifest: exactManifest, lockfile: exactLock, valid: true },
      { manifest: { dependencies: { three: '^0.180.0', '@react-three/fiber': '9.8.1' } }, lockfile: exactLock, valid: false },
      { manifest: { dependencies: { three: '~0.180.0', '@react-three/fiber': '9.8.1' } }, lockfile: exactLock, valid: false },
      { manifest: exactManifest, lockfile: { packages: { 'node_modules/three': { version: '0.179.1' }, 'node_modules/@react-three/fiber': { version: '9.8.1' } } }, valid: false },
      { manifest: exactManifest, lockfile: { packages: { 'node_modules/three': { version: '0.180.0' } } }, valid: false },
      { manifest: undefined, lockfile: exactLock, valid: false },
      { manifest: exactManifest, lockfile: undefined, valid: false },
    ];

    for (const contractCase of cases) {
      if (contractCase.valid) {
        expect(validateHangarDependencyPins(contractCase.manifest as never, contractCase.lockfile as never)).toBe(true);
      } else {
        expect(() => validateHangarDependencyPins(contractCase.manifest as never, contractCase.lockfile as never)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
      }
    }
  });
});
