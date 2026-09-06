<script lang="ts" module>
  /**
   * Wiring handed to the `control` snippet. Spread it onto the shadcn `Input`,
   * `Select.Trigger`, `Switch`, `Textarea` — anything that forwards attributes
   * to its underlying element — so the label, validation message and required
   * state stay attached to the real control.
   */
  export interface FormFieldControl {
    id: string;
    required: true | undefined;
    "aria-invalid": "true" | undefined;
    "aria-describedby": string | undefined;
  }
</script>

<script lang="ts">
  import type { Snippet } from "svelte";
  import { Label } from "$lib/components/ui/label";
  import { cn } from "$lib/utils";
  import { fieldMessages, type FieldIssues } from "./field-messages";

  interface Props {
    label: string;
    /** Control id. Generated when omitted. */
    id?: string;
    required?: boolean;
    /** Guidance shown below the control while the field has no error. */
    description?: string;
    /** Replaces {@link description} when a live status line is needed. */
    hint?: Snippet;
    /** Validation messages for this field; replaces the description when present. */
    issues?: FieldIssues;
    class?: string;
    labelClass?: string;
    control: Snippet<[FormFieldControl]>;
  }

  let {
    label,
    id,
    required = false,
    description,
    hint,
    issues,
    class: className,
    labelClass,
    control,
  }: Props = $props();

  const uid = $props.id();
  const controlId = $derived(id ?? `field-${uid}`);
  const messageId = $derived(`field-${uid}-message`);

  const messages = $derived(fieldMessages(issues));
  const invalid = $derived(messages.length > 0);
  const hasHint = $derived(!!hint || !!description);

  const wiring: FormFieldControl = $derived({
    id: controlId,
    required: required || undefined,
    "aria-invalid": invalid ? "true" : undefined,
    "aria-describedby": invalid || hasHint ? messageId : undefined,
  });
</script>

<div class={cn("space-y-2", className)}>
  <Label for={controlId} class={labelClass}>
    {label}
    {#if required}
      <span aria-hidden="true" class="text-destructive">*</span>
      <span class="sr-only">(required)</span>
    {/if}
  </Label>

  {@render control(wiring)}

  {#if invalid}
    <div id={messageId} role="alert" class="space-y-1">
      {#each messages as message}
        <p class="text-sm text-destructive">{message}</p>
      {/each}
    </div>
  {:else if hint}
    <div id={messageId}>{@render hint()}</div>
  {:else if description}
    <p id={messageId} class="text-xs text-muted-foreground">{description}</p>
  {/if}
</div>
