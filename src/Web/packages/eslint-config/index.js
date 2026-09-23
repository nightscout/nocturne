import prettier from "eslint-config-prettier";
import pluginsecurity from "eslint-plugin-security";
import { plugin as shadcn } from "@shadcn/lint";

import js from "@eslint/js";
import svelte from "eslint-plugin-svelte";
import globals from "globals";
import ts from "typescript-eslint";

// no-raw-colors reads `none` as an undeclared colour; fill-none and stroke-none paint nothing.
export const RAW_COLOR_ALLOW = ["fill-none", "stroke-none"];
const RAW_COLOR_HINT =
  "\"{{className}}\" is a raw colour. Use the token for what it means: glucose-* for a glucose range; insulin, carbs, iob-*, pred-* for a chart series; entry-* for a record kind's label or icon (entry-bolus, entry-carbs...); report-* for a report category's accent; status-* for a clinical state; success, warning, info, destructive for a UI outcome; demo for demo data; favorite for a pinned or favourite star. Otherwise a surface token (card, muted, foreground, border). All are declared in {{file}}.";

// Recommended warnings @nocturne/app has cleared; as errors they stay at none.
const SECURITY_ERRORS = [
  "security/detect-bidi-characters",
  "security/detect-buffer-noassert",
  "security/detect-child-process",
  "security/detect-disable-mustache-escape",
  "security/detect-eval-with-expression",
  "security/detect-new-buffer",
  "security/detect-no-csrf-before-method-override",
  "security/detect-non-literal-require",
  "security/detect-possible-timing-attacks",
  "security/detect-pseudoRandomBytes",
  "security/detect-non-literal-fs-filename",
  "security/detect-non-literal-regexp",
  "security/detect-unsafe-regex"
];

// Tests build partial mocks of framework and API types, which only an assertion can type.
const TEST_FILES = [
  "**/*.test.ts",
  "**/*.test.svelte",
  "**/*.spec.ts",
  "**/*.test-harness.svelte",
  "**/*.test-stub.svelte",
  "**/*-test-wrapper.svelte",
  "src/lib/test-stubs/**",
  "src/lib/test-fixtures/**",
  "e2e/**",
  "vitest.browser.setup.ts"
];

const BUILD_OUTPUT = ["build/", ".svelte-kit/", "dist/"];

// The files the shadcn plugin is registered for; a block naming a shadcn rule must stay within them.
export const SHADCN_FILES = ["**/*.svelte", "**/*.ts"];

// svelte-check types the bindings destructured from $props<T>() as any inside the
// component. Beside a top-level binding named after a rune, svelte-check reads that
// rune's other calls as store reads and leaves them untyped.
const COMPONENT_SCRIPT_SYNTAX = [
  {
    selector: 'CallExpression[callee.name="$props"][typeArguments]',
    message: "Type props with an interface: `let { a, b }: Props = $props()`. With $props<T>() svelte-check types the destructured props as any."
  },
  ...["state", "derived", "effect", "props"].flatMap((rune) =>
    [
      `VariableDeclaration > VariableDeclarator[id.name="${rune}"]`,
      `VariableDeclaration > VariableDeclarator > ObjectPattern > Property[value.name="${rune}"]`,
      `VariableDeclaration > VariableDeclarator > ObjectPattern > Property[value.left.name="${rune}"]`,
      `FunctionDeclaration[id.name="${rune}"]`,
      // svelte-check special-cases svelte/store's `derived` beside $derived.
      `ImportDeclaration:not([source.value="svelte/store"]) > [local.name="${rune}"]`
    ].map((binding) => ({
      selector: `SvelteScriptElement:has(Identifier[name="$${rune}"]) > ${binding}`,
      message: `Rename \`${rune}\`: while a local shares its name, svelte-check reads $${rune} as a store and types it as any.`
    }))
  )
];

// @shadcn/lint only sees classes on known components, so a raw control escapes the
// design system without a finding. Buttons are split out so a path can be let off
// raw buttons alone (see `rawButtonFiles`).
export const RAW_BUTTON_SYNTAX = [
  {
    selector: 'SvelteElement[kind="html"][name.name="button"]',
    message: "Use <Button> with a variant and size instead of a raw <button>. A pressed state is <Toggle>; a clickable card or row is <Item> with onclick or href; a single-select option card is <RadioGroup.Card>; an option in a popover list is <DropdownMenu.Item>, or <Button variant=\"menu\" size=\"menu\"> in a menu the editor floats itself (a bubble or slash menu); a chip's remove is <Badge onremove>; a remove that shows on hover is <Button reveal>."
  }
];

// Hidden inputs carry form state and render nothing.
export const RAW_MARKUP_SYNTAX = [
  {
    selector: 'SvelteElement[kind="html"][name.name="input"]:not(:has(SvelteAttribute[key.name="type"] > SvelteLiteral[value="hidden"]))',
    message: "Use <Input>, <Checkbox>, <Switch>, <RadioGroup> or <Slider> instead of a raw <input>. type=\"hidden\" is allowed."
  },
  {
    selector: 'SvelteElement[kind="html"][name.name="select"]',
    message: "Use <Select> instead of a raw <select>."
  },
  {
    selector: 'SvelteElement[kind="html"][name.name="textarea"]',
    message: "Use <Textarea> instead of a raw <textarea>."
  },
  {
    selector: "Literal[value=/hsl\\(var\\(--/]",
    message: "Theme variables are oklch; use var(--x) or color-mix(in oklch, var(--x) N%, transparent), not hsl(var(--x))."
  },
  {
    selector: "TemplateElement[value.raw=/hsl\\(var\\(--/]",
    message: "Theme variables are oklch; use var(--x) or color-mix(in oklch, var(--x) N%, transparent), not hsl(var(--x))."
  }
];

const typeRules = {
  rules: {
    "@typescript-eslint/consistent-type-assertions": ["error", { assertionStyle: "never" }],
    "@typescript-eslint/no-unused-vars": [
      "error",
      {
        argsIgnorePattern: "^_",
        varsIgnorePattern: "^_",
        caughtErrorsIgnorePattern: "^_"
      }
    ]
  }
};

// Tests read fixtures and sources under the repo and build patterns from their own
// identifiers; neither path nor pattern comes from a request, so every finding here
// was a false positive.
const testFileSecurity = {
  files: TEST_FILES,
  rules: {
    "security/detect-non-literal-fs-filename": "off",
    "security/detect-non-literal-regexp": "off",
    "security/detect-unsafe-regex": "off"
  }
};

const testFilePolicy = {
  files: TEST_FILES,
  rules: {
    "@typescript-eslint/consistent-type-assertions": [
      "error",
      { assertionStyle: "as", objectLiteralTypeAssertions: "allow" }
    ]
  }
};

const securityBase = [
  pluginsecurity.configs.recommended,
  // Near every finding is typed index access; the noise buries every other warning.
  { rules: { "security/detect-object-injection": "off" } }
];

/**
 * Downgrades a package's unpaid rules to warnings. Its `lint:ci` then gates on errors,
 * and `--max-warnings` holds the debt at its count. Each entry maps a rule to its finding
 * count. Lower the count and the cap as findings are fixed; at zero, delete the entry so
 * the rule is an error again.
 *
 * @param {Record<string, number>} counts
 * @returns {import("eslint").Linter.Config[]}
 */
export function debt(counts) {
  const rules = Object.keys(counts);
  const warn = (list) => Object.fromEntries(list.map((rule) => [rule, "warn"]));
  const shadcnRules = rules.filter((rule) => rule.startsWith("shadcn/"));
  const otherRules = rules.filter((rule) => !rule.startsWith("shadcn/"));
  return [
    ...(otherRules.length ? [{ rules: warn(otherRules) }] : []),
    ...(shadcnRules.length ? [{ files: SHADCN_FILES, rules: warn(shadcnRules) }] : [])
  ];
}

/**
 * Config for a TypeScript package with no Svelte components.
 *
 * @param {{ ignores?: string[] }} [options]
 */
export function nodeConfig({ ignores = [] } = {}) {
  return ts.config(
    js.configs.recommended,
    ...ts.configs.recommended,
    ...securityBase,
    prettier,
    { rules: Object.fromEntries(SECURITY_ERRORS.map((rule) => [rule, "error"])) },
    testFileSecurity,
    { languageOptions: { globals: { ...globals.node } } },
    { ignores: [...BUILD_OUTPUT, ...ignores] },
    typeRules,
    testFilePolicy
  );
}

/**
 * Config for a Svelte package that renders the design system.
 *
 * `componentDirs` are globs of the design system's own component sources. They own their
 * styling, so no-restyle, no-arbitrary-values and require-static-classes are off there,
 * and they may render raw controls. `noRestyle` holds shadcn/no-restyle's options, such
 * as a package's contracts. `shadcnSettings` is `settings.shadcn`, for a package whose
 * components.json does not name the component directory. `rawButtonFiles` are globs
 * where a raw <button> is allowed, for a surface drawn in a palette no Button variant
 * carries; raw inputs, selects and textareas stay errors there.
 *
 * @param {{
 *   componentDirs?: string[],
 *   noRestyle?: Record<string, unknown>,
 *   shadcnSettings?: Record<string, unknown>,
 *   rawButtonFiles?: string[],
 *   ignores?: string[]
 * }} [options]
 */
export function svelteConfig({
  componentDirs = [],
  noRestyle = { allow: ["layout"] },
  shadcnSettings,
  rawButtonFiles = [],
  ignores = []
} = {}) {
  return ts.config(
    js.configs.recommended,
    ...ts.configs.recommended,
    ...securityBase,
    ...svelte.configs["flat/recommended"],
    prettier,
    ...svelte.configs["flat/prettier"],
    {
      rules: Object.fromEntries(
        [...SECURITY_ERRORS, "svelte/no-at-debug-tags", "svelte/no-inspect"].map((rule) => [rule, "error"])
      )
    },
    testFileSecurity,
    {
      // ESLint's --report-unused-disable-directives never sees an HTML-comment directive
      // in Svelte markup; this rule owns those.
      files: ["**/*.svelte"],
      rules: { "svelte/comment-directive": ["error", { reportUnusedDisableDirectives: true }] }
    },
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
    { ignores: [...BUILD_OUTPUT, ...ignores] },
    {
      files: SHADCN_FILES,
      ...(shadcnSettings ? { settings: { shadcn: shadcnSettings } } : {}),
      rules: {
        "shadcn/no-restyle": ["error", noRestyle],
        "shadcn/no-raw-colors": ["error", { allow: RAW_COLOR_ALLOW, message: RAW_COLOR_HINT }],
        "shadcn/no-arbitrary-values": ["error", { allow: ["layout"], deny: ["text-[10px]", "text-[11px]"] }],
        "shadcn/no-inline-styles": "error",
        // The typography plugin matches these in its `prose` selectors and generates no
        // utility for them: `lead` styles a paragraph, `not-prose` opts a subtree out.
        "shadcn/no-unknown-classes": ["error", { allow: ["lead", "not-prose"] }],
        "shadcn/require-static-classes": "error"
      }
    },
    ...(componentDirs.length
      ? [
          {
            files: componentDirs,
            rules: {
              "shadcn/no-restyle": "off",
              "shadcn/no-arbitrary-values": "off",
              "shadcn/require-static-classes": "off"
            }
          }
        ]
      : []),
    typeRules,
    testFilePolicy,
    {
      // Flat config replaces no-restricted-syntax's options wholesale, so every .svelte
      // selector lives in this block or the two after it, which cover its ignores.
      files: ["**/*.svelte"],
      ignores: [...componentDirs, "**/*.test.svelte"],
      rules: {
        "no-restricted-syntax": ["error", ...COMPONENT_SCRIPT_SYNTAX, ...RAW_BUTTON_SYNTAX, ...RAW_MARKUP_SYNTAX]
      }
    },
    ...(rawButtonFiles.length
      ? [
          {
            files: rawButtonFiles,
            ignores: [...componentDirs, "**/*.test.svelte"],
            rules: {
              "no-restricted-syntax": ["error", ...COMPONENT_SCRIPT_SYNTAX, ...RAW_MARKUP_SYNTAX]
            }
          }
        ]
      : []),
    {
      files: [...componentDirs.map((dir) => `${dir.replace(/\/\*\*$/, "")}/**/*.svelte`), "**/*.test.svelte"],
      rules: {
        "no-restricted-syntax": ["error", ...COMPONENT_SCRIPT_SYNTAX]
      }
    }
  );
}
