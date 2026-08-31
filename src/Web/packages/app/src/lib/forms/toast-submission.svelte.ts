import { toast } from "svelte-sonner";
import { useSubmission } from "./submission.svelte";

export interface ToastSubmission {
  /** Whether a run is in flight. Disable the control that starts it. */
  readonly busy: boolean;
  /**
   * Runs `action`, turning a rejection into an error toast.
   *
   * @returns Whether the action succeeded.
   */
  run(action: () => unknown | Promise<unknown>): Promise<boolean>;
}

/**
 * Failure handling for an action that reports through a toast.
 *
 * `fallback` names the action the user took ("Failed to delete bolus") and is
 * what the toast says when the server sent nothing a person can act on; a 4xx
 * carrying a reason shows that reason instead. A call made while one is still
 * in flight is ignored.
 */
export function useToastSubmission(fallback: string): ToastSubmission {
  const submission = useSubmission({ fallback });
  let busy = $state(false);

  return {
    get busy() {
      return busy;
    },
    async run(action) {
      if (busy) return false;
      busy = true;
      try {
        const succeeded = await submission.run(async () => {
          await action();
          return true;
        });
        if (!succeeded) toast.error(submission.error ?? fallback);
        return succeeded;
      } finally {
        busy = false;
      }
    },
  };
}
