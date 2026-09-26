# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

The primary users are people with diabetes (usually type 1) and the caregivers who follow them: parents, partners, and other family members. They check the current glucose value and its direction at a glance, many times a day and often at night. Afterwards they review trends, treatments, and reports to change therapy.

The secondary user is the self-hoster, the technically capable person who installs Nocturne, connects devices, and manages the tenants for a household or community. A single deployment can serve a household, a clinic, or a community. Clinics and multi-patient review are supported, but the app UI is not designed around them.

## Product Purpose

Nocturne is a self-hosted, open-source diabetes data platform. It pulls readings, treatments, and device state from every CGM, pump, looping system, and app a person uses into one real-time dashboard. It also serves the Nightscout API, so the existing ecosystem keeps working.

Success means a person or caregiver can trust what they see, understand it at once, and act on it. It also means someone moving from Nightscout only has to change the URL their apps point at.

## Positioning

Nocturne is a drop-in Nightscout, rebuilt. It keeps full v1, v2, and v3 Nightscout API compatibility, so existing uploaders, watch faces, and followers keep working unchanged. On that base it adds what Nightscout never had:
- native multitenancy with Row Level Security isolation;
- native connectors instead of bridge uploaders;
- real-time updates over SignalR and WebSockets;
- a PostgreSQL backend that keeps years of history fast.

Commercial platforms such as Clarity, Tidepool, and Sugarmate are not self-hosted and don't speak the Nightscout API. Nightscout itself is not multitenant or connector-native.

## Operating Context

- **Surfaces:**
  - the tenant web app (SvelteKit), with its dashboard, reports, alerts, food and meals, calendar, and time spans;
  - configurable clock faces and fullscreen displays;
  - public share links (`{token}.share.{domain}`), which show anonymous viewers only the categories the owner granted;
  - the marketing and docs portal (`src/Portal`, `packages/portal`).
- **Companion surfaces in the repo:** a desktop app, embeddable widgets, and a chat bot framework for Discord, Slack, Telegram, and WhatsApp. The Prelude Android app is a separate product.
- **Data sources:**
  - native connectors: Dexcom, FreeStyle Libre, Eversense, Medtronic CareLink, Tandem, twiist, myLife, Glooko, Tidepool, Gluroo, Nightscout, MyFitnessPal, Home Assistant, and remote Nocturne instances;
  - Nightscout-API uploaders: Loop, Trio, AndroidAPS, xDrip+, Juggluco, and others.
- **Auth:** passkeys, plus legacy `API_SECRET` for Nightscout clients.
- **Deployment:** Docker Compose or Portainer bundles, installed as "two files and a domain name".

## Capabilities and Constraints

- The backend owns the data. Calculations, statistics, and classifications such as glucose range state come from the API, and the frontend never recomputes them.
- The frontend owns the view: layout, presentation, interaction, visual treatment, and copy.
- User-facing strings live in the frontend translation layer (Wuchale). All UI copy must be translatable.
- UI icons are Lucide icons. No emoji.
- Users choose their glucose unit (mg/dL or mmol/L), and every value must show which unit it is in.
- Public shares are fail-closed. A share can only show the categories it was granted, and by default only the last 24 hours.

## Brand Commitments

- The name is Nocturne. The portal describes it as "Nightscout-compatible, rebuilt" and as "built by the diabetes community".
- **Medical-data caution is binding:**
  - Nocturne is not a medical device.
  - UI and copy must never be ambiguous about a value, its unit, its age, or its direction.
  - Copy must not use alarmist language beyond what an alert actually warrants.
  - Stale or missing data must be obvious, never disguised as current.
- **Data, not advice, is binding.** Nocturne shows the data and leaves the conclusions to the user. It never gives therapy or treatment recommendations, such as "your basal might need adjusting" or "consider a correction". It never tells the user what a pattern means for their dosing. Analyses and reports describe what happened. They don't say what to do about it.

## Evidence on Hand

- Real app screenshots, light and dark: `src/Web/packages/screenshots/images/`
- Connector and partner logos: `src/Web/packages/app/static/logos/` (listed in `packages/portal/src/lib/data/connectors.ts`)
- Community data (contributors, latest release): fetched live by the portal
- There are no customer testimonials, clinical claims, benchmarks, or user counts. Future work must not invent any.

## Product Principles

1. **Show, don't advise.** Present the facts clearly enough that the user can draw the conclusion themselves.
2. **Trust before delight.** Every glucose value is shown with its unit, time, and trend, and stale data is flagged.
3. **Compatibility is a promise.** Nothing may break the Nightscout clients people already depend on.
4. **Backend data, frontend view.** The API owns the numbers and classifications. The frontend owns how they are seen.
5. **Your data, your install.** It is self-hosted and open source, each tenant's data is isolated, and shares reveal only what was granted.

## Accessibility & Inclusion

- WCAG 2.2 AA is the minimum standard.
- Glucose range states (low, in range, high, and so on) must never be shown by colour alone. Pair colour with position, label, icon, or pattern, and keep it distinguishable under common colour-vision deficiencies.
- Respect `prefers-reduced-motion` and both light and dark themes.
