import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
// WUCHALE-DISABLED: wuchale temporarily disabled
// import { wuchale } from '@wuchale/vite-plugin';
import lingo from 'vite-plugin-lingo';
import { blogManifest } from '@nocturne/cms/blog/vite-plugin';
import { resolve } from 'node:path';
import { cpSync, rmSync, existsSync, mkdirSync } from 'node:fs';
import { defineConfig, type Plugin, type PluginOption } from 'vite';

function sharedLogos(): Plugin {
  return {
    name: 'shared-logos',
    buildStart() {
      // Runs once at dev-server startup and at the start of every build.
      // Adding a logo to packages/app/static/logos during a running dev session
      // requires a server restart to be reflected here.
      const src = resolve(__dirname, '../app/static/logos');
      if (!existsSync(src)) {
        this.warn(`shared-logos: source not found at ${src}; skipping logo copy`);
        return;
      }
      const dest = resolve(__dirname, 'static/logos');
      if (existsSync(dest)) {
        rmSync(dest, { recursive: true, force: true });
      }
      cpSync(src, dest, { recursive: true });
    }
  };
}

function sharedFonts(): Plugin {
  return {
    name: 'shared-fonts',
    buildStart() {
      const src = resolve(__dirname, '../app/static/fonts');
      if (!existsSync(src)) {
        this.warn(`shared-fonts: source not found at ${src}; skipping font copy`);
        return;
      }
      const dest = resolve(__dirname, 'static/fonts');
      if (existsSync(dest)) {
        rmSync(dest, { recursive: true, force: true });
      }
      cpSync(src, dest, { recursive: true });
    }
  };
}

function releaseAssets(): Plugin {
  return {
    name: 'release-assets',
    buildStart() {
      const deployRoot = resolve(__dirname, '../../../../deploy');
      const dest = resolve(__dirname, 'src/lib/release');

      if (existsSync(dest)) {
        rmSync(dest, { recursive: true, force: true });
      }
      mkdirSync(dest, { recursive: true });

      const variants = ['portainer', 'docker-compose'] as const;
      for (const variant of variants) {
        const srcDir = resolve(deployRoot, variant);
        if (!existsSync(srcDir)) {
          this.warn(`release-assets: deploy/${variant} not found at ${srcDir}; skipping`);
          continue;
        }
        const variantDest = resolve(dest, variant);
        mkdirSync(variantDest, { recursive: true });
        for (const file of ['docker-compose.yaml', '.env.example']) {
          const src = resolve(srcDir, file);
          if (existsSync(src)) {
            cpSync(src, resolve(variantDest, file));
          } else {
            this.warn(`release-assets: ${file} not found in deploy/${variant}; skipping`);
          }
        }
      }

      // Copy standalone docs assets (e.g. BYO Postgres bootstrap script)
      const docsRoot = resolve(__dirname, '../../../../docs');
      const bootstrapSql = resolve(docsRoot, 'postgres/bootstrap-roles.sql');
      if (existsSync(bootstrapSql)) {
        cpSync(bootstrapSql, resolve(dest, 'bootstrap-roles.sql'));
      } else {
        this.warn(`release-assets: bootstrap-roles.sql not found at ${bootstrapSql}; skipping`);
      }
    }
  };
}

export default defineConfig({
  plugins: [
    sharedLogos(),
    sharedFonts(),
    releaseAssets(),
    tailwindcss(),
    lingo({
      route: '/_translations',
      localesDir: '../../locales',
    }) as PluginOption,
    blogManifest({ contentDir: resolve(__dirname, 'src/content/blog') }),
    sveltekit()
  ],
  server: {
    host: "0.0.0.0",
    port: parseInt(process.env.PORT || "5173", 10),
    strictPort: true,
    fs: {
      allow: [resolve(__dirname, 'src/lib/release')],
    },
  },
  ssr: {
    noExternal: ['@nocturne/app', '@nocturne/ui', '@nocturne/cms', 'lucide-svelte']
  }
});
