import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales } from "wuchale/load-utils/server";
import * as main from "../../../../../../locales/main.loader.server.svelte.js";
import type { JoinInviteInfo, TeamSiteAccess } from "@nocturne/api-client";
import type { SupportedLocale } from "#lib/stores/appearance-store.svelte.js";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";
import TeamSitesSection from "../teams/TeamSitesSection.svelte";
import InviteSummary from "./InviteSummary.svelte";

const site: TeamSiteAccess = {
  tenantId: "site-1",
  displayName: "Sam's Diabetes",
  roleNames: ["Carer", "Viewer", "Nurse"],
  limitTo24Hours: false,
  givenAt: "2026-03-01T10:00:00Z",
  hasEnded: true,
};

const invite: JoinInviteInfo = {
  tenantName: "Sam's Diabetes",
  roleNames: ["Carer", "Viewer"],
  permissions: ["glucose.read"],
  grantsAccess: true,
};

const sites = (language: SupportedLocale) =>
  renderInLanguage(language, { component: TeamSitesSection, viewProps: { sites: [site], narrowsToGroups: false } });

const inviteSummary = (language: SupportedLocale) =>
  renderInLanguage(language, { component: InviteSummary, viewProps: { invite, signedIn: false } });

describe("role lists under the production translation transform", () => {
  beforeAll(async () => {
    await loadLocales(main.key, main.loadCount, main.loadCatalog, ["en", "de"]);
  });

  it("joins the roles of a site with the interface language's own list words", async () => {
    const german = await sites("de");

    expect(german).toContain("Rollen: Carer, Viewer und Nurse");
    expect(await sites("en")).toContain("Roles: Carer, Viewer, and Nurse");
  });

  it("joins the roles of an invite with the interface language's own list words", async () => {
    expect(await inviteSummary("de")).toContain("Carer und Viewer");
    expect(await inviteSummary("en")).toContain("Carer and Viewer");
  });
});
