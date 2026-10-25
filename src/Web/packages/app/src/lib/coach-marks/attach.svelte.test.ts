import { afterEach, describe, expect, it, vi } from "vitest";
import { whileShown } from "./attach";

vi.mock("$app/state", () => ({ page: { data: {} } }));

const frame = () =>
  new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));

function target(display: string) {
  const element = document.createElement("div");
  element.style.display = display;
  element.textContent = "Target";
  document.body.append(element);
  return element;
}

/** A mark that writes `hold <name>` and `release <name>` to `log`. */
function loggedMark(log: string[], name: string) {
  return () => {
    log.push(`hold ${name}`);
    return () => void log.push(`release ${name}`);
  };
}

const run = (detach: unknown) => {
  if (typeof detach === "function") detach();
};

describe("whileShown", () => {
  afterEach(() => document.body.replaceChildren());

  it("takes the mark at attach time on an element already shown", () => {
    const mark = vi.fn();
    run(whileShown(mark)(target("block")));

    expect(mark).toHaveBeenCalledOnce();
  });

  it("releases the mark when the element is hidden, and takes it again when it is shown", async () => {
    const log: string[] = [];
    const element = target("block");
    const detach = whileShown(loggedMark(log, "a"))(element);

    element.style.display = "none";
    await frame();
    element.style.display = "block";
    await frame();
    run(detach);

    expect(log).toEqual(["hold a", "release a", "hold a", "release a"]);
  });

  it("never takes the mark on an element that stays hidden, though the observer reads it", async () => {
    const mark = vi.fn();
    const element = target("none");
    const reads = vi.spyOn(element, "getClientRects");
    const detach = whileShown(mark)(element);
    await frame();

    expect(reads.mock.calls.length).toBeGreaterThan(1);
    expect(mark).not.toHaveBeenCalled();
    run(detach);
  });

  it("holds the mark on the shown copy of a target rendered twice", async () => {
    const log: string[] = [];
    const detachDesktop = whileShown(loggedMark(log, "desktop"))(
      target("block")
    );
    const detachMobile = whileShown(loggedMark(log, "mobile"))(target("none"));
    await frame();

    expect(log).toEqual(["hold desktop"]);
    run(detachDesktop);
    run(detachMobile);
  });

  it("moves the mark across a layout swap before the context's unmount check can run", async () => {
    const log: string[] = [];
    const desktop = target("block");
    const mobile = target("none");
    const detachDesktop = whileShown(() => {
      log.push("hold desktop");
      return () => {
        log.push("release desktop");
        queueMicrotask(() => log.push("unmount check"));
      };
    })(desktop);
    const detachMobile = whileShown(loggedMark(log, "mobile"))(mobile);
    await frame();

    desktop.style.display = "none";
    mobile.style.display = "block";
    await frame();

    expect(log.slice(1, 3).sort()).toEqual(["hold mobile", "release desktop"]);
    expect(log[3]).toBe("unmount check");
    run(detachDesktop);
    run(detachMobile);
  });
});
