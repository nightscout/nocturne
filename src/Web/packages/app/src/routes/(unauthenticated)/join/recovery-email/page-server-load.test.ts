import { isRedirect } from "@sveltejs/kit";
import { describe, it, expect, vi } from "vitest";
import { load } from "./+page.server.js";

type Situation = {
  user?: object | null;
  recovery?: { available: boolean; email?: string | null } | Error;
  prompt?: { show: boolean };
};

function answer<T>(value: T | Error) {
  return async () => {
    if (value instanceof Error) throw value;
    return value;
  };
}

async function run(situation: Situation, search = "?next=%2F%3Fwelcome%3D1") {
  const event = {
    locals: {
      user: situation.user === undefined ? { subjectId: "s1" } : situation.user,
      apiClient: {
        recoveryEmail: { getRecoveryEmail: answer(situation.recovery ?? { available: true, email: null }) },
        recoveryEmailPrompt: { getRecoveryEmailPrompt: answer(situation.prompt ?? { show: true }) },
      },
    },
    url: new URL(`https://sam.example.test/join/recovery-email${search}`),
  } as unknown as Parameters<typeof load>[0];

  try {
    return { data: await load(event) };
  } catch (error) {
    if (isRedirect(error)) return { redirect: error.location };
    throw error;
  }
}

describe("join recovery email step load", () => {
  it("shows for a signed-in member with no address where mail can be sent", async () => {
    expect(await run({})).toEqual({ data: { next: "/?welcome=1" } });
  });

  it.each([
    ["mail cannot be sent", { recovery: { available: false } }],
    ["an address is already saved", { recovery: { available: true, email: "a@example.test" } }],
    ["the reminder is dismissed", { prompt: { show: false } }],
    ["nobody is signed in", { user: null }],
  ] as [string, Situation][])("goes straight on when %s", async (_, situation) => {
    expect(await run(situation)).toEqual({ redirect: "/?welcome=1" });
  });

  it("goes straight on rather than gate joining when a read fails", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});

    expect(await run({ recovery: new Error("down") })).toEqual({ redirect: "/?welcome=1" });
  });

  it("never follows a next that leaves the site", async () => {
    expect(await run({ user: null }, "?next=%2F%2Fevil.test")).toEqual({ redirect: "/" });
    expect(await run({ user: null }, "?next=https%3A%2F%2Fevil.test")).toEqual({ redirect: "/" });
  });
});
