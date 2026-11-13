import { existsSync, readFileSync } from "node:fs";
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
import { messagesUnder, routeSpecErrors } from "./spec-errors.js";
import { everyShape } from "./schema-form.fixtures.js";

/** The API test that pins the shapes its catalogue emits (`ReportCatalogueTests.FormShapes`); absent in a checkout of the web app alone. */
const API_TEST = fileURLToPath(
  new URL("../../../../../../../../../../tests/Unit/Nocturne.API.Tests/Reports/ReportCatalogueTests.cs", import.meta.url),
);

function apiShapes(): string[] {
  const block = /FormShapes\s*=\s*\[([\s\S]*?)\];/.exec(readFileSync(API_TEST, "utf8"))?.[1] ?? "";
  return [...block.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}

describe("the parameter form's shapes", () => {
  it("are the shapes the API says its catalogue emits", (ctx) => {
    if (!existsSync(API_TEST)) ctx.skip("The API tests are not in this checkout.");
    expect([...FORM_SHAPES].sort()).toEqual(apiShapes().sort());
  });

  it("each have a control", () => {
    for (const [shape, node] of Object.entries(everyShape)) {
      expect(shapeOf(node), shape).toBe(shape);
      expect(controlFor(node), shape).not.toBeNull();
    }
    expect(Object.keys(everyShape).sort()).toEqual([...FORM_SHAPES].sort());
  });

  it("finds no control for a shape it does not draw, a format included", () => {
    expect(controlFor({ type: "string", format: "date-time" } as ParameterSchema)).toBeNull();
    expect(controlFor({ type: "string", format: "time" })).toBeNull();
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
      { path: "panels[1].bindings.columns", message: "A field is named twice." },
      { path: "panels[1].range", message: "Shorten it." },
      { path: "panels[0].kind", message: "Cannot be shown so." },
      { path: "panels[0].grid", message: "Send a grid." },
      { path: "panels[0].id", message: "Use only letters." },
    ]);

    expect(routed.report.map((e) => e.path)).toEqual(["title"]);
    const second = routed.panels.get(1)!;
    expect(second.dataset).toEqual(["'x' is not a dataset."]);
    expect(second.parameters).toEqual([{ path: "cohorts[0].dayOfWeek[2]", message: "Send one of: Mon, Tue." }]);
    expect(second.bindings).toEqual([{ path: "columns", message: "A field is named twice." }]);
    expect(second.range).toEqual(["Shorten it."]);
    expect(routed.panels.get(0)!.kind).toEqual(["Cannot be shown so."]);
    expect(routed.panels.get(0)!.grid).toEqual(["Send a grid."]);
    expect(routed.panels.get(0)!.panel).toEqual(["Use only letters."]);
  });

  it("go to the track and the layer row their path names, and to the part of the row", () => {
    const routed = routeSpecErrors([
      { path: "panels[0].tracks[1].layers[2]", message: "A track has at most two units." },
      { path: "panels[0].tracks[1].layers[2].bindings.y", message: "Different units." },
      { path: "panels[0].tracks[1].layers[0].dataset.dayOfWeek", message: "A weekday is named twice." },
      { path: "panels[0].tracks[1].layers[0].derived.b.field", message: "Not a source." },
      { path: "panels[0].tracks[1].layers[0].derived.minutes", message: "Send 15 to 10080 minutes." },
      { path: "panels[0].tracks[1].layers[1].mark", message: "Cannot be drawn so." },
      { path: "panels[0].tracks[1].height", message: "Send a number." },
      { path: "panels[0].tracks", message: "A chart has 1 to 4 tracks." },
    ]);

    const panel = routed.panels.get(0)!;
    expect(panel.tracks).toEqual(["A chart has 1 to 4 tracks."]);
    const track = panel.trackMessages.get(1)!;
    expect(track.height).toEqual(["Send a number."]);
    expect(track.layers.get(2)!.layer).toEqual(["A track has at most two units."]);
    expect(track.layers.get(2)!.bindings).toEqual([{ path: "y", message: "Different units." }]);
    expect(track.layers.get(0)!.parameters).toEqual([{ path: "dayOfWeek", message: "A weekday is named twice." }]);
    expect(track.layers.get(0)!.derived).toEqual([
      { path: "b.field", message: "Not a source." },
      { path: "minutes", message: "Send 15 to 10080 minutes." },
    ]);
    expect(messagesUnder(track.layers.get(0)!.derived, "b")).toEqual([{ path: "field", message: "Not a source." }]);
    expect(track.layers.get(1)!.mark).toEqual(["Cannot be drawn so."]);
  });

  it("show at a leaf field with its items, and at a group or a list item only when they name it", () => {
    const messages = [
      { path: "cohorts[0].dayOfWeek[2]", message: "Pick weekdays from the list." },
      { path: "cohorts[0].timeOfDay", message: "The window starts and ends at the same time." },
      { path: "cohorts[1]", message: "Send an object." },
    ];
    expect(messagesAt(messages, "cohorts[0].dayOfWeek", true)).toEqual(["Pick weekdays from the list."]);
    expect(messagesAt(messages, "cohorts[0].timeOfDay", false)).toEqual(["The window starts and ends at the same time."]);
    expect(messagesAt(messages, "cohorts[0].timeOfDay.fromMinute", true)).toEqual([]);
    expect(messagesAt(messages, "cohorts[1]", false)).toEqual(["Send an object."]);
    expect(messagesAt(messages, "cohorts", false)).toEqual([]);
  });
});
