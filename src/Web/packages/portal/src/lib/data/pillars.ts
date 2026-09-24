import { DATA_SOURCES } from "./connectors";
import { AVAILABLE_REPORT_COUNT } from "./reports";

/**
 * The four headline features. The landing page and /features both render this
 * list, so the copy lives here once.
 */
export interface Pillar {
  n: number;
  title: string;
  body: string;
  bullets: readonly string[];
  /** A --feature-* token from app.css, as var(); the pillar sets it as --highlight. */
  color: string;
}

export const PILLARS: readonly Pillar[] = [
  {
    n: 1,
    title: "The reports your clinic asks for.",
    body:
      "Executive Summary. Glucose Profile (AGP). Glucose Distribution. Day in Review. Week to Week. " +
      "Insulin Delivery. Site Change Impact. Sleep. " +
      `${AVAILABLE_REPORT_COUNT} reports, on the dashboard the day you install.`,
    bullets: [
      `${AVAILABLE_REPORT_COUNT} built-in reports, from AGP to pump battery`,
      "AGP, insulin, and site-change reports laid out for printing",
      "Your own target range drawn alongside the clinical consensus bands",
    ],
    color: "var(--feature-reports)",
  },
  {
    n: 2,
    title: "Plays nice with your gear.",
    body:
      `${DATA_SOURCES.length} devices, apps, and services already wired in. Dexcom, Libre, Medtronic, Tandem, Omnipod, ` +
      "Loop, Trio, AndroidAPS, xDrip+, Nightscout, Home Assistant. If your kit is on the list, it works on day one.",
    bullets: [
      "Sign in to your CGM account and readings start flowing",
      "Pull your Nightscout history in and run both while you switch",
      "Every app that uploads to Nightscout uploads to Nocturne",
    ],
    color: "var(--feature-connectors)",
  },
  {
    n: 3,
    title: "Tell Nocturne what to do.",
    body:
      "Build alarms that fit your life. \"When I'm under 70 for ten minutes, message my partner on Telegram " +
      "and turn the bedroom lights on through Home Assistant.\" Point-and-click rules, no scripts required.",
    bullets: [
      "When-this-then-that rules with thresholds, durations, and trends",
      "Deliver by push, email, Discord, Slack, Telegram, WhatsApp, Home Assistant, or a webhook",
      "Snooze one alarm, or set quiet hours for the night",
    ],
    color: "var(--feature-alarms)",
  },
  {
    n: 4,
    title: "No password to lose.",
    body:
      "Sign in with a passkey on your phone, or with Google, GitHub, or your own OpenID Connect provider. " +
      "Your health data stays on your server, and the Nocturne project never sees it.",
    bullets: [
      "Passkeys on every modern phone and laptop",
      "Google, GitHub, or any OpenID Connect provider",
      "Apps get their own scoped tokens, never your login",
    ],
    color: "var(--feature-sign-in)",
  },
] as const;
