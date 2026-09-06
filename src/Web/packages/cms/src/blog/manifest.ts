import type { BlogPostMeta, BlogManifest } from './types.ts';

export function parseFrontmatter(content: string, filename: string): BlogPostMeta | null {
  // `\r?` matters: a post authored on Windows arrives with CRLF, and an `\n`-only
  // delimiter silently fails to match. The post then parses as having no frontmatter,
  // drops out of the manifest and is never built — with no error anywhere, so `check`
  // and `build` both pass and only the live 404 reveals it.
  const match = content.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!match) return null;

  // Normalised so a CRLF body cannot leave a stray \r inside a value. Scalars are
  // trimmed below, but array entries are split on ',' and would keep it.
  const yaml = match[1].replace(/\r\n/g, '\n');
  const meta: Record<string, unknown> = {};

  for (const line of yaml.split('\n')) {
    const colonIndex = line.indexOf(':');
    if (colonIndex === -1) continue;
    const key = line.slice(0, colonIndex).trim();
    let value: unknown = line.slice(colonIndex + 1).trim();

    // Parse arrays: [a, b, c]
    if (typeof value === 'string' && value.startsWith('[') && value.endsWith(']')) {
      value = value
        .slice(1, -1)
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
    }
    // Parse booleans
    else if (value === 'true') value = true;
    else if (value === 'false') value = false;

    meta[key] = value;
  }

  return {
    title: String(meta.title ?? ''),
    slug: String(meta.slug ?? filename.replace(/\.svx$/, '')),
    date: String(meta.date ?? ''),
    tags: Array.isArray(meta.tags) ? meta.tags : [],
    category: String(meta.category ?? ''),
    author: String(meta.author ?? ''),
    summary: String(meta.summary ?? ''),
    image: meta.image ? String(meta.image) : undefined,
    draft: typeof meta.draft === 'boolean' ? meta.draft : undefined,
    unlisted: typeof meta.unlisted === 'boolean' ? meta.unlisted : undefined,
  };
}

/**
 * The posts that belong in an index, feed or sitemap. Unlisted posts are
 * deliberately absent: they stay in the manifest so their page is still built
 * and reachable, but nothing should link to or announce them.
 */
export function listedPosts(posts: BlogPostMeta[]): BlogPostMeta[] {
  return posts.filter((post) => !post.unlisted);
}

export function buildManifest(posts: BlogPostMeta[], isProduction: boolean): BlogManifest {
  let filtered = isProduction ? posts.filter((p) => !p.draft) : posts;
  filtered = filtered.sort((a, b) => b.date.localeCompare(a.date));

  // Unlisted posts keep their place in `posts` so routing and prerendering can
  // reach them, but must not widen the tag and category lists the indexes use.
  const listed = listedPosts(filtered);
  const tags = [...new Set(listed.flatMap((p) => p.tags))].sort();
  const categories = [...new Set(listed.map((p) => p.category))].sort();

  return { posts: filtered, tags, categories };
}
