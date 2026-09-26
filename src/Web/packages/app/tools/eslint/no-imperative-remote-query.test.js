import { RuleTester } from "eslint";
import { afterAll, describe, it } from "vitest";
import { createRule } from "./no-imperative-remote-query.js";

RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.afterAll = afterAll;

const rule = createRule(new Set(["getThing"]));
const imports = 'import { getThing } from "$api/thing.remote";\n';

new RuleTester().run("no-imperative-remote-query", rule, {
  valid: [
    imports + "$effect(() => { getThing({}).then((t) => use(t)); });",
    imports + "$effect.pre(() => { getThing({}).then(use); });",
    imports + "const t = $derived.by(() => { getThing({}).then(use); return 1; });",
    imports + "$effect(async () => { const t = await getThing({}); use(t); });",
    imports + "async function load() { await getThing({}).run(); }",
  ],
  invalid: [
    { code: imports + "async function load() { await getThing({}); }", errors: [{ messageId: "useRun" }] },
    { code: imports + "await getThing({});", errors: [{ messageId: "useRun" }] },
    {
      code: imports + "$effect(async () => { await ready(); getThing({}).then(use); });",
      errors: [{ messageId: "useRun" }],
    },
    {
      code: imports + "$effect(() => { setTimeout(() => getThing({}).then(use)); });",
      errors: [{ messageId: "useRun" }],
    },
  ],
});
