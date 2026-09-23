import { SHADCN_FILES, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({
    // Coach renders outside the design system and depends on no component library, so
    // its popover draws its own buttons from theme.css (coach-btn, coach-popover__close).
    rawButtonFiles: ["src/lib/popover/**"]
  }),
  {
    // Coach styles itself with its own plain CSS (theme.css, BEM classes and --coach-*
    // properties) and does not use Tailwind, so the Tailwind class rules have nothing to check.
    files: SHADCN_FILES,
    rules: {
      "shadcn/no-restyle": "off",
      "shadcn/no-raw-colors": "off",
      "shadcn/no-arbitrary-values": "off",
      "shadcn/no-unknown-classes": "off",
      "shadcn/require-static-classes": "off"
    }
  }
];
