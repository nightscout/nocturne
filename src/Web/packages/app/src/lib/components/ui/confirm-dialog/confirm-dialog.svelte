<script lang="ts">
  import type { Snippet } from "svelte";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { buttonVariants } from "$lib/components/ui/button";
  import { Loader2 } from "lucide-svelte";

  interface Props {
    /**
     * Drives the dialog when there is no `trigger` — either bound, or passed
     * one-way alongside `onOpenChange` when the truth lives in another value
     * (`open={pendingDelete !== null}`).
     */
    open?: boolean;
    onOpenChange?: (open: boolean) => void;
    /** The element that opens the dialog; receives the trigger's own props. */
    trigger?: Snippet<[Record<string, unknown>]>;
    title: string | Snippet;
    description?: Snippet;
    descriptionClass?: string;
    /** Body between the header and the footer. */
    children?: Snippet;
    /** Replaces the whole Cancel/confirm pair. */
    footer?: Snippet;
    confirmLabel?: string;
    cancelLabel?: string;
    /** Colours the confirm button as a destructive action. */
    destructive?: boolean;
    /** Spins and disables the confirm button. */
    busy?: boolean;
    onConfirm?: () => void;
  }

  let {
    open = $bindable(false),
    onOpenChange,
    trigger,
    title,
    description,
    descriptionClass,
    children,
    footer,
    confirmLabel = "Confirm",
    cancelLabel = "Cancel",
    destructive = false,
    busy = false,
    onConfirm,
  }: Props = $props();
</script>

<AlertDialog.Root bind:open {onOpenChange}>
  {#if trigger}
    <AlertDialog.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        {@render trigger(props)}
      {/snippet}
    </AlertDialog.Trigger>
  {/if}
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>
        {#if typeof title === "string"}
          {title}
        {:else}
          {@render title()}
        {/if}
      </AlertDialog.Title>
      {#if description}
        <AlertDialog.Description class={descriptionClass}>
          {@render description()}
        </AlertDialog.Description>
      {/if}
    </AlertDialog.Header>

    {@render children?.()}

    <AlertDialog.Footer>
      {#if footer}
        {@render footer()}
      {:else}
        <AlertDialog.Cancel>{cancelLabel}</AlertDialog.Cancel>
        <AlertDialog.Action
          onclick={onConfirm}
          disabled={busy}
          class={destructive ? buttonVariants({ variant: "destructive" }) : undefined}
        >
          {#if busy}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {/if}
          {confirmLabel}
        </AlertDialog.Action>
      {/if}
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
