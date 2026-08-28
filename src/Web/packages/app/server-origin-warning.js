// A cross-site rejection and an authorization failure are the same bare 403 on the
// wire, and the one that means "your reverse proxy is not forwarding
// X-Forwarded-Proto / X-Forwarded-Host" carries nothing that says so — the operator
// sees a login page that loads and a login that fails. Name the two origins that
// failed to match, once the handler has actually answered 403 so that an
// authorization 403 from the proxied API (whose origin matches) stays quiet.
//
// This cannot be exact: CORS admits browser requests from any subdomain of the base
// domain, so a genuine cross-subdomain call that is refused on authorization also
// pairs a mismatch with a 403 and will be reported. The budget below bounds that to
// noise rather than a flood.

/**
 * Compares the way SvelteKit does, on `URL.origin`, so a default port or a difference
 * in host case is not mistaken for a proxy that is dropping headers. Falls back to the
 * raw value for input that will not parse — both sides come off the wire.
 *
 * @param {string} value
 */
function normaliseOrigin(value) {
  try {
    return new URL(value).origin;
  } catch {
    return value;
  }
}

/** Distinct origin pairings reported per window, so a scanner cannot flood the log. */
const REPORT_LIMIT = 32;

/**
 * How long a reported pairing stays remembered. The cap on its own is absorbing: a
 * scanner that elicits 403s from 32 origins of its choosing spends the whole budget
 * and the diagnostic never speaks again for the life of the process — including for
 * the operator's own pairing, which is the one message this exists to deliver.
 * Expiring the budget bounds the log to REPORT_LIMIT lines per window rather than
 * REPORT_LIMIT lines for all time.
 */
const REPORT_WINDOW_MS = 10 * 60_000;

/** @returns {{ reported: Set<string>, windowStart: number | null }} */
export function createReportBudget() {
  return { reported: new Set(), windowStart: null };
}

const defaultBudget = createReportBudget();

/**
 * Attaches a one-shot 403 diagnostic to a request whose Origin disagrees with the
 * origin this server reconstructs from the forwarded headers.
 *
 * @param {import('http').IncomingMessage} req
 * @param {import('http').ServerResponse} res
 * @param {{ budget?: ReturnType<typeof createReportBudget>, warn?: (message: string) => void, now?: () => number }} [deps]
 */
export function warnOnOriginMismatch(req, res, deps = {}) {
  const budget = deps.budget ?? defaultBudget;
  const warn = deps.warn ?? console.warn;
  const now = deps.now ?? Date.now;

  const browserOrigin = req.headers.origin;
  if (!browserOrigin) return;

  const computedOrigin = `${req.headers['x-forwarded-proto']}://${req.headers['x-forwarded-host']}`;
  if (normaliseOrigin(browserOrigin) === normaliseOrigin(computedOrigin)) return;

  res.on('finish', () => {
    if (res.statusCode !== 403) return;

    // The window is anchored on first use, not on the epoch: anchoring at 0 would
    // make the very first window expire immediately and drop the deduplication.
    const at = now();
    if (budget.windowStart === null || at - budget.windowStart >= REPORT_WINDOW_MS) {
      budget.reported.clear();
      budget.windowStart = at;
    }

    const pairing = `${browserOrigin} -> ${computedOrigin}`;
    if (budget.reported.has(pairing)) return;
    if (budget.reported.size >= REPORT_LIMIT) return;
    budget.reported.add(pairing);

    warn(
      `[origin] Rejected a cross-site ${req.method} to ${req.url}. The browser said it is on ` +
        `${browserOrigin}; this server reconstructed ${computedOrigin} from x-forwarded-proto ` +
        `and x-forwarded-host. When those disagree because a reverse proxy is not forwarding ` +
        `them, set Host, X-Forwarded-Host and X-Forwarded-Proto on it: ` +
        `https://getnocturne.dev/docs/installation/reverse-proxy`
    );
  });
}
