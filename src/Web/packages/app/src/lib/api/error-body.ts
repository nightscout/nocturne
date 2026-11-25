import type { RustValidationIssue } from "@nocturne/api-client";

/** The fields of an error body a status arm can route on. */
export interface ParsedErrorBody {
  /**
   * The sentence written for a person: RFC 7807 `detail`, or RFC 6749
   * `error_description` from an OAuth endpoint, which fills the same role.
   */
  detail?: string;
  /** RFC 7807 `title`, usually only the status phrase. */
  title?: string;
  /**
   * Carried instead of `detail` by a typed payload that declares its own
   * status.
   */
  message?: string;
  /** ASP.NET's per-field validation map. */
  errors?: Record<string, unknown>;
  /** The structured validation issues, when the body carries a well-formed set. */
  issues?: RustValidationIssue[];
  /** The stable machine code of the problem, when the body carries a well-formed one. */
  code?: string;
}

/**
 * Reads back the error body NSwag left unparsed on a thrown `ApiException`.
 *
 * NSwag parses an error response only for a status the operation declares a
 * `ProducesResponseType` for, and throws the parsed body itself. For any other
 * status it throws an `ApiException` whose `message` is its own boilerplate and
 * whose `response` holds the raw body text, parsed by nothing — so a curated
 * refusal on an undeclared status carries its reason in that string and nowhere
 * else.
 *
 * Every field is checked rather than cast, because what arrives is whatever the
 * far end sent: an `errors` that is a string would otherwise flatten to one
 * character per field and be shown to someone as their validation failure.
 */
export function parseErrorBody(err: unknown): ParsedErrorBody | undefined {
  if (!err || typeof err !== "object" || !("response" in err)) return undefined;

  const { response } = err;
  if (typeof response !== "string" || response.trim() === "") return undefined;

  let parsed: unknown;
  try {
    parsed = JSON.parse(response);
  } catch {
    return undefined;
  }

  if (!isPlainObject(parsed)) return undefined;

  return {
    detail: sentence(parsed.detail) ?? sentence(parsed.error_description),
    title: sentence(parsed.title),
    message: sentence(parsed.message),
    errors: isPlainObject(parsed.errors) ? parsed.errors : undefined,
    issues: parseIssues(parsed.issues),
    code: parseCode(parsed.code),
  };
}

/**
 * The problem code a body carries: lower snake_case, the shape the API's codes
 * are written in. Anything else is dropped, so a caller maps only a value it
 * can match against a constant.
 */
export function parseCode(value: unknown): string | undefined {
  return typeof value === "string" && PROBLEM_CODE.test(value) ? value : undefined;
}

const PROBLEM_CODE = /^[a-z0-9_]+$/;

/**
 * The structured issues a body carries, when every entry has the shape one
 * declares. Anything else is dropped rather than handed on as an issue the
 * caller would index into blindly.
 */
export function parseIssues(value: unknown): RustValidationIssue[] | undefined {
  if (!Array.isArray(value) || !value.every(isValidationIssue)) return undefined;
  return value;
}

function isValidationIssue(value: unknown): value is RustValidationIssue {
  if (!isPlainObject(value)) return false;

  return (
    typeof value.scope === "string" &&
    typeof value.path === "string" &&
    typeof value.reason === "string" &&
    (value.field === null ||
      value.field === undefined ||
      typeof value.field === "string")
  );
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function sentence(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() !== "" ? value : undefined;
}
