import { DATA_SOURCES } from "./connectors";
import { AVAILABLE_REPORT_COUNT } from "./reports";

/**
 * The four headline features. The landing page and /features both render this
 * list, so the copy lives here once.
 */
export interface Pillar {
  n: number;
  eyebrow: string;
  title: string;
  accent: string;
  body: string;
  bullets: readonly string[];
  color: string;
}

export const PILLARS: readonly Pillar[] = [
  {
    n: 1,
    eyebrow: "Reports",
    title: "The reports your clinic asks for.",
    accent: "Built in.",
    body:
      "Executive Summary. Glucose Profile (AGP). Glucose Distribution. Day in Review. Week to Week. " +
      "Insulin Delivery. Site Change Impact. Sleep. " +
      `${AVAILABLE_REPORT_COUNT} reports, on the dashboard the day you install.`,
    bullets: [
      `${AVAILABLE_REPORT_COUNT} built-in reports, from AGP to pump battery`,
      "AGP, insulin, and site-change reports laid out for printing",
      "Your own target range drawn alongside the clinical consensus bands",
    ],
    color: "oklch(0.6 0.118 184.704)",
  },
  {
    n: 2,
    eyebrow: "Connectors",
    title: "Plays nice with your gear.",
    accent: "Right out of the box.",
    body:
      `${DATA_SOURCES.length} devices, apps, and services already wired in. Dexcom, Libre, Medtronic, Tandem, Omnipod, ` +
      "Loop, Trio, AndroidAPS, xDrip+, Nightscout, Home Assistant. If your kit is on the list, it works on day one.",
    bullets: [
      "Sign in to your CGM account and readings start flowing",
      "Pull your Nightscout history in and run both while you switch",
      "Every app that uploads to Nightscout uploads to Nocturne",
    ],
    color: "oklch(0.72 0.16 150)",
  },
  {
    n: 3,
    eyebrow: "Alarms",
    title: "Tell Nocturne what to do.",
    accent: "It will.",
    body:
      "Build alarms that fit your life. \"When I'm under 70 for ten minutes, message my partner on Telegram " +
      "and turn the bedroom lights on through Home Assistant.\" Point-and-click rules, no scripts required.",
    bullets: [
      "When-this-then-that rules with thresholds, durations, and trends",
      "Deliver by push, email, Discord, Slack, Telegram, WhatsApp, Home Assistant, or a webhook",
      "Snooze one alarm, or set quiet hours for the night",
    ],
    color: "oklch(0.646 0.222 41.116)",
  },
  {
    n: 4,
    eyebrow: "Sign in",
    title: "No password to lose.",
    accent: "Or to leak.",
    body:
      "Sign in with a passkey on your phone, or with Google, GitHub, or your own OpenID Connect provider. " +
      "Your health data stays on your server, and the Nocturne project never sees it.",
    bullets: [
      "Passkeys on every modern phone and laptop",
      "Google, GitHub, or any OpenID Connect provider",
      "Apps get their own scoped tokens, never your login",
    ],
    color: "oklch(0.65 0.18 270)",
  },
] as const;
