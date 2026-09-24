import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { wuchale } from 'wuchale/vite';
import lingo from 'vite-plugin-lingo';
import { blogManifest } from '@nocturne/cms/blog/vite-plugin';
import { resolve, sep } from 'node:path';
import { cpSync, rmSync, existsSync, mkdirSync, realpathSync } from 'node:fs';
import { defineConfig, searchForWorkspaceRoot, type Plugin, type PluginOption } from 'vite';

/**
 * pnpm's global virtual store (`enableGlobalVirtualStore` in pnpm-workspace.yaml) links every
 * package out of the user-wide store, outside the workspace, so the dev server must be allowed to
 * serve from it or the client entry 403s and nothing hydrates. Found from where SvelteKit
 * actually resolves; empty under a conventional node_modules layout.
 */
function pnpmStoreRoots(): string[] {
  try {
    const kit = realpathSync(resolve(__dirname, 'node_modules/@sveltejs/kit'));
    const links = kit.lastIndexOf(`${sep}links${sep}`);
    return links === -1 ? [] : [kit.slice(0, links)];
  } catch {
    return [];
  }
}

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
        // Copied under its release download name: Vite's dev server refuses to serve any
        // `.env.*` file (server.fs.deny), so a `.env.example?raw` import 403s in the browser.
        const files = { 'docker-compose.yaml': 'docker-compose.yaml', '.env.example': 'default.env.example' };
        for (const [file, destName] of Object.entries(files)) {
          const src = resolve(srcDir, file);
          if (existsSync(src)) {
            cpSync(src, resolve(variantDest, destName));
          } else {
            this.warn(`release-assets: ${file} not found in deploy/${variant}; skipping`);
          }
        }
      }

      const oracleInstaller = resolve(deployRoot, 'oracle-cloud/oracle-cloud-install.sh');
      if (existsSync(oracleInstaller)) {
        mkdirSync(resolve(dest, 'oracle-cloud'), { recursive: true });
        cpSync(oracleInstaller, resolve(dest, 'oracle-cloud/oracle-cloud-install.sh'));
      } else {
        this.warn(`release-assets: oracle-cloud-install.sh not found at ${oracleInstaller}; skipping`);
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
    // The shared catalogue records references relative to packages/app, where translations:sync
    // runs, and wuchale reads references relative to its config's directory. Loaded from here,
    // no portal reference matched, so every portal string compiled to empty.
    wuchale({ configPath: '../app/wuchale.config.js' }),
    // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- vite-plugin-lingo resolves vite 8's Plugin type; this package builds on vite 6
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
      allow: [searchForWorkspaceRoot(process.cwd()), resolve(__dirname, 'src/lib/release'), ...pnpmStoreRoots()],
    },
  },
  ssr: {
    noExternal: ['@nocturne/app', '@nocturne/ui', '@nocturne/cms', 'lucide-svelte']
  }
});
