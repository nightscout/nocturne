import { describe, it, expect } from 'vitest';
import { getClientPropertyName } from '../utils/client-mapping.js';

describe('getClientPropertyName', () => {
  it('maps standard tags to camelCase', () => {
    expect(getClientPropertyName('Trackers')).toBe('trackers');
    expect(getClientPropertyName('Entries')).toBe('entries');
    expect(getClientPropertyName('Treatments')).toBe('treatments');
    expect(getClientPropertyName('StateSpans')).toBe('stateSpans');
  });

  it('strips version prefix and camelCases', () => {
    expect(getClientPropertyName('V4 Trackers')).toBe('trackers');
    expect(getClientPropertyName('V2 Notifications')).toBe('notifications');
    expect(getClientPropertyName('V1 Entries')).toBe('entries');
  });

  it('strips spaces from multi-word tags', () => {
    expect(getClientPropertyName('Loop Notifications')).toBe('loopNotifications');
    expect(getClientPropertyName('Chart Data')).toBe('chartData');
    expect(getClientPropertyName('V4 Device Age')).toBe('deviceAge');
    expect(getClientPropertyName('V4 Connector Settings')).toBe('connectorSettings');
  });

  it('applies pure camelCase without overrides', () => {
    // These tags now rely on [ClientPropertyName] for non-standard names
    expect(getClientPropertyName('Foods')).toBe('foods');
    expect(getClientPropertyName('DData')).toBe('dData');
    expect(getClientPropertyName('Prediction')).toBe('prediction');
    expect(getClientPropertyName('IOB')).toBe('iOB');
    expect(getClientPropertyName('UI Settings')).toBe('uISettings');
  });

  it('falls back to camelCase for unknown tags', () => {
    expect(getClientPropertyName('NewFeature')).toBe('newFeature');
    expect(getClientPropertyName('SomeThing')).toBe('someThing');
  });
});
