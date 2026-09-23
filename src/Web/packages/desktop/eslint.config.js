import { svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({
    shadcnSettings: { ui: "@nocturne/ui/ui" },
    // The floating clock's hover controls sit on a translucent black pill over whatever is
    // behind the window, a palette no Button variant carries.
    rawButtonFiles: ["src/routes/float/**/*.svelte"],
    noRestyle: {
      allow: ["layout"],
      // The same allowances @nocturne/app makes for these components.
      contracts: [
        { pattern: "^Card(Header|Content|Footer)$", allow: ["layout", "spacing", "border-t", "border-b"] },
        { pattern: "^CardTitle$", allow: ["layout", "typography", "gap"] },
        { pattern: "^Textarea$", allow: ["layout", "font-mono"] }
      ]
    }
  })
];
