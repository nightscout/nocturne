import { goto } from "$app/navigation";
import { match } from "$app/paths";
import { safeReturnUrl } from "#lib/navigation/safe-return-url.js";

/**
 * Navigates to a same-origin return address after sign-in. One that is not a page of the app, such
 * as the OAuth authorize endpoint the server proxies to the API, is loaded in full: goto() rejects
 * a URL that matches no route. One that is not a same-origin path goes to the home page instead.
 */
export async function goToReturnUrl(
  returnUrl: string,
  options: { replace?: boolean; refreshAll?: boolean } = {},
): Promise<void> {
  const url = new URL(safeReturnUrl(returnUrl), location.origin);
  if (await match(url)) {
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- a same-origin path that matched a route
    return goto(url.pathname + url.search + url.hash, options);
  }
  if (options.replace) location.replace(url.href);
  else location.assign(url.href);
}
