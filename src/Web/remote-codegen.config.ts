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
    on403: `throw error(403, (err as any)?.message ?? (err as any)?.detail ?? 'Forbidden')`,

    // The default `on500` swallows every non-401/403 status as a 500 with a
    // generic message. Forward 400 (validation, e.g. cyclic alert_state
    // references) and 409 (resource conflict, e.g. referencing rules on
    // delete) with the server's response body so the FE can show a useful
    // message to the user. Falls through to a 500 with the extracted message
    // so the real error is still visible in dev.
    //
    // NSwag throws ProblemDetails directly (not wrapped in ApiException) for
    // responses that declare a typed error schema, so the title/detail/errors
    // fields live on `err` itself — not on `err.body` or `err.response`.
    on500: (functionName: string) =>
      `const e = err as any;\n` +
      `    const body = e?.body ?? e?.response;\n` +
      `    const errors = body?.errors ?? e?.errors;\n` +
      `    const flat = errors ? Object.entries(errors).map(([, v]: [string, any]) => Array.isArray(v) ? v.join(', ') : v).join('; ') : undefined;\n` +
      `    const message = flat ?? body?.message ?? body?.title ?? body?.detail ?? e?.message ?? e?.title ?? e?.detail;\n` +
      `    if (status === 400 || status === 409) throw error(status, message ?? 'Request rejected');\n` +
      `    throw error(500, message ?? 'Failed to ${functionName}')`,
  },
};
