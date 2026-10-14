import { toast } from "svelte-sonner";
import SavedToastTitle from "./SavedToastTitle.svelte";

/**
 * A success toast with the confirmation wash behind it, for a save whose form
 * closes with it, so the submit button is gone before it could glaze.
 *
 * Not for a toast that states a therapy number (a bolus, a reading, a
 * reservoir level) or for a deletion; those stay on the plain `toast`.
 */
export function toastSaved(message: string): string | number {
  return toast.success(SavedToastTitle, { componentProps: { message } });
}
