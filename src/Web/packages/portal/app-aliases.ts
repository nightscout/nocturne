import { realpathSync } from 'node:fs';
import { resolve } from 'node:path';
import { normalizePath, type Plugin } from 'vite';

/**
 * The app's own aliases, for the app components the docs embed (the alert replay). `$lib` is the
 * portal's, and Vite's alias step rewrites it to the portal's lib before any plugin runs, so an
 * app file's `$lib/…` arrives here already pointing into the portal and is pointed back.
 * `$api-clients` is matched before `$api/`, which is its prefix.
 */
export function appAliases(): Plugin {
  const appSrc = normalizePath(realpathSync(resolve(import.meta.dirname, '../app/src')));
  const portalLib = normalizePath(realpathSync(resolve(import.meta.dirname, 'src/lib')));
  const prefixes: [string, string][] = [
    ['$api-clients', `${appSrc}/lib/api/generated/nocturne-api-client`],
    ['$api/', `${appSrc}/lib/api/`],
    ['$lib/', `${appSrc}/lib/`],
    [`${portalLib}/`, `${appSrc}/lib/`],
  ];
  return {
    name: 'app-aliases',
    enforce: 'pre',
    resolveId(source, importer, options) {
      if (!importer || !normalizePath(importer).startsWith(`${appSrc}/`)) return null;
      const id = normalizePath(source);
      const hit = prefixes.find(([from]) => id.startsWith(from));
      if (!hit) return null;
      return this.resolve(hit[1] + id.slice(hit[0].length), importer, { ...options, skipSelf: true });
    },
  };
}
