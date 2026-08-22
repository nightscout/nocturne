/**
 * Stub for @sveltejs/kit in browser test environment.
 *
 * The thrown values mirror the framework's own: an `HttpError` is a plain
 * `{ status, body }` object with no `Error` in its prototype chain, and so is a
 * `Redirect`. A stub that threw an `Error` instead would let a component test
 * pass against a handler that cannot read the rejection in production.
 */
class HttpError {
  status: number;
  body: App.Error;

  constructor(status: number, body?: App.Error | string) {
    this.status = status;
    if (typeof body === "string") {
      this.body = { message: body };
    } else if (body) {
      this.body = body;
    } else {
      this.body = { message: `Error: ${status}` };
    }
  }

  toString() {
    return JSON.stringify(this.body);
  }
}

class Redirect {
  constructor(
    public status: number,
    public location: string
  ) {}
}

class ValidationError extends Error {
  issues: { message: string }[];

  constructor(issues: { message: string }[]) {
    super("Validation failed");
    this.name = "ValidationError";
    this.issues = issues;
  }
}

class ActionFailure {
  constructor(
    public status: number,
    public data?: any
  ) {}
}

export function error(status: number, body?: App.Error | string): never {
  throw new HttpError(status, body);
}

export function isHttpError(e: unknown, status?: number): boolean {
  if (!(e instanceof HttpError)) return false;
  return !status || e.status === status;
}

export function redirect(status: number, location: string | URL): never {
  throw new Redirect(status, location.toString());
}

export function isRedirect(e: unknown): boolean {
  return e instanceof Redirect;
}

export function json(data: any, init?: ResponseInit) {
  return new Response(JSON.stringify(data), init);
}

export function fail(status: number, data?: any) {
  return new ActionFailure(status, data);
}

export function isActionFailure(e: unknown): boolean {
  return e instanceof ActionFailure;
}

// Returns never in @sveltejs/kit: callers invoke it bare and rely on it aborting,
// so returning here would let a rejected validation fall through to the success path.
export function invalid(...issues: ({ message: string } | string)[]): never {
  throw new ValidationError(
    issues.map((issue) =>
      typeof issue === "string" ? { message: issue } : issue
    )
  );
}

export function isValidationError(e: unknown): boolean {
  return e instanceof ValidationError;
}
