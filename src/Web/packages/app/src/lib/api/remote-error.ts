import { describeRemoteError, READ_SURFACE } from "$lib/forms/submit-error";

/**
 * Message to show for a rejected remote function call on a reading surface —
 * a query whose rejection is rendered in place of the data it was going to
 * show. A mutation takes `describeSubmitError` even where its fallback names a
 * permission.
 *
 * @see {@link import("$lib/forms/submit-error").RemoteErrorPolicy} for how the
 * two halves differ and which one a call site wants.
 */
export function remoteErrorMessage(err: unknown, fallback: string): string {
  return describeRemoteError(err, fallback, READ_SURFACE);
}
