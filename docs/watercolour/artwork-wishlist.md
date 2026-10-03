# Artwork wishlist

A survey of the front end for hero-artwork gaps. Every proposal is grounded in a
concrete screen read from `src/Web/packages/{app,portal}`. Catalogue as of the
survey: 38 artworks in `src/Web/packages/watercolour/src/types.ts`.

Eleven of the twelve proposals are now served by Lucide-derived watercolour
icons, baked into the curated asset set as `lucide-<name>` (see scene-format):
`weight-scale` -> `scale`, `open-book` -> `book-open`; the other nine keep their
proposal name (`database`, `fingerprint`, `server`, `syringe`, `sprout`,
`battery`, `flag`, `megaphone`, `rocket`). `sensor` has no Lucide analogue that
reads as a CGM disc; `cpu` was baked as a test-set icon, not as the sensor, so
that one proposal is still open (candidates to try at 48 px: `radio`,
`circle-dot`). The survey tables below stay as the record of the screens each
proposal was grounded in.

## Where artwork is used today

Both the portal and the app are wired. Sizing follows the detail tiers in
`detailForEdge`: nothing is painted below 192 px except `header-motif`, which
is a 5:1 rule rather than an icon.

| Surface | Treatment |
|---|---|
| Portal docs, 37 pages | `ArtworkHero` at 400 px, one distinct painting per page from `portal/src/lib/data/docs-artwork.ts`. Text flows around the silhouette; three pages park it in the gutter instead. |
| Portal marketing | Hero paintings on features (440 px) and changelog (256 px), plus the install section on the home page (240 px). |
| App report headers | 224 px under the page heading. |
| App empty states and dialog art | 192 px. |
| Card grids and list rows, both packages | Lucide glyphs, not watercolour. A canvas under 64 px renders at the `small` tier and reads as a smudge. |
| Accents | `HeaderMotif` on the report pages; `ConfirmationBackground` on the docs and demo pages, hidden on dark. |
| Showcase | Unchanged mockups of intended usage, at the older sizes. |

## Proposed artworks

Ordered by how many screens would use them. All are square icons; none exist in
the catalogue.

| Proposed id | Depicts | Screens that would use it |
|---|---|---|
| `database` | A data cylinder (imported/self-hosted history) | Setup migration import step `setup/steps/ImportProgress.svelte`; `settings/migration/+page.svelte:583,606` empty states; `settings/admin/connector-cursors/+page.svelte:283` "re-pull all history"; portal get-involved "Donate anonymized data" lane `portal/src/routes/get-involved/+page.svelte:116`; docs BYO-Postgres header `portal/src/routes/docs/installation/byo-postgres/+page.svelte:11` |
| `fingerprint` | Concentric arcs (passkey sign-in) | Auth login `auth/login/+page.svelte:46` (currently `Fingerprint` lucide); setup account step `setup/steps/AccountCreation.svelte`; recovery passkey `auth/recovery/passkey/+page.svelte`; invite join ceremony `join/+page.svelte` (passkey registration); `auth/help/+page.svelte` |
| `server` | A stack of rounded server racks (self-hosted) | Portal install docs `docs/installation/+page.svelte` platform cards + subpages (`byo-postgres`, `docker-compose`, `oracle-cloud` headers); home "Run it tonight" section `portal/src/routes/+page.svelte:160`; features "Your server, your data" pillar `portal/src/routes/features/+page.svelte:53`; FAQ Installation category `portal/src/routes/faq/+page.svelte:36` |
| `syringe` | A diagonal insulin pen/barrel | `reports/insulin-delivery/+page.svelte:122` (currently inline `PieChart`); `reports/basal-analysis/BasalAnalysisContent.svelte:99` (currently inline `Layers`); `reports/treatments/+page.svelte:372` bare "Treatment Log" h1 |
| `sprout` | A stem with two leaves (fresh start) | Setup "Fresh Start" path badge `setup/+page@.svelte:504` (currently `Sprout` lucide); dashboard "Waiting for your first reading" `lib/components/dashboard/first-reading/FirstReadingEmptyState.svelte:48`; setup finish screen fresh path `setup/steps/Finish.svelte` |
| `weight-scale` | A bathroom scale with dial | `settings/weight/+page.svelte:70` bare "Weight history" h1; `settings/weight/+page.svelte:83` "No entries yet"; patient record weight section `settings/patient/+page.svelte:74` |
| `battery` | A battery cell with a bolt | `reports/battery/+page.svelte:119` bare "Battery Report" h1; `reports/battery/+page.svelte:147` empty state (currently `Battery` lucide); trackers Battery category `settings/trackers/+page.svelte:192` |
| `flag` | A finish pennant on a pole | Portal roadmap empty state `portal/src/routes/roadmap/+page.svelte:102` and milestone cards `MilestoneCard.svelte:90`; reports "Coming soon" rows `reports/+page.svelte:567`; portal changelog empty state `portal/src/routes/changelog/+page.svelte:139` |
| `open-book` | An open book | Portal docs "Documentation in Progress" callout `portal/src/routes/docs/+page.svelte:130`; get-involved "Improve the docs" lane `portal/src/routes/get-involved/+page.svelte:78`; setup finish "Your first report" next-step `setup/steps/Finish.svelte:59` |
| `sensor` | A CGM sensor disc | `reports/data-quality/sensor-integrity/+page.svelte:71` bare "Signal Integrity" h1; trackers Sensor/Cannula categories `settings/trackers/+page.svelte`; `reports/site-change-impact/+page.svelte:68` header |
| `megaphone` | A megaphone | Portal get-involved "Spread the word" lane `portal/src/routes/get-involved/+page.svelte:96`; portal changelog hero `portal/src/routes/changelog/+page.svelte:90` |
| `rocket` | A rocket with fins | Portal docs Quick Start card `portal/src/routes/docs/+page.svelte:26`; `docs/getting-started/+page.svelte`; home "Get started" CTA `portal/src/routes/+page.svelte:70` |

Rejected after considering: `heart-handshake` (portal sponsor/donate) — a heart
plus clasped hands is two motifs and will not read at 32-48 px; the existing
`heart` already covers those lanes. A `chart` icon was dropped because
`report-pages` already owns the reports surface.

## Places that want artwork but not a new one

Existing catalogue pieces that fit a screen the survey found:

| Screen | Fitting artwork |
|---|---|
| Every report subpage's bare in-page h1 (`reports/steps/+page.svelte:69`, `reports/heart-rate/+page.svelte:77`, `reports/sleep/+page.svelte:220`, `reports/treatments/+page.svelte:372`, `reports/data-quality/+page.svelte:87`, and the rest) | `header-motif` (5:1) banner, exactly as on `reports/+page.svelte:229` |
| Alerts header + empty `alerts/+page.svelte:170,329` | `alarm-bell` |
| Notifications header + "All caught up" `notifications/+page.svelte:202,250` | `alarm-bell`, `confirmation-mark` |
| Sleep empty state `reports/sleep/+page.svelte:232` | `crescent-moon` or `moonlit-shoreline` (16:9) |
| Steps report header `reports/steps/+page.svelte:69` | `footprints` |
| Heart Rate header `reports/heart-rate/+page.svelte:77` | `heart-rate` |
| Food empty state `food/+page.svelte:155` | `apple` |
| Clock faces empty `clock/+page.svelte:117` | `clock` |
| Members empty `settings/members/+page.svelte:274` | `people-group` |
| Connectors empty `settings/connectors/+page.svelte:437` | `plug` |
| Appearance header `settings/appearance/+page.svelte:198` | `paint-palette` |
| Timezone empty `settings/timezone/+page.svelte:104` | `world-globe` |
| Year overview empty `reports/year-overview/+page.svelte:747` | `calendar` |
| Data quality header `settings/data-quality/+page.svelte:101` | `shield` |
| Compatibility header + empty `compatibility/+page.svelte:169,424` | `linked-rings` |
| Dashboard "Waiting for your first reading" `first-reading/FirstReadingEmptyState.svelte:48` | `sunrise` (dawn of the first reading) |
| Reports "No Data Available" `reports/+page.svelte:413-428` | `report-pages` |
| Setup finish celebration check `setup/steps/Finish.svelte:87` | `confirmation-mark` |
| Login "Welcome to Nocturne" card `auth/login/+page.svelte:46` | `crescent-moon` (night brand) |
| Portal home hero + CTA sections `portal/src/routes/+page.svelte:36,158` | `distant-mountains` (2:1), `connected-shores` (2:1), `moonlit-shoreline` (16:9) |
| Portal feature pillars `portal/src/routes/features/+page.svelte:53-58` | `shield`, `people-group`, `plug`, `heart-rate`, `report-pages` |
| Portal get-involved lanes `get-involved/+page.svelte:46-123` | `heart`, `chat-bubble`, `world-globe`, `report-pages` (the four that already fit; see proposal table for the rest) |
| Portal demo page + docs callout | `confirmation-background` (3:1) |
| Portal roadmap/changelog page headers `roadmap/+page.svelte:51`, `changelog/+page.svelte:89` | `header-motif` (5:1) |
| Portal `SupportNocturne.svelte:42` pricing tiers | `heart` |

The setup wizard itself (`setup/+page@.svelte`) already carries a bespoke
`ConstellationCanvas`; no artwork belongs there.

## Aspect and kind

Every proposal above is a **square icon**. Rationale: each target sits beside a
heading or inside a card at 32-48 px, and the wide-banner need (report page
headers, portal heroes, callouts) is already served by `header-motif`, the
landscape scenes, and `confirmation-background` from section 3. `server` and
`database` could also be authored at a wide aspect if a docs page hero ever wants
them, but the square version covers every concrete screen listed.

| Proposed id | Kind |
|---|---|
| `database` | square icon |
| `fingerprint` | square icon |
| `server` | square icon (optionally reusable at 4:1) |
| `syringe` | square icon |
| `sprout` | square icon |
| `weight-scale` | square icon |
| `battery` | square icon |
| `flag` | square icon |
| `open-book` | square icon |
| `sensor` | square icon |
| `megaphone` | square icon |
| `rocket` | square icon |