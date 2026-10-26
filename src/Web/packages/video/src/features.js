/**
 * The app pages a feature film mounts, keyed by composition name (`compositions/feature-<name>/`),
 * fixture (`src/fixtures/features/<name>.json`) and film (`src/features/films/<name>.js`). A feature
 * stacks one or more pages, top to bottom, under one root. `page` is a page's path under the
 * `(authenticated)` route directory; `route` is the address the capture opens, where `{name}` is a
 * parameter the capture resolves from the tenant (see `scripts/capture-features.js`). A feature with
 * `fixture` films another feature's pages and recording, from a different angle.
 * A feature with `stage` films a component in `src/features/stages/` instead of app pages, for a
 * feature that lives outside the web app (a chat bot, the Windows widget); it borrows `fixture`'s
 * clock and readings.
 */
export const features = {
  clocks: {
    pages: [{ route: "/clock/config/{id}", page: "clock/config/[id]/+page.svelte" }],
  },
  caregivers: { pages: [{ route: "/settings/members", page: "settings/members/+page.svelte" }] },
  "guest-links": { pages: [{ route: "/settings/members", page: "settings/members/+page.svelte" }] },
  "public-link": { pages: [{ route: "/settings/members", page: "settings/members/+page.svelte" }] },
  dedup: {
    pages: [
      {
        route: "/settings/data-quality",
        page: "settings/data-quality/+page.svelte",
      },
    ],
  },
  "alert-simulator": {
    pages: [{ route: "/alerts/simulator", page: "alerts/simulator/+page.svelte" }],
  },
  "smart-snooze": { pages: [{ route: "/alerts/{id}", page: "alerts/[id]/+page.svelte" }] },
  trackers: {
    pages: [{ route: "/trackers", page: "trackers/+page.svelte" }],
  },
  bots: { stage: "BotChat.svelte", fixture: "clocks" },
  widget: { stage: "Widget.svelte", fixture: "clocks" },
  community: { stage: "Community.svelte", fixture: "clocks" },
  food: { pages: [{ route: "/meals", page: "meals/+page.svelte" }] },
};
