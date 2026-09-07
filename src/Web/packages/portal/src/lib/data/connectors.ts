/**
 * Everything Nocturne can take readings from or send alerts to, as shown on the
 * marketing pages. One list feeds the hero marquee, the connector search demo,
 * and the counts quoted in copy, so a new connector is added here once.
 *
 * `comingSoon` entries link to the tracking issue on GitHub.
 */
export interface Connector {
  /** File name under static/logos. */
  file: string;
  name: string;
  kind: string;
  /** Extra search terms for the connector demo. */
  aliases?: string[];
  comingSoon?: true;
  issue?: number;
}

export const CONNECTORS: Connector[] = [
  // Pulled in by a connector: sign in once, readings arrive by themselves.
  // Eversense and twiist are follower connectors: the wearer shares with the
  // account you sign in with.
  { file: "dexcom.png", name: "Dexcom", kind: "CGM", aliases: ["Clarity", "Share", "G6", "G7"] },
  { file: "libre.png", name: "FreeStyle Libre", kind: "CGM", aliases: ["Libre", "FSL", "Abbott", "LibreLinkUp"] },
  { file: "eversense.png", name: "Eversense", kind: "CGM", aliases: ["Senseonics"] },
  { file: "medtronic.jpg", name: "Medtronic", kind: "Pump", aliases: ["CareLink", "MiniMed", "780G"] },
  { file: "tandem.png", name: "Tandem", kind: "Pump", aliases: ["t:slim", "Mobi", "Tandem Source", "TConnect"] },
  { file: "twiist.png", name: "twiist", kind: "Pump", aliases: ["Sequel", "Tidepool Loop"] },
  { file: "mylife.png", name: "myLife", kind: "Pump", aliases: ["CamAPS", "YpsoPump"] },
  { file: "glooko.png", name: "Glooko", kind: "Cloud" },
  { file: "tidepool.jpg", name: "Tidepool", kind: "Cloud" },
  { file: "gluroo.png", name: "Gluroo", kind: "Cloud", aliases: ["Global Connect"] },
  { file: "nightscout.png", name: "Nightscout", kind: "Cloud", aliases: ["NS"] },
  // Omnipod data arrives through Glooko, twiist, or a looping app; there is no
  // Insulet account to sign in to.
  { file: "omnipod.png", name: "Omnipod", kind: "Pump", aliases: ["Insulet", "DASH", "5"] },
  { file: "myfitnesspal.jpg", name: "MyFitnessPal", kind: "Food", aliases: ["MFP"] },
  { file: "home-assistant.png", name: "Home Assistant", kind: "Smart Home", aliases: ["HA"] },

  // Uploaded by an app through the Nightscout-compatible API.
  { file: "loop.png", name: "Loop", kind: "Looping" },
  { file: "trio.jpg", name: "Trio", kind: "Looping", aliases: ["iAPS"] },
  { file: "aaps.png", name: "AndroidAPS", kind: "Looping", aliases: ["AAPS", "Android"] },
  { file: "xdrip.jpg", name: "xDrip+", kind: "App", aliases: ["xDrip", "Android"] },
  { file: "spike.png", name: "Spike", kind: "App", aliases: ["iOS"] },
  { file: "juggluco.png", name: "Juggluco", kind: "App" },
  { file: "glucotracker.png", name: "GlucoTracker", kind: "App" },
  { file: "sugarmate.png", name: "Sugarmate", kind: "App" },

  // Where alerts and chat commands go.
  { file: "discord.png", name: "Discord", kind: "Notify" },
  { file: "slack.png", name: "Slack", kind: "Notify" },
  { file: "telegram.png", name: "Telegram", kind: "Notify" },
  { file: "whatsapp.png", name: "WhatsApp", kind: "Notify", aliases: ["WA"] },
  { file: "email.jpg", name: "Email", kind: "Notify", aliases: ["Resend"] },

  // Coming soon
  { file: "medtrum.jpg", name: "Medtrum", kind: "CGM", comingSoon: true, issue: 106 },
  { file: "n8n.png", name: "n8n", kind: "Cloud", comingSoon: true, issue: 112 },
  { file: "twilio.png", name: "Twilio / SMS", kind: "Notify", aliases: ["SMS"], comingSoon: true, issue: 137 },
  { file: "oura.png", name: "Oura", kind: "App", aliases: ["Oura Ring"], comingSoon: true, issue: 151 },
  { file: "imessage.jpg", name: "iMessage", kind: "Notify", aliases: ["Apple Messages", "iOS"], comingSoon: true, issue: 178 },
  { file: "google-chat.png", name: "Google Chat", kind: "Notify", aliases: ["GChat"], comingSoon: true, issue: 179 },
  { file: "teams.png", name: "MS Teams", kind: "Notify", aliases: ["Microsoft Teams"], comingSoon: true, issue: 180 },
];

export const LIVE_CONNECTORS: Connector[] = CONNECTORS.filter((c) => !c.comingSoon);

/** Kinds that carry glucose, insulin, or food data in; the rest are alert destinations. */
export const DATA_SOURCES: Connector[] = LIVE_CONNECTORS.filter((c) => c.kind !== "Notify");
