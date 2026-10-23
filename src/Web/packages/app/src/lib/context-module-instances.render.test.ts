import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import Harness from "./ContextProviderHarness.test.svelte";

type Module = Record<string, (...args: never[]) => unknown>;

interface Case {
  name: string;
  load: (instance: string) => Promise<Module>;
  provide: (m: Module) => unknown;
  read: (m: Module) => unknown;
}

const cases: Case[] = [
  {
    name: "date params",
    load: (instance) =>
      import(/* @vite-ignore */ `$lib/hooks/date-params.svelte?instance=${instance}`) as Promise<Module>,
    provide: (m) =>
      (m.setDateParamsContext as (p: unknown) => unknown)({ marker: true }),
    read: (m) => m.getDateParamsContext(),
  },
  {
    name: "resource context",
    load: (instance) =>
      import(/* @vite-ignore */ `$lib/hooks/resource-context.svelte?instance=${instance}`) as Promise<Module>,
    provide: (m) => m.createResourceContext(),
    read: (m) => m.getResourceContext(),
  },
  {
    name: "report print",
    load: (instance) =>
      import(/* @vite-ignore */ `$lib/components/reports/print/report-print.svelte?instance=${instance}`) as Promise<Module>,
    provide: (m) => m.createReportPrintContext(),
    read: (m) => m.getReportPrintContext(),
  },
];

describe("context keys across module instances", () => {
  it.each(cases)(
    "reads the $name through a second copy of its module",
    async (c) => {
      const provider = await c.load("a");
      const reader = await c.load("b");
      expect(reader).not.toBe(provider);

      const { body } = await render(Harness, {
        props: { provide: () => c.provide(provider), read: () => c.read(reader) },
      });

      expect(body).toContain('data-found="yes"');
    }
  );
});
