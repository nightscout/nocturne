<script lang="ts">
  import type { Snippet } from "svelte";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { AlertTriangle, Loader2, Trash2 } from "lucide-svelte";

  interface Props {
    open: boolean;
    onOpenChange?: (open: boolean) => void;
    title: string;
    description: string;
    confirmationPhrase: string;
    onConfirm: () => Promise<void>;
    disabled?: boolean;
    confirmButtonText?: string;
    /** Optional content to display (e.g., data breakdown) */
    content?: Snippet;
    /** Optional result snippet to display after confirmation */
    result?: Snippet;
  }

  let {
    open = $bindable(false),
    onOpenChange,
    title,
    description,
    confirmationPhrase,
    onConfirm,
    disabled = false,
    confirmButtonText = "Confirm",
    content,
    result,
  }: Props = $props();

  let confirmText = $state("");
  let isConfirming = $state(false);
  let showResult = $state(false);

  const isConfirmEnabled = $derived(
    confirmText === confirmationPhrase && !disabled && !isConfirming
  );

  function handleOpenChange(newOpen: boolean) {
    if (!newOpen) {
      // Reset state when closing
      confirmText = "";
      showResult = false;
    }
    open = newOpen;
    onOpenChange?.(newOpen);
  }

  async function handleConfirm() {
    if (!isConfirmEnabled) return;

    isConfirming = true;
    try {
      await onConfirm();
      showResult = true;
    } catch (e) {
      console.error("DangerZoneDialog confirm error:", e);
    } finally {
      isConfirming = false;
    }
  }
</script>

<AlertDialog.Root bind:open onOpenChange={handleOpenChange}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <AlertTriangle class="h-5 w-5" />
        {title}
      </AlertDialog.Title>
      <AlertDialog.Description class="sr-only">
        {description}
      </AlertDialog.Description>
    </AlertDialog.Header>

    <div class="space-y-4">
      <div
        class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
      >
        <p class="text-sm font-semibold text-red-800 dark:text-red-200">
          THIS ACTION CANNOT BE UNDONE
        </p>
        <p class="text-sm text-red-700 dark:text-red-300 mt-2">
          {description}
        </p>
      </div>

      {#if content}
        {@render content()}
      {/if}

      {#if showResult && result}
        {@render result()}
      {:else if !showResult}
        <div class="space-y-2">
          <label for="danger-zone-confirm" class="text-sm font-medium">
            Type <strong>{confirmationPhrase}</strong> to confirm:
          </label>
          <Input
            id="danger-zone-confirm"
            type="text"
            bind:value={confirmText}
            placeholder="Type {confirmationPhrase}"
          />
        </div>
      {/if}
    </div>

    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => handleOpenChange(false)}>
        {showResult ? "Close" : "Cancel"}
      </AlertDialog.Cancel>
      {#if !showResult}
        <Button
          variant="destructive"
          onclick={handleConfirm}
          disabled={!isConfirmEnabled}
          class="gap-2"
        >
          {#if isConfirming}
            <Loader2 class="h-4 w-4 animate-spin" />
            Processing...
          {:else}
            <Trash2 class="h-4 w-4" />
            {confirmButtonText}
          {/if}
        </Button>
      {/if}
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
