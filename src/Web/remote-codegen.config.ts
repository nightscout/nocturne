/**
 * The reason a rejected API call carried, read off the thrown value in one order
 * for every status arm.
 *
 * NSwag throws the parsed error body itself (not wrapped in ApiException) for a
 * response that declares a typed schema, so an RFC-7807 body arrives as the
 * thrown value: `detail` carries the reason and `title` only the status phrase,
 * which is why `detail` is read first. Without a typed schema NSwag throws an
 * ApiException whose `message` is its own boilerplate and whose body is left
 * unparsed, so that message is the last resort. `body.message` belongs to an
 * `HttpError` — the only other thing these catches can see, thrown by an
 * invalidated query's refresh — and is read first because a handler wrote it.
 *
 * That refresh runs inside the command's own `try`, so a write that succeeded
 * and a refresh that was then refused reject the caller with a reason belonging
 * to the read, not to the write: the arms cannot tell the two apart, and no
 * ordering here can.
 *
 * `body` names a local holding `parseErrorBody(err)` — the RFC-7807 fields
 * recovered from a body NSwag left unparsed (`$lib/api/error-body`). They sit
 * ahead of `message` because anything the server wrote beats NSwag's
 * boilerplate, and behind the parsed fields because those are the same fields
 * already decoded.
 */
const reason = (err: string, body: string) =>
  `${err}?.body?.message ?? ${err}?.detail ?? ${err}?.title ?? ` +
  `${body}?.detail ?? ${body}?.title ?? ${body}?.message ?? ${err}?.message`;

export default {
  openApiPath: './packages/app/src/lib/api/generated/openapi.json',
  outputDir: './packages/app/src/lib',
  remoteFunctionsOutput: 'api/generated',
  apiClientOutput: 'api/api-client.generated.ts',
  imports: {
    schemas: '$lib/api/generated/schemas',
    apiTypes: '$api',
  },
  nswagClientPath: './generated/nocturne-api-client',
  errorHandling: {
    // `parseErrorBody` recovers the reason from an error body NSwag left unparsed;
    // both the 403 and 500 arms read it. See `$lib/api/error-body`.
    imports: [`import { parseErrorBody } from '$lib/api/error-body';`],

    // The default redirects queries to /auth/login on 401. For a public share
    // host ({token}.share.{baseDomain}) the viewer is anonymous by design and has
    // no account to sign into — and the dashboard fetches categories the tenant
    // may not have shared, each 401ing — so the default bounces them to login
    // ("flash of dashboard, then redirect"). On a share host, surface 401 as a
    // normal error instead so unshared categories just fail their widget rather
    // than navigating away. Host detection mirrors $lib/share-host's isShareHost
    // (inlined — generated code can't import it). Commands/forms already throw
    // error(401) and never redirected.
    on401: (kind: string) =>
      kind === 'query'
        ? `const { request, url } = getRequestEvent();\n` +
          `    const shareHost = request.headers.get('x-forwarded-host') ?? request.headers.get('host') ?? '';\n` +
          `    if (/^[^.]+\\.share\\./i.test(shareHost)) throw error(401, 'Unauthorized');\n` +
          '    throw redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`)'
        : `throw error(401, 'Unauthorized')`,

    // Forward the server's actual error message for 403 so the FE can show
    // a meaningful reason (e.g. "Insufficient permissions for …") instead of
    // a bare "Forbidden".
    //
    // The generator emits this arm inside a block of its own, so it may declare
    // the locals the read order needs.
    on403:
      `const e403 = err as any;\n` +
      `      const b403 = parseErrorBody(e403);\n` +
      `      throw error(403, ${reason('e403', 'b403')} ?? 'Forbidden')`,

    // The default `on500` swallows every non-401/403 status as a 500 with a
    // generic message. Forward 400 (validation, e.g. cyclic alert_state
    // references) and 409 (resource conflict, e.g. revoking an already-redeemed
    // alert invite) with the server's response body so the FE can show a useful
    // message to the user. Falls through to a 500 with the extracted message
    // so the real error is still visible in dev.
    //
    // The status is read off the thrown value, so an error body must declare a
    // `status` of its own to reach these arms — a plain payload flattens to a
    // 500 no matter what the response said.
    //
    // 429 and 404 are forwarded too, and with a fixed reason rather than the
    // extracted message: the rate limiter's body declares no typed schema, so
    // NSwag supplies its own "status code was not expected" text, and a missing
    // resource is answered with either an empty body (`NotFound()`) or a `detail`
    // that echoes back the id it was handed ("Body weight record with ID … not
    // found") — neither is copy to put in front of a user, while the status is
    // what tells a caller "already gone"/"you were throttled" from "this failed".
    // `describeSubmitError` and `remoteErrorMessage` resolve the wording from
    // the status.
    //
    // A forwarded 404 reaches a query and a command differently, and both are
    // wanted: a query awaited by a page load renders the not-found page, while a
    // command or form rejects its caller with an `HttpError(404)` — SvelteKit
    // renders an error page only for a failed load, so a dialog still gets to
    // show its own "already gone" wording.
    on500: (functionName: string) =>
      `const e = err as any;\n` +
      `    const b = parseErrorBody(e);\n` +
      `    const errors = e?.errors ?? b?.errors;\n` +
      `    const flat = errors ? Object.entries(errors).map(([, v]: [string, any]) => Array.isArray(v) ? v.join(', ') : v).join('; ') : undefined;\n` +
      `    const message = flat ?? ${reason('e', 'b')};\n` +
      `    if (status === 429) throw error(429, 'Too many requests');\n` +
      `    if (status === 404) throw error(404, 'Not found');\n` +
      `    if (status === 400 || status === 409) throw error(status, message ?? 'Request rejected');\n` +
      `    throw error(500, message ?? 'Failed to ${functionName}')`,
  },
};
