import Plug from "@lucide/svelte/icons/plug";
import Bell from "@lucide/svelte/icons/bell";
import Smartphone from "@lucide/svelte/icons/smartphone";
import Syringe from "@lucide/svelte/icons/syringe";
import Users from "@lucide/svelte/icons/users";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import type { Component } from "svelte";
import { resolve } from "$app/paths";
import type { PaletteId } from "@nocturne/watercolour";
import { SetupHubItemKey } from "$api";
import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

/**
 * How the frontend presents one setup hub item. The server decides which items
 * a tenant has and where each stands; this only names, describes and routes
 * them. A new item is one entry here, keyed by its `SetupHubItemKey` in
 * {@link setupHubItems}, plus its guided page under `/setup/<slug>` if it needs
 * more than the generic one.
 */
export interface SetupHubItemView {
  /** The guided page is `/setup/<slug>`. */
  slug: string;
  icon: Component;
  palette: PaletteId;
  /** The full settings page the guided page hands over to. */
  settingsHref: string;
  title: string;
  description: (voice: PatientVoice) => string;
}

/**
 * Built per call, so the titles and descriptions are read in the current
 * locale.
 */
export function setupHubItems(): Record<SetupHubItemKey, SetupHubItemView> {
  return {
    [SetupHubItemKey.ConnectData]: {
      slug: "connect-data",
      icon: Plug,
      palette: "water",
      settingsHref: resolve("/(authenticated)/settings/connectors"),
      title: "Connect data",
      description: (voice) =>
        voice.kind === "self"
          ? "Bring in your glucose readings from a CGM app, a cloud service or an uploader."
          : voice.kind === "named"
            ? `Bring in ${voice.name}'s glucose readings from a CGM app, a cloud service or an uploader.`
            : "Bring in glucose readings from a CGM app, a cloud service or an uploader.",
    },
    [SetupHubItemKey.Alerts]: {
      slug: "alerts",
      icon: Bell,
      palette: "ember",
      settingsHref: resolve("/(authenticated)/alerts"),
      title: "Alerts",
      description: (voice) =>
        voice.kind === "self"
          ? "Choose when Nocturne should alert you about your glucose."
          : voice.kind === "named"
            ? `Choose when Nocturne should alert you about ${voice.name}'s glucose.`
            : "Choose when Nocturne should send glucose alerts.",
    },
    [SetupHubItemKey.Devices]: {
      slug: "devices",
      icon: Smartphone,
      palette: "slate",
      settingsHref: resolve("/(authenticated)/settings/patient"),
      title: "Devices",
      description: (voice) =>
        voice.kind === "self"
          ? "Record the sensor, pump and insulins you use."
          : voice.kind === "named"
            ? `Record the sensor, pump and insulins ${voice.name} uses.`
            : "Record the sensor, pump and insulins in use.",
    },
    [SetupHubItemKey.Therapy]: {
      slug: "therapy",
      icon: Syringe,
      palette: "moonlight",
      settingsHref: resolve("/(authenticated)/settings/profile"),
      title: "Therapy settings",
      description: (voice) =>
        voice.kind === "self"
          ? "Check the basal rates, carb ratios and targets your care team set."
          : voice.kind === "named"
            ? `Check the basal rates, carb ratios and targets ${voice.name}'s care team set.`
            : "Check the basal rates, carb ratios and targets the care team set.",
    },
    [SetupHubItemKey.Sharing]: {
      slug: "sharing",
      icon: Users,
      palette: "dusk",
      settingsHref: resolve("/(authenticated)/settings/members"),
      title: "Sharing",
      description: (voice) =>
        voice.kind === "self"
          ? "Choose who else can see your data."
          : voice.kind === "named"
            ? `Choose who else can see ${voice.name}'s data.`
            : "Choose who else can see this data.",
    },
    [SetupHubItemKey.About]: {
      slug: "about",
      icon: HeartPulse,
      palette: "moss",
      settingsHref: resolve("/(authenticated)/settings/patient"),
      title: "About",
      description: (voice) =>
        voice.kind === "self"
          ? "Add your diabetes type and diagnosis date for your reports."
          : voice.kind === "named"
            ? `Add ${voice.name}'s diabetes type and diagnosis date for their reports.`
            : "Add the diabetes type and diagnosis date for reports.",
    },
  };
}

export function setupHubItemBySlug(slug: string): SetupHubItemKey | undefined {
  const items = setupHubItems();
  return Object.values(SetupHubItemKey).find((key) => items[key].slug === slug);
}

/**
 * Painted stages in `hub-dawn-ridges`; stop k of them is a finished, dry
 * picture.
 */
export const HUB_PAINTING_STOPS = 6;

/**
 * The painting stop a hub with `open` items left shows: one whole stage per
 * resolved item, counted back from the finished picture, so an item a tenant
 * is never offered is already painted. With every item open it shows half the
 * first stage rather than blank paper; any resolved item lands on a dry stop.
 */
export function hubPaintingStop(open: number): number {
  return Math.min(HUB_PAINTING_STOPS, Math.max(HUB_PAINTING_STOPS - open, 0.5));
}
