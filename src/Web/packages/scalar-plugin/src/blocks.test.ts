import { describe, expect, it } from 'vitest';
import { parseBlocks, parseInline } from './blocks';
import { currentDocument, resourcesOf, scopesOf } from './spec';

describe('parseBlocks', () => {
  it('reads the blocks of a page in order, with their arguments', () => {
    const source = [
      'Text.',
      '```callout warning',
      'Keep it **safe**.',
      '```',
      '```bash',
      'curl example',
      '```',
      '```resources Health Data',
      '```',
      '```scopes',
      '```',
    ].join('\r\n');

    expect(parseBlocks(source)).toEqual([
      { name: 'callout', argument: 'warning', body: 'Keep it **safe**.' },
      { name: 'resources', argument: 'Health Data', body: '' },
      { name: 'scopes', argument: '', body: '' },
    ]);
  });
});

describe('parseInline', () => {
  it('renders code, strong and links, and drops a link it cannot trust', () => {
    expect(parseInline('Send `x` to **y**, see [a](#nocturne/tag/a) or [b](javascript:alert(1)).')).toEqual([
      { kind: 'text', text: 'Send ' },
      { kind: 'code', text: 'x' },
      { kind: 'text', text: ' to ' },
      { kind: 'strong', text: 'y' },
      { kind: 'text', text: ', see ' },
      { kind: 'link', text: 'a', href: '#nocturne/tag/a' },
      { kind: 'text', text: ' or ' },
      { kind: 'text', text: 'b' },
      { kind: 'text', text: ').' },
    ]);
  });
});

describe('spec readers', () => {
  const spec = {
    paths: { '/a': { get: { tags: ['Sensor Glucose'] }, post: { tags: ['Sensor Glucose'] } } },
    tags: [
      { name: 'overview-glucose', description: 'Notes.' },
      { name: 'Sensor Glucose', description: 'CGM readings. List them.', 'x-displayName': 'Sensor' },
    ],
    'x-tagGroups': [{ name: 'Glucose', tags: ['overview-glucose', 'Sensor Glucose'] }],
    components: {
      securitySchemes: {
        oauth2: { flows: { authorizationCode: { scopes: { 'glucose.read': 'Read glucose' } } } },
        bearer: {},
      },
    },
  };

  it('lists the entries of a group that have operations', () => {
    expect(resourcesOf(spec, 'nocturne', 'Glucose')).toEqual([
      { label: 'Sensor', href: '#nocturne/tag/sensor-glucose', summary: 'CGM readings.', operations: 2 },
    ]);
  });

  it('lists the scopes of the security schemes', () => {
    expect(scopesOf(spec)).toEqual([{ name: 'glucose.read', description: 'Read glucose' }]);
  });

  it('reads the document from the hash', () => {
    expect(currentDocument('#nightscout/tag/v1-entries')).toBe('nightscout');
    expect(currentDocument('')).toBe('nocturne');
  });
});
