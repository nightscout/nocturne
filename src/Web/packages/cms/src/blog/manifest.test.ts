import { describe, it, expect } from 'vitest';
import { parseFrontmatter, buildManifest } from './manifest.ts';
import type { BlogPostMeta } from './types.ts';

describe('parseFrontmatter', () => {
  it('parses valid frontmatter from svx content', () => {
    const content = `---
title: Test Post
slug: test-post
date: 2026-04-12
tags: [announcement, release]
category: news
author: Rhys
summary: A test post
---

# Content here`;

    const meta = parseFrontmatter(content, 'test-post.svx');
    expect(meta).toEqual({
      title: 'Test Post',
      slug: 'test-post',
      date: '2026-04-12',
      tags: ['announcement', 'release'],
      category: 'news',
      author: 'Rhys',
      summary: 'A test post',
      image: undefined,
      draft: undefined,
    });
  });

  it('returns null for content without frontmatter', () => {
    const meta = parseFrontmatter('# Just a heading', 'no-front.svx');
    expect(meta).toBeNull();
  });

  // A post authored on Windows arrives with CRLF. The delimiter pattern only accepted a
  // bare newline, so such a post parsed as having no frontmatter, dropped out of the
  // manifest and was never built — silently, with both check and build still passing, so
  // only the live 404 revealed it.
  it('parses frontmatter with CRLF line endings', () => {
    const content = [
      '---',
      'title: Windows Post',
      'slug: windows-post',
      'date: 2026-04-12',
      'tags: [announcement, release]',
      'category: news',
      'author: Rhys',
      'summary: Authored with CRLF',
      'draft: true',
      '---',
      '',
      '# Content here',
    ].join('\r\n');

    const meta = parseFrontmatter(content, 'windows-post.svx');

    expect(meta).toEqual({
      title: 'Windows Post',
      slug: 'windows-post',
      date: '2026-04-12',
      tags: ['announcement', 'release'],
      category: 'news',
      author: 'Rhys',
      summary: 'Authored with CRLF',
      image: undefined,
      draft: true,
    });
  });

  // Covers the parts a trailing trim() alone would not: the last entry of an inline array,
  // and a boolean matched by exact value.
  it('leaves no carriage returns in values parsed from CRLF content', () => {
    const content = [
      '---',
      'title: Trailing CR',
      'slug: trailing-cr',
      'date: 2026-04-12',
      'tags: [one, two]',
      'category: news',
      'author: Rhys',
      'summary: No stray carriage returns',
      'draft: true',
      '---',
      '# Body',
    ].join('\r\n');

    const meta = parseFrontmatter(content, 'trailing-cr.svx');

    expect(meta?.tags).toEqual(['one', 'two']);
    expect(meta?.draft).toBe(true);
    expect(JSON.stringify(meta)).not.toContain('\\r');
  });

  it('handles optional image and draft fields', () => {
    const content = `---
title: Draft Post
slug: draft-post
date: 2026-04-12
tags: []
category: dev
author: Rhys
summary: A draft
image: /blog/draft.png
draft: true
---`;

    const meta = parseFrontmatter(content, 'draft-post.svx');
    expect(meta?.image).toBe('/blog/draft.png');
    expect(meta?.draft).toBe(true);
  });
});

describe('buildManifest', () => {
  it('sorts posts by date descending', () => {
    const posts = [
      makeMeta({ slug: 'old', date: '2026-01-01' }),
      makeMeta({ slug: 'new', date: '2026-04-12' }),
      makeMeta({ slug: 'mid', date: '2026-02-15' }),
    ];
    const manifest = buildManifest(posts, false);
    expect(manifest.posts.map((p) => p.slug)).toEqual(['new', 'mid', 'old']);
  });

  it('excludes drafts in production mode', () => {
    const posts = [
      makeMeta({ slug: 'published', draft: false }),
      makeMeta({ slug: 'draft', draft: true }),
    ];
    const manifest = buildManifest(posts, true);
    expect(manifest.posts).toHaveLength(1);
    expect(manifest.posts[0].slug).toBe('published');
  });

  it('includes drafts in dev mode', () => {
    const posts = [
      makeMeta({ slug: 'published', draft: false }),
      makeMeta({ slug: 'draft', draft: true }),
    ];
    const manifest = buildManifest(posts, false);
    expect(manifest.posts).toHaveLength(2);
  });

  it('collects unique tags and categories', () => {
    const posts = [
      makeMeta({ tags: ['a', 'b'], category: 'news' }),
      makeMeta({ tags: ['b', 'c'], category: 'dev' }),
    ];
    const manifest = buildManifest(posts, false);
    expect(manifest.tags).toEqual(['a', 'b', 'c']);
    expect(manifest.categories).toEqual(['dev', 'news']);
  });
});

function makeMeta(overrides: Partial<BlogPostMeta> = {}): BlogPostMeta {
  return {
    title: 'Test',
    slug: 'test',
    date: '2026-01-01',
    tags: [],
    category: 'general',
    author: 'Test',
    summary: 'Test summary',
    ...overrides,
  };
}
