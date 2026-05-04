import type { PageLoad } from './$types';
import manifest from 'virtual:blog-manifest';

export const prerender = true;

export function entries() {
  return manifest.posts.map((post: { slug: string }) => ({ slug: post.slug }));
}

export const load: PageLoad = async ({ params }) => {
  const modules = import.meta.glob<{ default: typeof import('svelte').SvelteComponent; metadata: Record<string, unknown> }>(
    '../../../content/blog/*.svx',
  );

  const path = Object.keys(modules).find((p) => p.endsWith(`/${params.slug}.svx`));
  if (!path) throw new Error(`Post not found: ${params.slug}`);

  const mod = await modules[path]();
  return {
    content: mod.default,
    meta: manifest.posts.find((p: { slug: string }) => p.slug === params.slug),
  };
};
