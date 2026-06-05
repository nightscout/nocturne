import prettier from "eslint-config-prettier";
import pluginsecurity from "eslint-plugin-security";

import js from '@eslint/js';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';

import noImperativeRemoteQuery from "./tools/eslint/no-imperative-remote-query.js";

export default ts.config(
  js.configs.recommended,
  ...ts.configs.recommended,
  pluginsecurity.configs.recommended,
  ...svelte.configs["flat/recommended"],
  prettier,
  ...svelte.configs['flat/prettier'],
  {
    languageOptions: {
	  globals: {
	    ...globals.browser,
	    ...globals.node
	  }
	}
  },
  {
    files: ["**/*.svelte"],

    languageOptions: {
	  parserOptions: {
	    parser: ts.parser
	  }
	}
  },
  {
    ignores: ["build/", ".svelte-kit/", "dist/"]
  },
  {
    rules: {
      "@typescript-eslint/consistent-type-assertions": [
        "error",
        {
          assertionStyle: "never"
        }
      ]
    }
  },
  {
    // Guard against the year-overview/alerts-polling regression class: a remote
    // query() awaited or `.then()`-chained imperatively (outside a reactive
    // context) throws "not created in a reactive context ... Use .run()".
    files: ["**/*.svelte", "**/*.svelte.ts"],
    plugins: {
      nocturne: {
        rules: { "no-imperative-remote-query": noImperativeRemoteQuery }
      }
    },
    rules: {
      "nocturne/no-imperative-remote-query": "warn"
    }
  }
);
