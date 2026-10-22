import { errorStatus } from "$lib/forms/submit-error";

/**
 * Runs a provider change that the API refuses with 409 when some members sign in only through
 * that provider, and on that refusal asks before running it again with the override. Those
 * members then need a recovery code to get back in.
 *
 * @returns Whether the change went through: false when the person declined the override.
 */
export async function withLockoutOverride(
  providerName: string | undefined,
  run: (force: boolean) => Promise<unknown>,
  confirmOverride: (message: string) => boolean = (message) => confirm(message)
): Promise<boolean> {
  try {
    await run(false);
    return true;
  } catch (err: unknown) {
    if (errorStatus(err) !== 409) throw err;
    const proceed = confirmOverride(
      `Some members sign in only through "${providerName}". They will be locked out until an administrator gives them a recovery code. Continue anyway?`
    );
    if (proceed) await run(true);
    return proceed;
  }
}
