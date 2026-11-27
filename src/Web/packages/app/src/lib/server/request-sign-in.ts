import { signInAddress } from "#lib/navigation/sign-in-address.js";
import { getOriginalHost, getOriginalProto } from "#lib/server/request-host.js";

type PageRequest = { url: URL; request: Request; isRemoteRequest: boolean };

/**
 * The origin the browser addressed, as the edge reports it. `request.url` is the address the
 * gateway forwarded to, which differs from the browser's under a rewritten Host.
 */
function edgeOrigin(request: Request): string | undefined {
  const host = getOriginalHost(request);
  if (host === null) return undefined;
  const origin = `${getOriginalProto(request)}://${host}`;
  return URL.canParse(origin) ? new URL(origin).origin : undefined;
}

/**
 * The page a request belongs to, as a path with its query, or undefined when that is not known.
 *
 * `event.url` is the page for everything but a query the browser calls: SvelteKit has already
 * removed a data request's suffix and reserved parameters from it, and a command or form sends the
 * page in `x-sveltekit-pathname`. A browser-called query sends no page header, so its `event.url`
 * is the remote endpoint; the page is the Referer, which the browser sets to the address of the
 * document that made the call. A Referer from another origin is never the page.
 */
function requestPage({ url, request, isRemoteRequest }: PageRequest): string | undefined {
  const browserQuery = isRemoteRequest && request.headers.get("x-sveltekit-pathname") === null;
  if (!browserQuery) return url.pathname + url.search;

  const referer = request.headers.get("referer");
  if (referer === null || !URL.canParse(referer)) return undefined;

  const page = new URL(referer);
  if (page.origin !== edgeOrigin(request)) return undefined;
  return page.pathname + page.search;
}

/**
 * The sign-in address that returns to the page the request belongs to, or the bare sign-in address
 * where the page is not known. A query cannot read `event.url` (SvelteKit throws), so a remote
 * function takes the address from `locals.signInAddress`, which closes over the hook's own event.
 */
export function signInAddressForRequest(event: PageRequest): string {
  return signInAddress(requestPage(event));
}
