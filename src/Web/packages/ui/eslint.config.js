import { svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({
    // The design system owns its components' styling; callers are held to it elsewhere.
    componentDirs: ["src/lib/components/ui/**"],
    shadcnSettings: { ui: "$lib/components/ui" }
  })
];
