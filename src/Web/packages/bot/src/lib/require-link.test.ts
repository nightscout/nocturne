import { describe, it, expect, vi } from "vitest";
import type { SlashCommandEvent } from "chat";
import { requireLink } from "./require-link.js";
import { getApi, runWithContext, type BotRequestContext } from "./request-context.js";
import type { BotApiClient, DirectoryCandidate } from "../types.js";

const HOME: DirectoryCandidate = {
  id: "link-home",
  tenantId: "11111111-1111-1111-1111-111111111111",
  tenantSlug: "home-clinic",
  nocturneUserId: "nocturne-user-home",
  label: "home",
  displayName: "HOME",
  isDefault: false,
};

const WORK: DirectoryCandidate = {
  ...HOME,
  id: "link-work",
  tenantId: "22222222-2222-2222-2222-222222222222",
  tenantSlug: "work-clinic",
  nocturneUserId: "nocturne-user-work",
  label: "work",
  displayName: "WORK",
};

const asDefault = (c: DirectoryCandidate): DirectoryCandidate => ({
  ...c,
  isDefault: true,
});

function createContext(candidates: DirectoryCandidate[] | null) {
  const resolve = vi.fn().mockResolvedValue(candidates);
  const scopedApiFactory = vi.fn(
    (tenantSlug: string) => ({ tenantSlug }) as unknown as BotApiClient,
  );
  const context: BotRequestContext = {
    unscopedApi: { directory: { resolve } } as unknown as BotApiClient,
    scopedApiFactory,
    resolvedTenantSlug: null,
    resolvedLink: null,
  };
  return { context, resolve, scopedApiFactory };
}

function createSlashEvent(text: string) {
  const postEphemeral = vi.fn().mockResolvedValue(null);
  const event = {
    adapter: { name: "discord" },
    channel: { post: vi.fn(), postEphemeral },
    command: "/bg",
    text,
    user: { userId: "discord-user-1", fullName: "Sam Tester" },
  } as unknown as SlashCommandEvent;
  return { event, postEphemeral };
}

async function runSlash(
  candidates: DirectoryCandidate[] | null,
  text: string,
) {
  const ctx = createContext(candidates);
  const { event, postEphemeral } = createSlashEvent(text);
  const seen: string[] = [];

  const result = await runWithContext(ctx.context, () =>
    requireLink(event, async (link) => {
      seen.push((getApi() as unknown as { tenantSlug: string }).tenantSlug);
      return link.label;
    }),
  );

  return { ...ctx, postEphemeral, result, seen };
}

describe("requireLink", () => {
  it("resolves the only link and ignores a mistyped label", async () => {
    const run = await runSlash([HOME], " Beach ");

    expect(run.result).toBe("home");
    expect(run.seen).toEqual(["home-clinic"]);
    expect(run.postEphemeral).not.toHaveBeenCalled();
  });

  it("resolves a multi-link user by the label argument", async () => {
    const run = await runSlash([HOME, WORK], "  WORK ");

    expect(run.result).toBe("work");
    expect(run.seen).toEqual(["work-clinic"]);
  });

  it("resolves a multi-link user to their default when no label is given", async () => {
    const run = await runSlash([HOME, asDefault(WORK)], "");

    expect(run.result).toBe("work");
    expect(run.seen).toEqual(["work-clinic"]);
    expect(run.postEphemeral).not.toHaveBeenCalled();
  });

  it("prefers an explicit label over the default", async () => {
    const run = await runSlash([HOME, asDefault(WORK)], "home");

    expect(run.result).toBe("home");
    expect(run.seen).toEqual(["home-clinic"]);
    expect(run.postEphemeral).not.toHaveBeenCalled();
  });

  it("stays ambiguous when more than one link claims to be default", async () => {
    const run = await runSlash([asDefault(HOME), asDefault(WORK)], "");

    expect(run.result).toBeNull();
    expect(run.seen).toEqual([]);
    expect(run.postEphemeral).toHaveBeenCalledOnce();
  });

  it("lists the choices when a multi-link user gives no label", async () => {
    const run = await runSlash([HOME, WORK], "");

    expect(run.result).toBeNull();
    expect(run.seen).toEqual([]);
    expect(run.postEphemeral).toHaveBeenCalledExactlyOnceWith(
      expect.anything(),
      "You have multiple linked Nocturne accounts: `home` (HOME), `work` (WORK). Use `/bg <label>` to pick one, or set a default in Settings → Integrations → Discord.",
      { fallbackToDM: true },
    );
  });

  it("lists the choices when the label matches nothing", async () => {
    const run = await runSlash([HOME, WORK], "beach");

    expect(run.result).toBeNull();
    expect(run.seen).toEqual([]);
    expect(run.postEphemeral).toHaveBeenCalledExactlyOnceWith(
      expect.anything(),
      "No linked account named `beach`. Your linked accounts: `home`, `work`.",
      { fallbackToDM: true },
    );
  });

  it("tells an unlinked user to connect", async () => {
    const run = await runSlash([], "");

    expect(run.result).toBeNull();
    expect(run.scopedApiFactory).not.toHaveBeenCalled();
    expect(run.postEphemeral).toHaveBeenCalledExactlyOnceWith(
      expect.anything(),
      "Your Discord account isn't linked to a Nocturne account yet. Run `/connect` to get started.",
      { fallbackToDM: true },
    );
  });
});
