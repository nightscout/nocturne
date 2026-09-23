import prettier from "eslint-config-prettier";
import pluginsecurity from "eslint-plugin-security";
import { plugin as shadcn } from "@shadcn/lint";

import js from '@eslint/js';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';

import noImperativeRemoteQuery from "./tools/eslint/no-imperative-remote-query.js";

// {{variants}} and {{sizes}} come out empty for Button: the lint does not follow
// button.svelte to button-variants.ts. Keep these names in step with that file.
const BUTTON_VARIANT_HINT =
  "\"{{className}}\" is not allowed on <Button>: it owns its {{category}}. Use a variant: ghost-muted (quiet secondary action), ghost-destructive (quiet remove), outline-destructive (bordered remove), dashed (add-item placeholder), or default, secondary, outline, ghost, link, destructive.";

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
          {
            pattern: "^(Input|SelectTrigger)$",
            allow: ["layout", "tabular-nums", "font-mono"],
            message: {
              spacing: 'Use size="xs" or "sm" on <{{component}}>; each matches the same size on Input, SelectTrigger and ToggleGroup.',
              typography: 'Use size="xs" or "sm" on <{{component}}>; Input also has variant="code" for device codes.',
              color: "Mark an invalid field with aria-invalid, which <{{component}}> already styles."
            }
          },
          { pattern: "^Textarea$", allow: ["layout", "font-mono"] },
          {
            pattern: "^Label$",
            allow: ["layout"],
            message: {
              typography: 'Use <Label size="sm"> or size="lg", or variant="option" for a checkbox, radio or switch choice.',
              color: 'Use <Label variant="muted"> for a secondary caption.'
            }
          },
          {
            pattern: "^ToggleGroup(Item)?$",
            allow: ["layout"],
            message: 'Set it on <ToggleGroup>: size="xs" for a compact row, variant="segmented" for a view switcher, spacing={1} for separate chips.'
          },
          // A placeholder takes the radius of the content it stands in for.
          { pattern: "^Skeleton$", allow: ["layout", "rounded"] },
          {
            pattern: "^Button$",
            allow: ["layout"],
            message: {
              color: BUTTON_VARIANT_HINT,
              shape: BUTTON_VARIANT_HINT,
              effects: BUTTON_VARIANT_HINT,
              motion: BUTTON_VARIANT_HINT,
              spacing: "\"{{className}}\" is not allowed on <Button>: it owns its padding and gap. Use a size: xs (h-6, text-xs), sm, default, lg, icon-xs (size-6), icon-sm (size-8), icon (size-9), or inline (no padding, a link in running text). For space around it, use margin or gap on the parent.",
              typography: "\"{{className}}\" is not allowed on <Button>: it owns its type. size=\"xs\" gives text-xs; every other size is text-sm font-medium."
            }
          }
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
