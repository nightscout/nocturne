import { createHmac } from "crypto";
import { env } from "$env/dynamic/private";

/**
 * Carriage of the end user's address across the SSR hop to the API.
 *
 * Calls made for a page or remote function leave this container, not the browser, so without this
 * the API sees one address for every user and keys its per-client rate limits on it. The signature
 * is what makes the address believable — see `ClientRateLimitKey` on the API for the trust rule.
 */

/** Must stay in sync with `ServiceNames.Headers.ClientIp` on the API. */
export const CLIENT_IP_HEADER = "X-Nocturne-Client-Ip";

/** Must stay in sync with `ServiceNames.Headers.ClientIpSignature` on the API. */
export const CLIENT_IP_SIGNATURE_HEADER = "X-Nocturne-Client-Ip-Signature";

/** The part of SvelteKit's RequestEvent the address is read from. */
export interface ClientAddressSource {
  request: Request;
  getClientAddress(): string;
}

/**
 * The address of the browser this request came from: the last X-Forwarded-For entry, else the peer
 * of the connection this container accepted.
 *
 * The last entry is the one the nearest hop wrote, and the edge is configured to write exactly one
 * — so on every shipped topology this is the same value the first entry would give, minus the part
 * a caller can choose. Reading the first entry would let anyone name themselves any address and
 * have it signed below, which is the one thing the signature is supposed to prevent.
 */
export function getClientAddress(event: ClientAddressSource): string | null {
  const forwarded = event.request.headers.get("x-forwarded-for");
  const entries = forwarded?.split(",") ?? [];
  const client = entries[entries.length - 1]?.trim();
  if (client) return client;

  try {
    return event.getClientAddress() || null;
  } catch {
    // Throws while prerendering, where there is no client to name.
    return null;
  }
}

/**
 * Headers naming the end user of an onward API call, signed with the instance key.
 *
 * Empty when there is no address or no instance key to sign with: the API then partitions on the
 * connection it sees, which is this container — the pre-existing shared bucket.
 */
export function clientAddressHeaders(
  event: ClientAddressSource,
): Record<string, string> {
  const instanceKey = env.INSTANCE_KEY;
  const clientAddress = getClientAddress(event);
  if (!instanceKey || !clientAddress) return {};

  return {
    [CLIENT_IP_HEADER]: clientAddress,
    [CLIENT_IP_SIGNATURE_HEADER]: createHmac("sha256", instanceKey)
      .update(clientAddress)
      .digest("hex"),
  };
}
