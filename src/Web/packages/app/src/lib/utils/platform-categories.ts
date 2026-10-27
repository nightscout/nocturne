const DISPLAY_NAMES: Record<string, string> = {
  discord: "Discord",
  slack: "Slack",
  telegram: "Telegram",
  whatsapp: "WhatsApp",
  resend: "Resend (email)",
};

/** The display name of a platform settings category, or the category itself when it has none. */
export function platformCategoryName(category: string): string {
  return DISPLAY_NAMES[category] ?? category;
}
