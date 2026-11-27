import { describe, expect, it } from "vitest";
import { CLAUSE_MARKS, parseClauseLayout } from "./workspace.svelte.js";

const { signals, lens, range } = CLAUSE_MARKS;
const all = ["signals", "lens", "range"] as const;
const english = {
  slots: [
    { kind: "signals", before: "" },
    { kind: "lens", before: " " },
    { kind: "range", before: " " },
  ],
  after: "",
};

describe("the layout of a caption's clause", () => {
  it("reads the order of the parts and the text between them", () => {
    expect(parseClauseLayout(`${lens}, ${signals} - ${range}`, all)).toEqual({
      slots: [
        { kind: "lens", before: "" },
        { kind: "signals", before: ", " },
        { kind: "range", before: " - " },
      ],
      after: "",
    });
  });

  it("keeps the text before the first part and after the last", () => {
    const layout = parseClauseLayout(`(${signals} ${lens} ${range})`, all);

    expect(layout.slots[0]).toEqual({ kind: "signals", before: "(" });
    expect(layout.after).toBe(")");
  });

  it("reads a clause with no signals as two parts", () => {
    expect(parseClauseLayout(`${range}/${lens}`, ["lens", "range"]).slots).toEqual([
      { kind: "range", before: "" },
      { kind: "lens", before: "/" },
    ]);
  });

  it("falls back to the English order when a part is dropped", () => {
    expect(parseClauseLayout(`${lens} ${range}`, all)).toEqual(english);
  });

  it("falls back to the English order when a part is repeated", () => {
    expect(parseClauseLayout(`${signals} ${lens} ${lens} ${range}`, all)).toEqual(english);
    expect(parseClauseLayout(`${signals} ${signals} ${range}`, all)).toEqual(english);
  });

  it("falls back to the English order for an empty or unrelated message", () => {
    expect(parseClauseLayout("", all)).toEqual(english);
    expect(parseClauseLayout("something else", all)).toEqual(english);
  });
});
