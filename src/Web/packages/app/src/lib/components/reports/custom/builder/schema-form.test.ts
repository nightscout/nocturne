import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  choicesOf,
  controlFor,
  FORM_SHAPES,
  messagesAt,
  minuteToTime,
  shapeOf,
  timeToMinute,
  type ParameterSchema,
} from "./schema-form.js";
import { routeSpecErrors } from "./spec-errors.js";
import { everyShape } from "./schema-form.fixtures.js";

/** The API's pinned list of the shapes its catalogue emits (`ReportCatalogueTests.FormShapes`). */
function apiShapes(): string[] {
  const path = fileURLToPath(
    new URL("../../../../../../../../../../tests/Unit/Nocturne.API.Tests/Reports/ReportCatalogueTests.cs", import.meta.url),
  );
  const source = readFileSync(path, "utf8");
  const block = /FormShapes\s*=\s*\[([\s\S]*?)\];/.exec(source)?.[1] ?? "";
  return [...block.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}

describe("the parameter form's shapes", () => {
  it("are the shapes the API says its catalogue emits", () => {
    expect([...FORM_SHAPES].sort()).toEqual(apiShapes().sort());
  });

  it("each have a control", () => {
    for (const [shape, node] of Object.entries(everyShape)) {
      expect(shapeOf(node), shape).toBe(shape);
      expect(controlFor(node), shape).not.toBeNull();
    }
    expect(Object.keys(everyShape).sort()).toEqual([...FORM_SHAPES].sort());
  });

  it("finds no control for a shape it does not draw", () => {
    expect(controlFor({ type: "string", format: "uri" } as ParameterSchema)).not.toBeNull();
    expect(controlFor({ type: "array", items: { type: "integer" } })).toBeNull();
    expect(controlFor({ type: "string", "x-input": "colour" })).toBeNull();
  });

  it("reads an enum's words from the API, for a value or a list's items", () => {
    expect(choicesOf({ type: "string", enum: ["a", "b"], "x-enumLabels": ["Alpha", "Beta"] })).toEqual([
      { value: "a", label: "Alpha" },
      { value: "b", label: "Beta" },
    ]);
    expect(choicesOf({ type: "array", items: { type: "string", enum: ["Mon"], "x-enumLabels": ["Mon"] } })).toEqual([
      { value: "Mon", label: "Mon" },
    ]);
  });

  it("writes minutes after midnight as a time and reads them back", () => {
    expect(minuteToTime(450)).toBe("07:30");
    expect(timeToMinute("07:30")).toBe(450);
    expect(timeToMinute("24:00")).toBeNull();
    expect(timeToMinute("soon")).toBeNull();
  });
});

describe("the validator's messages", () => {
  it("go to the card and the field their path names", () => {
    const routed = routeSpecErrors([
      { path: "title", message: "Use 200 characters or fewer." },
      { path: "panels[1].dataset.name", message: "'x' is not a dataset." },
      { path: "panels[1].dataset.cohorts[0].dayOfWeek[2]", message: "Send one of: Mon, Tue." },
      { path: "panels[1].bindings.y", message: "Send exactly one field." },
      { path: "panels[1].range", message: "Shorten it." },
      { path: "panels[0].primitive", message: "Cannot be drawn so." },
      { path: "panels[0].id", message: "Use only letters." },
    ]);

    expect(routed.report.map((e) => e.path)).toEqual(["title"]);
    const second = routed.panels.get(1)!;
    expect(second.dataset).toEqual(["'x' is not a dataset."]);
    expect(second.parameters).toEqual([{ path: "cohorts[0].dayOfWeek[2]", message: "Send one of: Mon, Tue." }]);
    expect(second.bindings).toEqual([{ path: "y", message: "Send exactly one field." }]);
    expect(second.range).toEqual(["Shorten it."]);
    expect(routed.panels.get(0)!.primitive).toEqual(["Cannot be drawn so."]);
    expect(routed.panels.get(0)!.panel).toEqual(["Use only letters."]);
  });

  it("show at a leaf field with its items, and at a group only when they name the group", () => {
    const messages = [
      { path: "cohorts[0].dayOfWeek[2]", message: "Send one of: Mon, Tue." },
      { path: "cohorts[0]", message: "Check this group." },
    ];
    expect(messagesAt(messages, "cohorts[0].dayOfWeek", true)).toEqual(["Send one of: Mon, Tue."]);
    expect(messagesAt(messages, "cohorts[0]", false)).toEqual(["Check this group."]);
    expect(messagesAt(messages, "cohorts", false)).toEqual([]);
  });
});
