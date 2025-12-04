<script lang="ts">
  import type { Profile } from "$lib/api";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import { Trash2 } from "lucide-svelte";
  import { formatDateDetailed } from "$lib/utils/date-formatting";

  interface Props {
    open: boolean;
    profile: Profile | null;
    isLoading?: boolean;
    onClose: () => void;
    onConfirm: () => void;
  }

  let {
    open = $bindable(),
    profile,
    isLoading = false,
    onClose,
    onConfirm,
  }: Props = $props();
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <Trash2 class="h-5 w-5" />
        Delete Profile
      </AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete this profile? This action cannot be
        undone.
      </AlertDialog.Description>
    </AlertDialog.Header>

    {#if profile}
      <div class="rounded-lg border bg-muted/50 p-4 my-4">
        <p class="font-medium">{profile.defaultProfile ?? "Unnamed Profile"}</p>
        <p class="text-sm text-muted-foreground">
          Created: {formatDateDetailed(profile.created_at)}
        </p>
        {#if profile.store}
          <p class="text-sm text-muted-foreground">
            Contains {Object.keys(profile.store).length} profile store(s)
          </p>
        {/if}
      </div>
    {/if}

    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={onClose} disabled={isLoading}>
        Cancel
      </AlertDialog.Cancel>
      <Button variant="destructive" disabled={isLoading} onclick={onConfirm}>
        {#if isLoading}
          Deleting...
        {:else}
          Delete Profile
        {/if}
      </Button>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
