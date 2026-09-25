import { SvelteMap } from "svelte/reactivity";
import { toast } from "svelte-sonner";
import { copyToClipboard } from "$lib/utils";

const RESET_MS = 2000;
const DEFAULT_KEY = "default";

export interface CopyFeedback {
  copy(text: string, key?: string): Promise<boolean>;
  isCopied(key?: string): boolean;
}

/**
 * One component's "copied" state: which keys are freshly copied, each reset
 * after a short window, plus the single failure message every copy site shows.
 *
 * The timer lives per key, so a repeat copy of one key restarts its window
 * without touching another key already showing feedback. Timers are cleared
 * when the owning component is destroyed.
 */
export function createCopyFeedback(): CopyFeedback {
  const copied = new SvelteMap<string, boolean>();
  const resetTimers = new SvelteMap<string, ReturnType<typeof setTimeout>>();

  $effect(() => {
    return () => {
      for (const timer of resetTimers.values()) clearTimeout(timer);
      resetTimers.clear();
    };
  });

  function resetAfter(key: string) {
    const pending = resetTimers.get(key);
    if (pending !== undefined) clearTimeout(pending);
    resetTimers.set(
      key,
      setTimeout(() => {
        copied.set(key, false);
        resetTimers.delete(key);
      }, RESET_MS)
    );
  }

  return {
    async copy(text, key = DEFAULT_KEY) {
      if (!(await copyToClipboard(text))) {
        toast.error(
          "Couldn't copy to the clipboard. Copy it manually instead."
        );
        return false;
      }
      copied.set(key, true);
      resetAfter(key);
      return true;
    },
    isCopied(key = DEFAULT_KEY) {
      return copied.get(key) === true;
    },
  };
}
