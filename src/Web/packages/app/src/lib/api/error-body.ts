/** The fields a parsed error body may carry that a status arm can route on. */
export interface ParsedErrorBody {
  /** RFC 7807 `detail` — the sentence written for a person. */
  detail?: string;
  /** RFC 7807 `title` — usually just the status phrase. */
  title?: string;
  /** The shape a typed error payload uses when the payload itself is the point. */
  message?: string;
  /** ASP.NET's per-field validation map. */
  errors?: Record<string, unknown>;
}

/**
 * The error body NSwag left unparsed on a thrown `ApiException`.
 *
 * NSwag parses an error response only for a status the operation declares a
 * `ProducesResponseType` for; it then throws the parsed body itself. For any
 * other status it throws an `ApiException` whose `message` is its own
 * boilerplate and whose `response` holds the raw body text, parsed by nothing.
 *
 * So a curated `Problem(detail: …)` refusal on an undeclared status carries its
 * reason only in that string. Reading it back is the only way the reason reaches
 * the user; no ordering of reads off the exception can recover what the
 * exception does not hold.
 *
 * Best-effort by construction: the body may be empty, HTML from a proxy, or a
 * JSON scalar. Anything that is not a JSON object answers undefined, so a caller
 * falls through to whatever it would have said otherwise.
 */
export function parseErrorBody(err: unknown): ParsedErrorBody | undefined {
  if (!err || typeof err !== "object" || !("response" in err)) return undefined;

  const { response } = err as { response?: unknown };
  if (typeof response !== "string" || response.trim() === "") return undefined;

  let parsed: unknown;
  try {
    parsed = JSON.parse(response);
  } catch {
    return undefined;
  }

  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return undefined;

  return parsed as ParsedErrorBody;
}
