import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const prerender = true;

// Every standard docs page is a markdown (.svx) file under content/docs, named to mirror its
// URL: content/docs/bots/discord.svx -> /docs/bots/discord, content/docs/bots/index.svx ->
// /docs/bots, content/docs/sharing.svx -> /docs/sharing.
const pages = import.meta.glob<{ default: typeof import('svelte').SvelteComponent }>(
  '../../../content/docs/**/*.svx',
);

/** The URL path (under /docs) that a content file serves. */
function slugFor(path: string): string {
  const rel = path.replace(/^.*\/content\/docs\//, '').replace(/\.svx$/, '');
  return rel.endsWith('/index') ? rel.slice(0, -'/index'.length) : rel;
}

// Slugs served by dedicated routes with bespoke layouts (the docs landing page and the
// getting-started page), so the catch-all leaves them alone.
const reserved = new Set(['', 'getting-started']);

export function entries() {
  return Object.keys(pages)
    .map(slugFor)
    .filter((slug) => !reserved.has(slug))
    .map((slug) => ({ slug }));
}

export const load: PageLoad = async ({ params }) => {
  const match = Object.entries(pages).find(([path]) => slugFor(path) === params.slug);
  if (!match) throw error(404, `Doc not found: ${params.slug}`);

  const mod = await match[1]();
  return { content: mod.default };
};
