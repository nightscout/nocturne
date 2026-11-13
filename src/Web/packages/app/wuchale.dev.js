// @ts-check
import { basename, dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

/** Read by wuchaleConfig in wuchale.shared.js. */
export const DEV_LOCALES_DIR_ENV = 'NOCTURNE_WUCHALE_LOCALES_DIR'

// The directory wuchale resolves localesDir against: the config is always loaded from here.
const wuchaleRoot = dirname(fileURLToPath(import.meta.url))
const sharedLocalesDir = resolve(wuchaleRoot, '../../locales')

const slashes = (/** @type {string} */ p) => p.replaceAll('\\', '/')

/**
 * Gives a dev server a compiled catalogue that no other process writes.
 *
 * wuchale numbers messages in memory, per process, and writes the compiled catalogue to
 * `<localesDir>/.wuchale/`. A dev server keeps its numbering for its whole life and ignores
 * changes to that file, so any other process that loads the plugin (svelte-check, the
 * `wuchale` CLI behind lint:copy and translations:sync, the render tests, a build) rewrote
 * the file under it with its own numbering, and every page then showed other messages'
 * strings until the server restarted.
 *
 * Under `vite dev` this moves wuchale's localesDir to `locales/.dev/<pkg>` and resolves the
 * app's imports of the loaders and `data.js` in `locales/` to their copies there. The `.po`
 * files stay shared: their path is set apart from localesDir, and the dev server reloads them
 * when they change without renumbering.
 *
 * Must come before `wuchale()`, which reads the config when Vite resolves it.
 *
 * @param {string} pkg the package running the dev server; each one gets its own directory
 * @returns {import('vite').Plugin}
 */
export function privateDevCatalogue(pkg) {
    const privateDir = join(sharedLocalesDir, '.dev', pkg)
    let active = false

    return {
        name: 'nocturne:private-dev-catalogue',
        enforce: 'pre',
        config(_, { command, isPreview }) {
            active = command === 'serve' && !isPreview
            if (active) {
                process.env[DEV_LOCALES_DIR_ENV] = slashes(relative(wuchaleRoot, privateDir))
            }
        },
        async resolveId(source, importer, options) {
            if (!active || !importer || !source.includes('locales/')) {
                return null
            }
            const resolved = await this.resolve(source, importer, { ...options, skipSelf: true })
            if (!resolved || slashes(dirname(resolved.id)) !== slashes(sharedLocalesDir)) {
                return null
            }
            return slashes(join(privateDir, basename(resolved.id)))
        },
    }
}
