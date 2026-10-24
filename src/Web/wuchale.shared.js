// @ts-check
import { adapter as svelte, svelteKitDefaultHeuristic } from "@wuchale/svelte"
import { adapter as js } from 'wuchale/adapter-vanilla'
import { defineConfig, gemini, pofile } from "wuchale"
import supportedLocales from "./supportedLocales.json" with { type: 'json' };

// The monorepo's one wuchale config. It is loaded only as packages/app/wuchale.config.js:
// `pnpm run translations:sync` runs there, and the portal's vite plugin loads that file by
// path. Its globs and the catalogue's references are relative to packages/app. wuchale
// resolves references against the directory of the loading config, so loaded from another
// package, that package's strings match no reference and compile to empty.
//
// Every package's files are listed so one extraction writes the complete shared catalogue.
// An extraction that saw only one package would obsolete the other's messages.
const PACKAGES = ['app', 'portal', 'cms']

const globs = (patterns) =>
    PACKAGES.flatMap(pkg =>
        patterns.map(p => (pkg === 'app' ? `src/${p}` : `../${pkg}/src/${p}`)))

// An analytics event name (the portal's `track("Donate Click")`) is a key in the event schema,
// not display text. Translated, it would log a different event per locale.
const IGNORED_CALLS = new Set(['track'])

/** @type {import('wuchale').HeuristicFunc} */
const svelteHeuristic = msg =>
    msg.details.call && IGNORED_CALLS.has(msg.details.call) ? false : svelteKitDefaultHeuristic(msg)

/** Both adapters share one catalogue set (same storage key -> shared .po files). */
export function wuchaleConfig() {
    const storage = pofile({ location: '../../locales/{locale}.po' })

    return defineConfig({
        locales: supportedLocales,
        localesDir: '../../locales',
        adapters: {
            main: svelte({
                loader: 'sveltekit',
                heuristic: svelteHeuristic,
                sourceLocale: 'en',
                storage,
                files: globs(['**/*.svelte', '**/*.svelte.{js,ts}']),
            }),
            js: js({
                loader: 'vite',
                sourceLocale: 'en',
                storage,
                files: globs([
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
