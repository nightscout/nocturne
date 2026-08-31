<script lang="ts">
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import { Trash2 } from "lucide-svelte";
  import { formatDateDetailed } from "$lib/utils/formatting";

  // Local type definition for profile
  interface Profile {
    defaultProfile?: string;
    created_at?: string;
    store?: Record<string, any>;
    [key: string]: any;
  }

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

<ConfirmDialog
  bind:open
  onOpenChange={(o) => { if (!o) onClose(); }}
  confirmLabel={isLoading ? "Deleting..." : "Delete Profile"}
  destructive
  busy={isLoading}
  {onConfirm}
>
  {#snippet title()}
    <span class="flex items-center gap-2 text-destructive">
      <Trash2 class="h-5 w-5" />
      Delete Profile
    </span>
  {/snippet}

  {#snippet description()}
    Are you sure you want to delete this profile? This action cannot be
    undone.
  {/snippet}

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
</ConfirmDialog>
