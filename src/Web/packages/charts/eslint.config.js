import { svelteConfig } from "@nocturne/eslint-config";

// The package renders from props alone, so a host with no SvelteKit app can draw it: no
// SvelteKit modules, no app modules, no remote functions or stores, no translation runtime.
const APP_ONLY = "@nocturne/charts renders from props: take it as a prop, a label or a snippet.";

export default [
  // No stylesheet here imports Tailwind: the consumer supplies it and the theme, so
  // no-raw-colors flags palette colours but cannot check token names.
  ...svelteConfig({
    restrictedImports: {
      paths: [{ name: "svelte/store", message: APP_ONLY }],
      patterns: [
        { group: ["$app", "$app/*", "$lib", "$lib/*", "#lib", "#lib/*", "#routes/*"], message: APP_ONLY },
        { group: ["@sveltejs/kit", "@sveltejs/kit/*"], message: APP_ONLY },
        { group: ["@nocturne/app", "@nocturne/app/*"], message: APP_ONLY },
        { group: ["wuchale", "wuchale/*", "@wuchale/*"], message: APP_ONLY },
        { group: ["**/*.remote", "**/*.remote.js", "**/*.remote.ts"], message: APP_ONLY },
        { group: ["**/stores/*", "**/*-store", "**/*-store.*"], message: APP_ONLY }
      ]
    }
  }),
  {
    // The static charts paint on a canvas with no stylesheet: layerchart's canvas marks take a
    // label's font and a line's dashes only through `style`, and no class reaches them.
    files: ["src/report/static/**/*.svelte", "src/static/**/*.svelte"],
    rules: { "shadcn/no-inline-styles": "off" }
  }
];
