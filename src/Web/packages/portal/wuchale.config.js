// @ts-check
import { adapter as svelte } from "@wuchale/svelte"
import { adapter as js } from 'wuchale/adapter-vanilla'
import { defineConfig, gemini } from "wuchale"
import supportedLocales from "../../supportedLocales.json" with { type: 'json' };
const localesDir = '../../locales'
export default defineConfig({
    locales: supportedLocales,
    adapters: {
        main: svelte({ loader: 'sveltekit', localesDir: localesDir, sourceLocale: 'en' }),
        js: js({
            loader: 'vite',
            localesDir: localesDir,
            files: [
                'src/**/+{page,layout}.{js,ts}',
                'src/**/+{page,layout}.server.{js,ts}',


                '../app/src/**/+{page,layout}.{js,ts}',
                '../app/src/**/+{page,layout}.server.{js,ts}',
            ],
        })
    },
    ai: gemini({
        model: 'gemini-3-flash-preview',
        batchSize: 40,
        parallel: 5,
        think: true, // default: false
  }),
})