/** The exchange a handoff link asks for. */
export interface HandoffExchange {
  code: string;
  returnUrl?: string;
}

/**
 * The exchange carried by a handoff URL, or null when the link has no code —
 * which is the same dead end for the visitor as a code the API refuses.
 */
export function readHandoffExchange(url: URL): HandoffExchange | null {
  const code = url.searchParams.get("code");
  if (!code) return null;

  const returnUrl = url.searchParams.get("returnUrl");
  return returnUrl ? { code, returnUrl } : { code };
}
