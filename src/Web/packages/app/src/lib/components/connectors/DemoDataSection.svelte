<script lang="ts">
  import { formatNumber } from "$lib/utils/formatting";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import {
    Sparkles,
    CheckCircle,
    AlertCircle,
    Loader2,
    Trash2,
  } from "lucide-svelte";
  import { deleteDemoData as deleteDemoDataRemote } from "$api/generated/services.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import { page } from "$app/state";

  let { open = $bindable(false), onDeleteComplete } = $props<{
    open: boolean;
    onDeleteComplete?: () => Promise<void>;
  }>();

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "tenant.settings")
  );

  let isDeletingDemo = $state(false);
  let demoDeleteResult = $state<{
    success?: boolean;
    totalDeleted?: number;
    error?: string;
  } | null>(null);

  // Reset state when opening
  $effect(() => {
    if (open) {
      demoDeleteResult = null;
      isDeletingDemo = false;
    }
  });

  async function deleteDemoData() {
    isDeletingDemo = true;
    demoDeleteResult = null;
    try {
      const result = await deleteDemoDataRemote();
      demoDeleteResult = {
        success: result.success ?? false,
        totalDeleted: result.totalDeleted,
        error: result.error ?? undefined,
      };
      if (result.success && onDeleteComplete) {
        await onDeleteComplete();
      }
    } catch (e) {
      demoDeleteResult = {
        success: false,
        error: describeSubmitError(e, "Failed to delete demo data"),
      };
    } finally {
      isDeletingDemo = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Sparkles class="h-5 w-5 text-purple-500" />
        Demo Data
      </Dialog.Title>
      <Dialog.Description>
        Manage the simulated demo data in your Nocturne instance
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if demoDeleteResult}
        {#if demoDeleteResult.success}
          <div
            class="rounded-lg border border-success/30 bg-success/10 p-4"
          >
            <div
              class="flex items-center gap-2 text-success"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Demo data cleared successfully</span>
            </div>
            <p class="text-sm text-success mt-1">
              Deleted {formatNumber(demoDeleteResult.totalDeleted)} records
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-destructive/30 bg-destructive/10 p-4"
          >
            <div class="flex items-center gap-2 text-destructive">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Failed to delete demo data</span>
            </div>
            <p class="text-sm text-destructive mt-1">
              {demoDeleteResult.error}
            </p>
          </div>
        {/if}
      {:else}
        <div
          class="rounded-lg border border-purple-200 dark:border-purple-800 bg-purple-50 dark:bg-purple-950/20 p-4"
        >
          <p class="text-sm text-purple-800 dark:text-purple-200">
            <strong>This is demo data</strong>
            — synthetic glucose readings generated for testing and demonstration purposes.
          </p>
        </div>

        <div class="space-y-3">
          <p class="text-sm text-muted-foreground">
            You can safely delete all demo data. It's very easy to regenerate:
          </p>
          <ul
            class="text-sm text-muted-foreground list-disc list-inside space-y-1"
          >
            <li>Restart the demo service to regenerate data</li>
            <li>Only demo-generated data will be deleted</li>
            <li>Your real health data (if any) is not affected</li>
          </ul>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (open = false)}>Close</Button>
      {#if canManage && !demoDeleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDemoData}
          disabled={isDeletingDemo}
        >
          {#if isDeletingDemo}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Clear Demo Data
          {/if}
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
