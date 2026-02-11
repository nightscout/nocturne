import { describe, it, expect } from 'vitest';
import { operationIdToFunctionName, tagToFileName, resolveInvalidation } from '../utils/naming.js';

describe('operationIdToFunctionName', () => {
  it('strips tag prefix and camelCases', () => {
    expect(operationIdToFunctionName('Trackers_GetDefinitions')).toBe('getDefinitions');
  });

  it('handles single-segment operationId', () => {
    expect(operationIdToFunctionName('getDefinitions')).toBe('getDefinitions');
  });

  it('handles operationId starting with uppercase', () => {
    expect(operationIdToFunctionName('CreateNote')).toBe('createNote');
  });

  it('handles multi-underscore operationId', () => {
    expect(operationIdToFunctionName('Trackers_Get_All')).toBe('get_All');
  });
});

describe('tagToFileName', () => {
  it('lowercases already-plural tag', () => {
    expect(tagToFileName('Trackers')).toBe('trackers');
  });

  it('pluralizes singular tag', () => {
    expect(tagToFileName('Note')).toBe('notes');
  });

  it('strips version prefix', () => {
    expect(tagToFileName('V4 Trackers')).toBe('trackers');
  });

  it('handles consonant + y -> ies', () => {
    expect(tagToFileName('Battery')).toBe('batteries');
  });

  it('handles vowel + y -> ys', () => {
    expect(tagToFileName('Key')).toBe('keys');
  });

  it('handles sibilant ending (ch)', () => {
    expect(tagToFileName('Match')).toBe('matches');
  });

  it('preserves words already ending in s', () => {
    expect(tagToFileName('Status')).toBe('status');
  });

  it('handles sibilant ending (x)', () => {
    expect(tagToFileName('Box')).toBe('boxes');
  });
});

describe('resolveInvalidation', () => {
  it('resolves simple name within current tag', () => {
    const result = resolveInvalidation('GetDefinitions', 'Trackers');
    expect(result.functionName).toBe('getDefinitions');
    expect(result.fromTag).toBe('Trackers');
  });

  it('resolves cross-tag operationId', () => {
    const result = resolveInvalidation('Trackers_GetActiveInstances', 'Notes');
    expect(result.functionName).toBe('getActiveInstances');
    expect(result.fromTag).toBe('Trackers');
  });
});
