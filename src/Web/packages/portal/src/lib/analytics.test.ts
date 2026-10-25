import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  DOCS_SECTION_IDS,
  INSTALL_PLATFORM_IDS,
  INSTALL_STEP_IDS,
  VIDEO_IDS,
  allowedProps,
  campaignUrl,
  docsSectionAt,
  pageviewUrl,
} from './analytics';
import { DOCS_NAV_SECTIONS } from './data/docs-nav';

describe('allowedProps', () => {
  it('keeps a value the event declares', () => {
    expect(allowedProps('Support Tier Click', { tier: 'patron' })).toEqual({ tier: 'patron' });
  });

  it('drops a value outside the declared set', () => {
    expect(allowedProps('Support Tier Click', { tier: 'benefactor' })).toEqual({});
  });

  it('drops a key the event does not declare', () => {
    expect(allowedProps('Donate Click', { email: 'someone@example.com' })).toEqual({});
  });

  it('drops a key inherited from Object.prototype', () => {
    expect(allowedProps('Donate Click', { constructor: 'anything' })).toEqual({});
  });

  it('keeps the declared key and drops the rest', () => {
    expect(allowedProps('Docs Copy', { kind: 'code', page: '/docs/installation' })).toEqual({
      kind: 'code',
    });
  });
});

describe('docs videos', () => {
  it('reports a published video by id', () => {
    expect(VIDEO_IDS).toContain('agp');
    expect(allowedProps('Video Play', { video: 'agp' })).toEqual({ video: 'agp' });
    expect(allowedProps('Video Complete', { video: 'agp' })).toEqual({ video: 'agp' });
  });

  it('drops an id the video manifest does not hold', () => {
    expect(allowedProps('Video Complete', { video: 'not-a-video' })).toEqual({});
  });
});

describe('pageviewUrl', () => {
  it('keeps the path and drops an unlisted query parameter', () => {
    expect(pageviewUrl(new URL('https://getnocturne.dev/docs?locale=de'))).toBe(
      'https://getnocturne.dev/docs',
    );
  });

  it('keeps campaign parameters', () => {
    expect(
      pageviewUrl(new URL('https://getnocturne.dev/?utm_source=discord&utm_medium=chat')),
    ).toBe('https://getnocturne.dev/?utm_source=discord&utm_medium=chat');
  });

  it('drops the fragment', () => {
    expect(pageviewUrl(new URL('https://getnocturne.dev/get-involved#donate'))).toBe(
      'https://getnocturne.dev/get-involved',
    );
  });
});

describe('docs navigation', () => {
  // `DocsNavSection.id` is typed against DOCS_SECTION_IDS, but this package's `check` script
  // typechecks neither .ts nor .svelte, so nothing in CI enforces that binding. This does.
  it('reports every sidebar section', () => {
    const sidebar = DOCS_NAV_SECTIONS.map((section) => section.id).sort();
    expect(sidebar).toEqual([...DOCS_SECTION_IDS].sort());
  });

  it('declares each section once', () => {
    const ids = DOCS_NAV_SECTIONS.map((section) => section.id);
    expect(new Set(ids).size).toBe(ids.length);
  });
});

describe('an event name that is not in the table', () => {
  it('reports no properties instead of throwing', () => {
    const unknown = 'Docs Navigation' as Parameters<typeof allowedProps>[0];
    expect(() => allowedProps(unknown, { section: 'sdks' })).not.toThrow();
    expect(allowedProps(unknown, { section: 'sdks' })).toEqual({});
  });
});

describe('pageview properties', () => {
  it('keeps the docs section', () => {
    expect(allowedProps('pageview', { doc_section: 'alerts' })).toEqual({ doc_section: 'alerts' });
  });

  it('drops anything else', () => {
    expect(allowedProps('pageview', { depth: '100', doc_section: 'nowhere' })).toEqual({});
  });
});

describe('docsSectionAt', () => {
  it('finds the section of a sidebar page', () => {
    expect(docsSectionAt('/docs/alerts/conditions')).toBe('alerts');
  });

  it('finds the section of a page below a sidebar link', () => {
    expect(docsSectionAt('/docs/reports/loop-analysis/loop-health')).toBe('reports');
  });

  it('gives the docs landing page its own section', () => {
    expect(docsSectionAt('/docs')).toBe('getting-started');
  });

  it('does not put every docs page under the landing page', () => {
    expect(docsSectionAt('/docs/windows-widget-privacy')).toBeUndefined();
  });

  it('gives a page outside the docs no section', () => {
    expect(docsSectionAt('/get-involved')).toBeUndefined();
  });
});

describe('installation tracking', () => {
  const guides = resolve(import.meta.dirname, '../routes/docs/installation');

  it('reports every guide in the installation sidebar', () => {
    const installation = DOCS_NAV_SECTIONS.find((section) => section.id === 'installation');
    const sidebar = installation!.items
      .map((item) => item.href.replace('/docs/installation', '').replace(/^\//, ''))
      .filter(Boolean)
      .sort();
    expect(sidebar).toEqual([...INSTALL_PLATFORM_IDS].sort());
  });

  const markdownGuides = resolve(import.meta.dirname, '../content/docs/installation');

  // A marker whose value is not declared would be dropped by `allowedProps`, and the step would
  // silently never be counted.
  it('declares every step marker on the guides', () => {
    const steps = readdirSync(guides, { recursive: true, encoding: 'utf-8' })
      .filter((file) => file.endsWith('.svelte'))
      .flatMap((file) =>
        [...readFileSync(resolve(guides, file), 'utf-8').matchAll(/data-install-step="([^"]*)"/g)].map(
          (match) => match[1],
        ),
      );
    expect(steps.length).toBeGreaterThan(0);
    for (const step of steps) expect(INSTALL_STEP_IDS).toContain(step);
  });

  it('declares every step in the frontmatter of the markdown guides', () => {
    const steps = readdirSync(markdownGuides, { recursive: true, encoding: 'utf-8' })
      .filter((file) => file.endsWith('.svx'))
      .flatMap((file) => {
        const frontmatter = readFileSync(resolve(markdownGuides, file), 'utf-8').split(/^---$/m)[1] ?? '';
        const block = /^installSteps:\r?\n((?: {2}.+\r?\n?)+)/m.exec(frontmatter)?.[1] ?? '';
        return [...block.matchAll(/^ {2}[\w-]+: *"?([^"\r\n]+)"?\r?$/gm)].map((match) => match[1]);
      });
    expect(steps.length).toBeGreaterThan(0);
    for (const step of steps) expect(INSTALL_STEP_IDS).toContain(step);
  });

  it('keeps the step of a copied command', () => {
    expect(allowedProps('Docs Copy', { kind: 'code', step: '3' })).toEqual({ kind: 'code', step: '3' });
  });
});

describe('campaignUrl', () => {
  it('tags an off-site link as coming from the docs', () => {
    expect(campaignUrl('https://nocturne.run', 'managed-instances')).toBe(
      'https://nocturne.run/?utm_source=nocturne-docs&utm_medium=referral&utm_content=managed-instances',
    );
  });

  it('keeps a query the link already has', () => {
    expect(campaignUrl('https://example.com/a?b=1', 'x')).toBe(
      'https://example.com/a?b=1&utm_source=nocturne-docs&utm_medium=referral&utm_content=x',
    );
  });
});
