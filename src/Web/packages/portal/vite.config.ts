import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
// WUCHALE-DISABLED: wuchale temporarily disabled
// import { wuchale } from '@wuchale/vite-plugin';
import lingo from 'vite-plugin-lingo';
import { blogManifest } from '@nocturne/cms/blog/vite-plugin';
import { resolve } from 'node:path';
import { defineConfig, type PluginOption } from 'vite';


export default defineConfig({
  plugins: [tailwindcss(),
    lingo({
      route: '/_translations',  // Route where editor UI is served
      localesDir: '../../locales',  // Path to .po files
    }) as PluginOption, blogManifest({ contentDir: resolve(__dirname, 'src/content/blog') }), sveltekit()],
  server: {
    host: "0.0.0.0",
    port: parseInt(process.env.PORT || "5173", 10),
    strictPort: true,
  },
  ssr: {
    noExternal: ['@nocturne/app', '@nocturne/ui', '@nocturne/cms', 'lucide-svelte']
  }
});
