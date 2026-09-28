import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { mdsvex } from 'mdsvex';
import rehypeSlug from 'rehype-slug';
import { remarkVars } from '@nocturne/cms/remark/vars';

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: [
    vitePreprocess(),
    mdsvex({ extensions: ['.svx'], remarkPlugins: [remarkVars], rehypePlugins: [rehypeSlug] }),
  ],
  extensions: ['.svelte', '.svx'],
  kit: {
    adapter: adapter({
      pages: 'build',
      assets: 'build',
      fallback: '404.html',
    }),
    paths: {
      base: process.env.BASE_PATH ?? '',
    },
    // Types for the app modules the alerts docs embed, which use the app's own aliases. TypeScript
    // tries each `$lib/*` location in turn, so the app's lib only answers what the portal's lacks;
    // the one path both hold, `$lib/utils`, exports the same `cn` the app files take from it.
    // At runtime app-aliases.ts resolves these by importer instead.
    typescript: {
      config(config) {
        const paths = config.compilerOptions.paths;
        paths['$lib/*'] = [...paths['$lib/*'], '../../app/src/lib/*'];
        paths['$api/*'] = ['../../app/src/lib/api/*'];
        paths['$api-clients'] = ['../../app/src/lib/api/generated/nocturne-api-client'];
      },
    },
    prerender: {
      handleHttpError: ({ path, message }) => {
        if (path === '/setup') return;
        throw new Error(message);
      },
    },
  },
  compilerOptions: {
    experimental: {
      async: true,
    },
  },
};

export default config;
