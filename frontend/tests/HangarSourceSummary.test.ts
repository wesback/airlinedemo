import { describe, expect, it, vi } from 'vitest';
import {
  HANGAR_SOURCE_IDS,
  SOURCE_UNAVAILABLE_TEXT,
  SOURCE_SUMMARY_UNAVAILABLE,
  projectHangarSourceSummary,
  refreshHangarSourceSummary,
  validateSourceSummary,
} from '../src/hangar/sourceSummary';

const cases = [
  { caseId: 'cp-case-a', name: 'Case A', category: 'narrow-body' },
  { caseId: 'cp-case-b', name: 'Case B', category: 'regional' },
  { caseId: 'cp-case-c', name: 'Case C', category: 'wide-body' },
];

const summary = (caseId: string, coverage = true, payload?: unknown) => ({
  contractVersion: 1, caseId, fixtureVersion: '1.0.0',
  sources: HANGAR_SOURCE_IDS.map((sourceId, index) => ({
    sourceId, mode: index === 1 ? 'stale' : 'available', coverage: index === 0 ? coverage : false,
    capabilityLabel: 'Simulated',
  })),
  ...(payload === undefined ? {} : { payload, personalData: 'must-not-render' }),
});

describe('HangarSourceSummary', () => {
  it('HangarSourceSummary projects three ordered cases', async () => {
    const reader = vi.fn(async (caseId: string) => summary(caseId));
    const entries = await projectHangarSourceSummary(cases, ['cp-case-a','cp-case-b','cp-case-c'], reader);
    expect(entries).toHaveLength(3);
    expect(entries.map(entry => entry.caseId)).toEqual(['cp-case-a','cp-case-b','cp-case-c']);
    for (let index = 0; index < entries.length; index++) {
      const entry = entries[index];
      expect(entry).toMatchObject({ caseId: cases[index].caseId, name: cases[index].name, category: cases[index].category, unavailable: false });
      expect(entry.sources?.map(source => source.sourceId)).toEqual([...HANGAR_SOURCE_IDS]);
      expect(entry.sources?.[0]).toMatchObject({ mode: 'available', coverage: true, modeText: 'mode: available', coverageText: 'coverage: true' });
    }
    expect(reader).toHaveBeenCalledTimes(3);

    const valid = summary('cp-case-a');
    const invalidCases = [
      { ...valid, contractVersion: 99 },
      { ...valid, sources: valid.sources.slice(1) },
      { ...valid, sources: [{ ...valid.sources[0], sourceId: 'unknown' }, ...valid.sources.slice(1)] },
      { ...valid, sources: [...valid.sources, valid.sources[0]] },
      { ...valid, payload: '<script>PAYLOAD</script>', personalData: 'SYN-PERSON' },
    ];
    for (const bad of invalidCases) {
      expect(validateSourceSummary(bad, 'cp-case-a').valid).toBe(false);
    }
  });

  it('HangarSourceSummary isolates unavailable cases', async () => {
    const reader = vi.fn(async (caseId: string) => {
      if (caseId === 'cp-case-b') throw new Error('request failed');
      if (caseId === 'cp-case-c') return { ...summary(caseId), contractVersion: 2 };
      return summary(caseId, true);
    });
    const first = await projectHangarSourceSummary(cases, ['cp-case-a','cp-case-b','cp-case-c'], reader);
    expect(first[0]).toMatchObject({ unavailable: false });
    expect(first[1]).toEqual({ caseId: 'cp-case-b', name: 'Case B', category: 'regional', unavailable: true, sources: null });
    expect(first[2].unavailable).toBe(true);
    expect(first.every(entry => !entry.sources?.some(source => source.text.includes('stale coverage')))).toBe(true);
    expect(SOURCE_UNAVAILABLE_TEXT).toBe('source summary unavailable');
    expect(SOURCE_SUMMARY_UNAVAILABLE).toBe('source summary unavailable');

    const refreshed = await refreshHangarSourceSummary(cases, ['cp-case-b'], async () => summary('cp-case-b', true));
    expect(refreshed).toHaveLength(1);
    expect(refreshed[0].unavailable).toBe(false);
    expect(refreshed[0].sources?.every(source => Boolean(source.modeText && source.coverageText))).toBe(true);
  });

  it('HangarSourceSummary requests only scope-listed cases', async () => {
    const reader = vi.fn(async (caseId: string) => summary(caseId));
    const scoped = await projectHangarSourceSummary(cases, ['cp-case-b'], reader);
    expect(scoped).toHaveLength(1);
    expect(scoped[0].caseId).toBe('cp-case-b');
    expect(reader).toHaveBeenCalledTimes(1);
    expect(reader).toHaveBeenCalledWith('cp-case-b');

    const browserSupplied = await projectHangarSourceSummary([
      ...cases,
      { caseId: 'cp-case-x', name: 'Injected', category: 'unknown' },
    ], { caseScopes: ['cp-case-b'] }, reader);
    expect(browserSupplied.map(entry => entry.caseId)).toEqual(['cp-case-b']);
    expect(reader).toHaveBeenCalledTimes(2);
  });
});
