import { describe, it, expect, vi, afterEach } from 'vitest';
import { deltaEntry, legacyFamilies, LegacyDeltaBuffer, LEGACY_DELTA_WINDOW_MS } from './legacy-delta.js';

describe('legacyFamilies', () => {
  it('names each v1 collection once for the v1 and V4 categories that feed it', () => {
    expect(legacyFamilies(['entries', 'glucose', 'care', 'heartrate'])).toEqual(['entries', 'treatments']);
  });
});

describe('deltaEntry', () => {
  it('reads an entry by its type', () => {
    expect(deltaEntry('create', { colName: 'entries', doc: { type: 'cal', date: 5 } })).toEqual({
      family: 'entries',
      key: 'cals',
      doc: { type: 'cal', date: 5, mills: 5 },
    });
  });

  it('dates a record from created_at when it carries no mills', () => {
    const entry = deltaEntry('create', { colName: 'treatments', doc: { _id: 't1', created_at: '2026-11-14T00:00:00Z' } });

    expect(entry?.doc).toEqual({ _id: 't1', created_at: '2026-11-14T00:00:00Z', mills: Date.UTC(2026, 10, 14) });
  });

  it('reports a deleted treatment as a removal', () => {
    expect(deltaEntry('delete', { colName: 'treatments', identifier: 't1', doc: { _id: 't1', mills: 9 } })).toEqual({
      family: 'treatments',
      key: 'treatments',
      doc: { _id: 't1', action: 'remove', mills: 9 },
    });
  });

  it('names the collection alone for a deleted entry', () => {
    expect(deltaEntry('delete', { colName: 'entries', identifier: 'e1', doc: { _id: 'e1', type: 'sgv' } })).toEqual({
      family: 'entries',
      key: 'sgvs',
    });
  });

  it.each([
    ['glucose', 'sensorGlucose', 'entries', 'sgvs'],
    ['glucose', 'meterGlucose', 'entries', 'mbgs'],
    ['glucose', 'bgCheck', 'entries', 'treatments'],
    ['device', 'deviceEvent', 'devicestatus', 'treatments'],
    ['device', 'pumpSnapshot', 'devicestatus', 'devicestatus'],
    ['care', 'tempBasal', 'treatments', 'treatments'],
    ['care', 'stateSpan', 'treatments', 'treatments'],
    ['device', 'stateSpan', 'devicestatus', 'treatments'],
  ])('puts a %s %s under the collection a v1 client reads it from', (colName, recordType, family, key) => {
    expect(deltaEntry('create', { colName, recordType, doc: { id: 'x' } })).toEqual({ family, key });
  });

  it('ignores a category no classic client reads', () => {
    expect(deltaEntry('create', { colName: 'heartrate', doc: {} })).toBeNull();
  });
});

describe('LegacyDeltaBuffer', () => {
  afterEach(() => vi.useRealTimers());

  it('keeps tenants and collections apart, and emits each once per window', () => {
    vi.useFakeTimers();
    const emit = vi.fn();
    const buffer = new LegacyDeltaBuffer(emit);

    buffer.add('rhys', 'create', { colName: 'entries', doc: { _id: 'a', sgv: 100, date: 1 } });
    buffer.add('other', 'create', { colName: 'entries', doc: { _id: 'b', sgv: 110, date: 1 } });
    buffer.add('rhys', 'create', { colName: 'profiles', doc: { _id: 'p' } });
    vi.advanceTimersByTime(LEGACY_DELTA_WINDOW_MS);
    buffer.add('rhys', 'create', { colName: 'entries', doc: { _id: 'c', sgv: 120, date: 2 } });
    vi.advanceTimersByTime(LEGACY_DELTA_WINDOW_MS);

    expect(emit.mock.calls).toEqual([
      ['rhys', 'entries', { delta: true, sgvs: [{ _id: 'a', sgv: 100, date: 1, mills: 1, mgdl: 100 }] }],
      ['other', 'entries', { delta: true, sgvs: [{ _id: 'b', sgv: 110, date: 1, mills: 1, mgdl: 110 }] }],
      ['rhys', 'profiles', { delta: true, profiles: [{ _id: 'p' }] }],
      ['rhys', 'entries', { delta: true, sgvs: [{ _id: 'c', sgv: 120, date: 2, mills: 2, mgdl: 120 }] }],
    ]);
  });

  it('emits nothing once cleared', () => {
    vi.useFakeTimers();
    const emit = vi.fn();
    const buffer = new LegacyDeltaBuffer(emit);

    buffer.add('rhys', 'create', { colName: 'entries', doc: { _id: 'a', sgv: 100, date: 1 } });
    buffer.clear();
    vi.advanceTimersByTime(LEGACY_DELTA_WINDOW_MS);

    expect(emit).not.toHaveBeenCalled();
  });
});
