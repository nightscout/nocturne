import { describe, expect, it } from 'vitest';
import { isSafeHref, pairBlocks, parseBlocks, parseInline } from './blocks';
import { type Spec, documentsFrom, firstRows, resourcesOf, scopesOf, tagHref } from './spec';

const lines = (...text: string[]) => text.join('\n');

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

  it('does not read a longer language name as a block', () => {
    expect(parseBlocks(lines('```callouts', 'x', '```'))).toEqual([]);
  });

  it('reads a longer or indented fence, and ends it where markdown does', () => {
    const source = lines('1. Step', '   ````callout tip', '   a', '   ````', '```resources Glucose', '```');

    expect(parseBlocks(source).map((block) => [block.name, block.argument])).toEqual([
      ['callout', 'tip'],
      ['resources', 'Glucose'],
    ]);
  });

  it('ends a block at a closing fence longer than the opening one', () => {
    const source = lines('```callout', 'a', '````', '```resources Glucose', '```');

    expect(parseBlocks(source).map((block) => block.name)).toEqual(['callout', 'resources']);
  });
});

describe('pairBlocks', () => {
  const blocks = parseBlocks(lines('```resources A', '```', '```callout tip', 'x', '```', '```resources B', '```'));

  it('pairs the blocks of one name in order, whatever lies between them', () => {
    expect(pairBlocks('resources', ['first', 'second'], blocks)?.map(([element, block]) => [element, block.argument])).toEqual([
      ['first', 'A'],
      ['second', 'B'],
    ]);
  });

  it('pairs nothing when the page renders a block the source parser did not find', () => {
    expect(pairBlocks('callout', ['one', 'two'], blocks)).toBeNull();
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

  it('keeps a link on this site and refuses one that names another host', () => {
    expect(['https://a.example/x', '#nocturne/tag/a', '/docs'].every(isSafeHref)).toBe(true);
    expect(['//evil.example', '/\\evil.example', 'http://a.example', 'JavaScript:alert(1)', ' javascript:x'].some(isSafeHref)).toBe(false);
  });
});

describe('spec readers', () => {
  const spec: Spec = {
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

  it('makes the anchor of a tag from its letters, digits, spaces and hyphens', () => {
    expect(tagHref('nightscout', 'V1 Entries')).toBe('#nightscout/tag/v1-entries');
    expect(tagHref('nocturne', 'Carbs & Meals / v2.')).toBe('#nocturne/tag/carbs-meals-v2');
  });

  it('reads the open document first, and every document when the hash names none', () => {
    expect(documentsFrom('#nightscout/tag/v1-entries')).toEqual(['nightscout', 'nocturne']);
    expect(documentsFrom('')).toEqual(['nocturne', 'nightscout']);
  });

  it('falls back to the next document when the first has no rows', async () => {
    const read = (document: Spec, name: string) => resourcesOf(document, name, 'Glucose');
    const load = async (name: string) => (name === 'nightscout' ? spec : {});

    const rows = await firstRows(['nocturne', 'nightscout'], read, load);

    expect(rows.map((row) => row.href)).toEqual(['#nightscout/tag/sensor-glucose']);
  });
});
