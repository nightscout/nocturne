/**
 * Resolution of a logo id to the asset under `static/logos`.
 *
 * Kept out of AppLogo.svelte so the maps can be asserted against the files that
 * actually ship: an id with no asset renders a broken image, which is how
 * carelink, iaps and gluroo silently 404'd in production.
 */

/** Fallback mark for an id with no asset of its own. */
export const FALLBACK_LOGO = "/logos/device.svg";

/**
 * Ids that render another brand's asset: CareLink is Medtronic's service, so it
 * shares the Medtronic mark rather than shipping a second copy of it.
 */
export const logoAliases: Record<string, string> = {
  carelink: "medtronic",
};

/**
 * Marks that ship as one flat colour and disappear against a dark surface.
 * Inverted in dark mode so they read as light-on-dark.
 */
export const monochromeLogos = new Set(["iaps"]);

/** Extension per id; anything unlisted is assumed to be `.svg`. */
export const logoExtensions: Record<string, string> = {
  aaps: "png",
  dexcom: "png",
  discord: "png",
  eversense: "png",
  github: "png",
  glooko: "png",
  glucotracker: "png",
  gluroo: "png",
  "google-chat": "png",
  "home-assistant": "png",
  iaps: "png",
  juggluco: "png",
  libre: "png",
  loop: "png",
  mylife: "png",
  nightscout: "png",
  nocturne: "png",
  omnipod: "png",
  slack: "png",
  spike: "png",
  sugarmate: "png",
  tandem: "png",
  teams: "png",
  telegram: "png",
  twiist: "png",
  wechat: "png",
  whatsapp: "png",
  imessage: "jpg",
  medtronic: "jpg",
  messenger: "jpg",
  myfitnesspal: "jpg",
  tidepool: "jpg",
  trio: "jpg",
  xdrip: "jpg",
  xdrip4ios: "jpg",
};

/** The id an icon actually renders, after alias resolution. */
export function resolveLogoName(icon: string | undefined): string {
  const name = icon ?? "device";
  return logoAliases[name] ?? name;
}

/** Path under `static/` for an icon id, or a bare filename passed through. */
export function resolveLogoSrc(icon: string | undefined): string {
  const name = resolveLogoName(icon);
  if (name.includes(".")) return `/logos/${name}`;
  return `/logos/${name}.${logoExtensions[name] ?? "svg"}`;
}
