// A cross-site rejection and an authorization failure are the same bare 403 on the
// wire, and the one that means "your reverse proxy is not forwarding
// X-Forwarded-Proto / X-Forwarded-Host" carries nothing that says so — the operator
// sees a login page that loads and a login that fails. Name the two origins that
// failed to match, once the handler has actually answered 403 so that an
// authorization 403 from the proxied API (whose origin matches) stays quiet.

/** Distinct origin pairings already reported, so a scanner cannot flood the log. */
const REPORT_LIMIT = 32;

/**
 * Attaches a one-shot 403 diagnostic to a request whose Origin disagrees with the
 * origin this server reconstructs from the forwarded headers.
 *
 * @param {import('http').IncomingMessage} req
 * @param {import('http').ServerResponse} res
 * @param {{ reported?: Set<string>, warn?: (message: string) => void }} [deps]
 */
export function warnOnOriginMismatch(req, res, deps = {}) {
  const reported = deps.reported ?? defaultReported;
  const warn = deps.warn ?? console.warn;

  const browserOrigin = req.headers.origin;
  if (!browserOrigin) return;

  const computedOrigin = `${req.headers['x-forwarded-proto']}://${req.headers['x-forwarded-host']}`;
  if (browserOrigin === computedOrigin) return;

  res.on('finish', () => {
    if (res.statusCode !== 403) return;

    const pairing = `${browserOrigin} -> ${computedOrigin}`;
    if (reported.has(pairing)) return;
    if (reported.size >= REPORT_LIMIT) return;
    reported.add(pairing);

    warn(
      `[origin] Rejected a cross-site ${req.method} to ${req.url}. The browser said it is on ` +
        `${browserOrigin}; this server reconstructed ${computedOrigin} from x-forwarded-proto ` +
        `and x-forwarded-host. When those disagree because a reverse proxy is not forwarding ` +
        `them, set Host, X-Forwarded-Host and X-Forwarded-Proto on it: ` +
        `https://getnocturne.dev/docs/installation/reverse-proxy`
    );
  });
}

const defaultReported = new Set();
