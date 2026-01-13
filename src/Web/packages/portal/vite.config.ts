
import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { wuchale } from '@wuchale/vite-plugin';
import lingo from 'vite-plugin-lingo';
import { defineConfig } from 'vite';
import fs from 'fs';


export default defineConfig({
  plugins: [tailwindcss(), wuchale(),
    lingo({
      route: '/_translations',  // Route where editor UI is served
      localesDir: '../../locales',  // Path to .po files
    }), sveltekit()],
  server: {
    https: process.env.SSL_CRT_FILE && process.env.SSL_KEY_FILE ? {
      cert: fs.readFileSync(process.env.SSL_CRT_FILE),
      key: fs.readFileSync(process.env.SSL_KEY_FILE),
    } : undefined,
    host: "0.0.0.0",
    port: parseInt(process.env.PORT || "5173", 10),
    strictPort: true,
    proxy: {
      '/api': {
        target: process.env.VITE_PORTAL_API_URL,
        secure: false,
        changeOrigin: true
      }
    }
  },
  ssr: {
    noExternal: ['@nocturne/app', 'lucide-svelte']
  }
});
