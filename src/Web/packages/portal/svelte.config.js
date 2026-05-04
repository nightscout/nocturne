import adapter from '@sveltejs/adapter-node';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { mdsvex } from 'mdsvex';
import { remarkVars } from '@nocturne/cms/remark/vars';

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: [vitePreprocess(), mdsvex({ extensions: ['.svx'], remarkPlugins: [remarkVars] })],
  extensions: ['.svelte', '.svx'],
  kit: {
    adapter: adapter({
      out: 'build',
      precompress: true,
    }),
    prerender: {
      handleHttpError: ({ path, message }) => {
        // /setup lives in the main app, not the portal — ignore during prerendering
        if (path === '/setup') return;
        throw new Error(message);
      },
    },
    experimental: {
      remoteFunctions: true,
    },
  },
  compilerOptions: {
    experimental: {
      async: true,
    },
  },
};

export default config;
