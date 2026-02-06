import { describe, it, expect } from 'vitest';
import { getClientPropertyName } from '../utils/client-mapping.js';

describe('getClientPropertyName', () => {
  it('maps standard tags to camelCase', () => {
    expect(getClientPropertyName('Trackers')).toBe('trackers');
    expect(getClientPropertyName('Entries')).toBe('entries');
    expect(getClientPropertyName('Treatments')).toBe('treatments');
    expect(getClientPropertyName('StateSpans')).toBe('stateSpans');
  });

  it('strips version prefix', () => {
    expect(getClientPropertyName('V4 Trackers')).toBe('trackers');
    expect(getClientPropertyName('V2 Notifications')).toBe('v2Notifications');
    expect(getClientPropertyName('V1 Entries')).toBe('entries');
  });

  it('maps non-standard Foods tag', () => {
    expect(getClientPropertyName('Foods')).toBe('foodsV4');
  });

  it('maps non-standard DData tag', () => {
    expect(getClientPropertyName('DData')).toBe('v2DData');
  });

  it('maps non-standard Loop tag', () => {
    expect(getClientPropertyName('Loop')).toBe('loopNotifications');
  });

  it('maps non-standard Prediction tag', () => {
    expect(getClientPropertyName('Prediction')).toBe('predictions');
  });

  it('maps non-standard versioned tags', () => {
    expect(getClientPropertyName('Notifications')).toBe('v2Notifications');
    expect(getClientPropertyName('Properties')).toBe('v2Properties');
    expect(getClientPropertyName('Summary')).toBe('v2Summary');
  });

  it('falls back to camelCase for unknown tags', () => {
    expect(getClientPropertyName('NewFeature')).toBe('newFeature');
    expect(getClientPropertyName('SomeThing')).toBe('someThing');
  });
});
