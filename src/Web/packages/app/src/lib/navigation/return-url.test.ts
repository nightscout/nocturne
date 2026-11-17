import { beforeEach, describe, expect, it, vi } from "vitest";

const nav = vi.hoisted(() => ({ goto: vi.fn(), match: vi.fn() }));
vi.mock("$app/navigation", () => ({ goto: nav.goto }));
vi.mock("$app/paths", () => ({ match: nav.match }));

const { goToReturnUrl } = await import("./return-url");

const location = { origin: "https://sleepy.nocturne.test", assign: vi.fn(), replace: vi.fn() };

describe("returning after sign-in", () => {
  beforeEach(() => {
    vi.stubGlobal("location", location);
    nav.goto.mockReset();
    nav.match.mockReset();
    location.assign.mockReset();
    location.replace.mockReset();
  });

  it("navigates in the app to a page it has", async () => {
    nav.match.mockResolvedValue({ id: "/(authenticated)/reports/agp", params: {} });

    await goToReturnUrl("/reports/agp?days=14", { refreshAll: true });

    expect(nav.goto).toHaveBeenCalledWith("/reports/agp?days=14", { refreshAll: true });
    expect(location.assign).not.toHaveBeenCalled();
  });

  it("loads in full an address the app has no page for, such as the OAuth authorize endpoint", async () => {
    nav.match.mockResolvedValue(null);

    await goToReturnUrl("/api/oauth/authorize?client_id=x&state=y");

    expect(nav.goto).not.toHaveBeenCalled();
    expect(location.assign).toHaveBeenCalledWith(
      "https://sleepy.nocturne.test/api/oauth/authorize?client_id=x&state=y",
    );
  });

  it("replaces the history entry when asked, for either kind", async () => {
    nav.match.mockResolvedValue(null);

    await goToReturnUrl("/api/oauth/authorize?client_id=x", { replace: true });

    expect(location.replace).toHaveBeenCalledWith("https://sleepy.nocturne.test/api/oauth/authorize?client_id=x");
  });

  it("goes home instead of to an address that is not a same-origin path", async () => {
    nav.match.mockResolvedValue({ id: "/(authenticated)", params: {} });

    for (const returnUrl of ["https://evil.test/steal", "//evil.test/steal", "javascript:alert(1)"]) {
      nav.goto.mockReset();
      await goToReturnUrl(returnUrl, { replace: true });
      expect(nav.goto).toHaveBeenCalledWith("/", { replace: true });
    }
    expect(location.replace).not.toHaveBeenCalled();
    expect(location.assign).not.toHaveBeenCalled();
  });
});
