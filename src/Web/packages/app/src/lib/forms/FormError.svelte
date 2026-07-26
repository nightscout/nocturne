<script lang="ts">
  import { AlertTriangle } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { fieldMessages, type FieldIssues } from "./field-messages";

  interface Props {
    /** Message(s) to show. Nothing renders when empty. */
    issues?: FieldIssues;
    /**
     * Move focus here as soon as a message appears. Use on submit failures so a
     * screen reader announces the reason instead of leaving focus on the button
     * that appeared to do nothing.
     */
    focusOnShow?: boolean;
    class?: string;
    /** The container element, so callers can focus it themselves. */
    ref?: HTMLDivElement | null;
  }

  let {
    issues,
    focusOnShow = false,
    class: className,
    ref = $bindable(null),
  }: Props = $props();

  const messages = $derived(fieldMessages(issues));

  $effect(() => {
    if (focusOnShow && messages.length > 0) ref?.focus();
  });
</script>

{#if messages.length > 0}
  <div
    bind:this={ref}
    role="alert"
    tabindex="-1"
    class={cn(
      "flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3 outline-none",
      className
    )}
  >
    <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
    <div class="space-y-1">
      {#each messages as message}
        <p class="text-sm text-destructive">{message}</p>
      {/each}
    </div>
  </div>
{/if}
