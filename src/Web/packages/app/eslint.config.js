import prettier from "eslint-config-prettier";
import pluginsecurity from "eslint-plugin-security";
import { plugin as shadcn } from "@shadcn/lint";

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
    files: ["**/*.svelte", "**/*.svelte.ts"],

    languageOptions: {
	  parserOptions: {
	    parser: ts.parser
	  }
	},
    plugins: { shadcn }
  },
  {
    files: ["**/*.ts"],
    plugins: { shadcn }
  },
  {
    ignores: ["build/", ".svelte-kit/", "dist/"]
  },
  {
    files: ["**/*.svelte", "**/*.ts"],
    rules: {
      "shadcn/no-restyle": ["warn", {
        allow: ["layout"],
        // A later matching contract replaces an earlier one, so each restates layout.
        contracts: [
          {
            pattern: "^Card(Header|Content|Footer)$|^(Dialog|Sheet|AlertDialog)(Header|Footer)$|^(Popover|Collapsible)Content$",
            allow: ["layout", "spacing"]
          },
          { pattern: "^Table(Cell|Head)$", allow: ["layout", "spacing", "tabular-nums", "font-mono"] },
          { pattern: "^(Card|Dialog|Sheet|AlertDialog)Title$", allow: ["layout", "typography", "gap"] },
          { pattern: "^(Input|SelectTrigger)$", allow: ["layout", "tabular-nums", "font-mono"] },
          // A placeholder takes the radius of the content it stands in for.
          { pattern: "^Skeleton$", allow: ["layout", "rounded"] }
        ]
      }],
      "shadcn/no-raw-colors": "warn",
      "shadcn/no-arbitrary-values": ["warn", { allow: ["layout"] }],
      "shadcn/no-inline-styles": "warn",
      "shadcn/no-unknown-classes": "warn",
      "shadcn/require-static-classes": "warn"
    }
  },
  {
    files: ["src/lib/components/ui/**"],
    rules: {
      "shadcn/no-restyle": "off",
      "shadcn/no-arbitrary-values": "off",
      "shadcn/require-static-classes": "off"
    }
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
