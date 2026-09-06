<script lang="ts">
  import type { Snippet } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Loader2, Save } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import FormError from "./FormError.svelte";
  import type { FieldIssues } from "./field-messages";

  interface Props {
    /**
     * The `form()` remote function driving this form. Its `pending` count puts
     * the submit button into its in-flight state and keeps it disabled, so a
     * double-tap can't submit twice.
     */
    form?: { readonly pending: number };
    /** Additional in-flight work owned by the caller (e.g. a second save). */
    pending?: boolean;
    /** Form-level error — one that belongs to no single field. */
    error?: FieldIssues;
    /** Move focus to the error when it appears. */
    focusError?: boolean;
    /** Disable submit for caller-owned reasons (nothing dirty, nothing selected). */
    disabled?: boolean;
    submitLabel?: string;
    /** Label while submitting. Defaults to {@link submitLabel}. */
    pendingLabel?: string;
    cancelLabel?: string;
    /** Renders a Cancel button when provided. Always `type="button"`. */
    onCancel?: () => void;
    /**
     * Associates the submit button with a `<form>` elsewhere in the document,
     * for layouts that keep the actions outside the form element.
     */
    formId?: string;
    /** Icon shown on the idle submit button. Pass `false` for none. */
    icon?: Snippet | false;
    /** Extra controls, rendered before Cancel. */
    children?: Snippet;
    class?: string;
  }

  let {
    form,
    pending = false,
    error,
    focusError = false,
    disabled = false,
    submitLabel = "Save",
    pendingLabel,
    cancelLabel = "Cancel",
    onCancel,
    formId,
    icon,
    children,
    class: className,
  }: Props = $props();

  const inFlight = $derived(pending || (form?.pending ?? 0) > 0);
</script>

<div class={cn("space-y-3", className)}>
  <FormError issues={error} focusOnShow={focusError} />

  <div class="flex items-center justify-end gap-2">
    {@render children?.()}

    {#if onCancel}
      <Button type="button" variant="outline" onclick={onCancel} disabled={inFlight}>
        {cancelLabel}
      </Button>
    {/if}

    <Button type="submit" form={formId} disabled={inFlight || disabled}>
      {#if inFlight}
        <Loader2 class="mr-2 h-4 w-4 animate-spin" />
      {:else if icon}
        {@render icon()}
      {:else if icon !== false}
        <Save class="mr-2 h-4 w-4" />
      {/if}
      {inFlight ? (pendingLabel ?? submitLabel) : submitLabel}
    </Button>
  </div>
</div>
