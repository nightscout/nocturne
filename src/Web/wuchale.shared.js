// @ts-check
import { adapter as svelte } from "@wuchale/svelte"
import { adapter as js } from 'wuchale/adapter-vanilla'
import { defineConfig, gemini, pofile } from "wuchale"
import supportedLocales from "./supportedLocales.json" with { type: 'json' };

// Every package's files are listed in every package's config so a single
// extraction run (from any of them) produces the complete shared catalog. An
// extraction that sees only one package obsoletes the other's messages, which
// compiles to an empty string in a production build. Derived from one list
// here so a new package or glob cannot be added to one config and forgotten
// in the other; the order is package-independent so extraction from any of
// them writes the same catalog. Run it through `pnpm run translations:sync`.
const PACKAGES = ['app', 'portal']

/** Resolves a per-package source glob from the config owner's directory. */
const globs = (owner, patterns) =>
    PACKAGES.flatMap(pkg =>
        patterns.map(p => (pkg === owner ? `src/${p}` : `../${pkg}/src/${p}`)))

/**
 * The wuchale config for one package of the monorepo. Both adapters share one
 * catalog set (same storage key -> shared .po files).
 *
 * @param {'app' | 'portal'} owner
 */
export function wuchaleConfig(owner) {
    const storage = pofile({ location: '../../locales/{locale}.po' })

    return defineConfig({
        locales: supportedLocales,
        localesDir: '../../locales',
        adapters: {
            main: svelte({
                loader: 'sveltekit',
                sourceLocale: 'en',
                storage,
                files: globs(owner, ['**/*.svelte', '**/*.svelte.{js,ts}']),
            }),
            js: js({
                loader: 'vite',
                sourceLocale: 'en',
                storage,
                files: globs(owner, [
                    '**/+{page,layout}.{js,ts}',
                    '**/+{page,layout}.server.{js,ts}',
                ]),
            }),
        },
        ai: gemini({
            model: 'gemini-3-flash-preview',
            batchSize: 40,
            parallel: 5,
            think: true, // default: false
        }),
    })
}
