import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/state", () => import("#lib/test-stubs/app-state.js"));
vi.mock("$app/navigation", () => import("#lib/test-stubs/app-navigation.js"));

import { page } from "#lib/test-stubs/app-state.js";
import { createCoachRouter } from "./router";

beforeEach(() => {
  page.state = {};
});

describe("coach router", () => {
  it("reads back the marker it pushes, keeping the page state already there", () => {
    page.state = { dialog: true };
    const router = createCoachRouter();

    expect(router.isMarkedEntry()).toBe(false);
    router.pushMarkedEntry();

    expect(router.isMarkedEntry()).toBe(true);
    expect(page.state).toMatchObject({ dialog: true });
  });

  it("does not read a marker another instance pushed, as after a reload", () => {
    createCoachRouter().pushMarkedEntry();

    expect(createCoachRouter().isMarkedEntry()).toBe(false);
  });
});
