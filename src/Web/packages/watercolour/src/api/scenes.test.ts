import { describe, expect, it, vi } from 'vitest';
import { type IconHints, type IconNode } from '../types';
import { WatercolourError, toWatercolourError } from './errors';
import { ICON_HINTS } from './icon-hints';
import { type IconRef, authoredSceneJson, iconSvg, isIconRef, mergeIconHints, paletteKey, parseSceneDocument, resolveSceneJson } from './scenes';
import { type IconArtworkSource, type PigmentRole, type IconNode as IndexIconNode, type IconHints as IndexIconHints, ICON_HINTS as INDEX_ICON_HINTS, mergeIconHints as indexMergeIconHints, iconSvg as indexIconSvg } from '../index';

vi.mock('../components/Artwork.svelte', () => ({ default: {} }));
vi.mock('../components/ArtworkHero.svelte', () => ({ default: {} }));
vi.mock('../components/PaintedUnderline.svelte', () => ({ default: {} }));
vi.mock('../components/SelectionEdge.svelte', () => ({ default: {} }));
vi.mock('../components/AvatarWash.svelte', () => ({ default: {} }));
vi.mock('../components/ConfirmationBackground.svelte', () => ({ default: {} }));
vi.mock('../components/HeaderMotif.svelte', () => ({ default: {} }));
vi.mock('../components/DropSurface.svelte', () => ({ default: {} }));
vi.mock('../components/DropGroup.svelte', () => ({ default: {} }));

describe('parseSceneDocument', () => {
  it('accepts version 1 and reports the id', () => {
    expect(parseSceneDocument('{"version":1,"id":"wash-water-7"}')).toEqual({ version: 1, id: 'wash-water-7' });
  });

  it('rejects other and missing versions with UnsupportedVersion', () => {
    for (const doc of ['{"version":2}', '{"version":"1"}', '{}']) {
      try {
        parseSceneDocument(doc);
        expect.unreachable(doc);
      } catch (error) {
        expect(error).toBeInstanceOf(WatercolourError);
        expect((error as WatercolourError).code).toBe('UnsupportedVersion');
      }
    }
  });

  it('rejects non-JSON and non-objects with InvalidScene', () => {
    for (const doc of ['nope', '[]', 'null']) {
      try {
        parseSceneDocument(doc);
        expect.unreachable(doc);
      } catch (error) {
        expect((error as WatercolourError).code).toBe('InvalidScene');
      }
    }
  });
});

describe('resolveSceneJson', () => {
  it('passes the palette key, defaults and detail through to the engine', () => {
    const calls: unknown[][] = [];
    const module = {
      catalogueScene: (...args: unknown[]) => {
        calls.push(args);
        return '{"version":1}';
      },
      iconScene: () => '{"version":1}',
    };
    resolveSceneJson(module, { id: 'crescent-moon', palette: 'dusk', surface: 'dark', seed: 42, intensity: 0.9, detail: 'small' });
    resolveSceneJson(module, { id: 'wash' });
    expect(calls).toEqual([
      ['crescent-moon', 42, 'dusk', 0.9, 'small', 'dark', 0],
      ['wash', 0, 'moonlight', 0.7, 'large', 'light', 0],
    ]);
  });

  it('lets a live override win over the ref detail and passes the sim resolution', () => {
    const calls: unknown[][] = [];
    const module = {
      catalogueScene: (...args: unknown[]) => {
        calls.push(args);
        return '{"version":1}';
      },
      iconScene: () => '{"version":1}',
    };
    resolveSceneJson(module, { id: 'moonlit-shoreline', detail: 'medium' }, { detail: 'extraLarge', simResolution: 480 });
    resolveSceneJson(module, { id: 'wash' }, { simResolution: 512 });
    expect(calls).toEqual([
      ['moonlit-shoreline', 0, 'moonlight', 0.7, 'extraLarge', 'light', 480],
      ['wash', 0, 'moonlight', 0.7, 'large', 'light', 512],
    ]);
  });

  it('turns the engine message into a typed error', () => {
    const module = {
      catalogueScene: () => {
        throw new Error('UnknownArtwork: alarm-bell');
      },
      iconScene: () => {
        throw new Error('UnknownArtwork: clock');
      },
    };
    try {
      resolveSceneJson(module, { id: 'alarm-bell' });
      expect.unreachable();
    } catch (error) {
      expect((error as WatercolourError).code).toBe('UnknownArtwork');
      expect((error as WatercolourError).message).toBe('alarm-bell');
    }
  });
});

describe('resolveSceneJson with an icon source', () => {
  const clock: IconNode[] = [
    ['circle', { cx: '12', cy: '12', r: '10' }],
    ['path', { d: 'M12 6v6l4 2' }],
  ];

  it('serialises the element list and forwards the name and args to iconScene', () => {
    const calls: unknown[][] = [];
    const module = {
      catalogueScene: () => '{"version":1}',
      iconScene: (...args: unknown[]) => {
        calls.push(args);
        return '{"version":1}';
      },
    };
    resolveSceneJson(module, { icon: clock, name: 'clock', palette: 'dusk', surface: 'dark', seed: 42, detail: 'small' });
    resolveSceneJson(module, { icon: clock, name: 'clock' });
    expect(JSON.parse(calls[0][0] as string)).toEqual(clock);
    expect(calls[0]).toEqual([
      JSON.stringify(clock),
      'clock',
      42,
      'dusk',
      0.7,
      'small',
      'dark',
      0,
      '{"markRadius":1.4}',
    ]);
    expect(calls[1]).toEqual([
      JSON.stringify(clock),
      'clock',
      0,
      'moonlight',
      0.7,
      'large',
      'light',
      0,
      '{"markRadius":1.4}',
    ]);
  });

  it('merges the built-in hints under the caller hints, caller winning per field', () => {
    const calls: unknown[][] = [];
    const module = {
      catalogueScene: () => '{"version":1}',
      iconScene: (...args: unknown[]) => {
        calls.push(args);
        return '{"version":1}';
      },
    };
    resolveSceneJson(module, { icon: clock, name: 'clock', hints: { markRadius: 2 } });
    resolveSceneJson(module, { icon: clock, name: 'clock', hints: { smallMarks: 1 } });
    expect(calls[0][8]).toBe('{"markRadius":2}');
    expect(calls[1][8]).toBe('{"markRadius":1.4,"smallMarks":1}');
  });

  it('sends an empty hints string for names without built-in tuning', () => {
    const calls: unknown[][] = [];
    const module = {
      catalogueScene: () => '{"version":1}',
      iconScene: (...args: unknown[]) => {
        calls.push(args);
        return '{"version":1}';
      },
    };
    resolveSceneJson(module, { icon: clock, name: 'no-such-icon' });
    expect(calls[0][8]).toBe('');
  });

  it('merges only defined caller fields and leaves the table readable', () => {
    expect(mergeIconHints('database')).toEqual({ fill: [1] });
    expect(mergeIconHints('database', { fill: [2] })).toEqual({ fill: [2] });
    expect(mergeIconHints('battery', {})).toEqual({ markRadius: 1.6 });
    expect(mergeIconHints('unknown')).toBeUndefined();
    expect(mergeIconHints('unknown', { smallMarks: 2 })).toEqual({ smallMarks: 2 });
    expect(ICON_HINTS['key']).toEqual({ holes: [[7.5, 15.5, 2.2]] });
    const typed: IconHints = { fill: [1], markRadius: 1.4, smallMarks: 4, holes: [[0, 0, 1]], bodyRole: 'accent', markRole: 'glow' };
    expect(typed.fill).toEqual([1]);
  });

  it('distinguishes icon sources from artwork refs and scene documents', () => {
    expect(isIconRef({ icon: clock, name: 'clock' })).toBe(true);
    expect(isIconRef({ id: 'clock' })).toBe(false);
    expect(isIconRef({ sceneJson: '{"version":1}' })).toBe(false);
  });

  it('accepts the vanilla lucide IconNode type as an icon source', () => {
    type LucideIconNode = import('lucide').IconNode;
    const host: LucideIconNode = [
      ['circle', { cx: '12', cy: '12', r: '10' }],
      ['path', { d: 'M12 6v6l4 2' }],
    ];
    const source: IconRef = { icon: host, name: 'clock' };
    expect(isIconRef(source)).toBe(true);
  });

  it('builds a plain Lucide SVG for the static fallback', () => {
    const svg = iconSvg(clock, 'light');
    expect(svg).toContain('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"');
    expect(svg).toContain('<circle cx="12" cy="12" r="10"/>');
    expect(svg).toContain('<path d="M12 6v6l4 2"/>');
    expect(svg).toContain('stroke-width="2"');
    expect(iconSvg(clock, 'dark')).toContain('stroke="#e8e6e1"');
    expect(iconSvg(clock, 'light')).toContain('stroke="#3d4451"');
  });
});

describe('paletteKey and toWatercolourError', () => {
  it('suffixes dark surfaces and defaults to moonlight', () => {
    expect(paletteKey()).toBe('moonlight');
    expect(paletteKey('ember', 'light')).toBe('ember');
    expect(paletteKey('ember', 'dark')).toBe('ember_dark');
  });

  it('keeps known codes and files the rest under Unknown', () => {
    expect(toWatercolourError(new Error('InstanceLimit: 4 live')).code).toBe('InstanceLimit');
    expect(toWatercolourError('DeviceLost: gone').code).toBe('DeviceLost');
    expect(toWatercolourError(new Error('Something: else')).code).toBe('Unknown');
    expect(toWatercolourError(new TypeError('boom')).message).toBe('boom');
  });
});

describe('index surface', () => {
  it('re-exports the icon surface for hosts', () => {
    expect(typeof INDEX_ICON_HINTS).toBe('object');
    expect(typeof indexMergeIconHints).toBe('function');
    expect(typeof indexIconSvg).toBe('function');
    const hints: IndexIconHints = { fill: [1], bodyRole: 'accent', markRole: 'glow' };
    const source: IconArtworkSource = { icon: [], name: 'clock', hints };
    expect(source.hints?.bodyRole).toBe('accent');
    const node: IndexIconNode = ['circle', { cx: '12', cy: '12', r: '10' }];
    expect(node[0]).toBe('circle');
    const role: PigmentRole = 'shadow';
    expect(source.hints?.markRole).not.toBe(role);
  });
});

describe('authoredSceneJson', () => {
  it('returns a finished document as is and authors a lazy one with the module', () => {
    const module = { catalogueScene: () => '{"donor":true}' } as never;
    expect(authoredSceneJson({ sceneJson: '{"a":1}' }, module)).toBe('{"a":1}');
    expect(authoredSceneJson({ scene: (m) => m.catalogueScene('x', 0, 'water', 0.7, 'large', 'light', 0) }, module)).toBe('{"donor":true}');
  });
});
