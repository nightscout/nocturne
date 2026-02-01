<script lang="ts">
  import type { Treatment } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import * as Dialog from "$lib/components/ui/dialog";

  interface Props {
    open: boolean;
    treatments: Treatment[];
    onSelect: (treatment: Treatment) => void;
    onClose: () => void;
  }

  let { open = $bindable(), treatments, onSelect, onClose }: Props = $props();

  function formatTreatmentSummary(treatment: Treatment): string {
    const parts: string[] = [];
    if (treatment.eventType) parts.push(treatment.eventType);
    if (treatment.insulin) parts.push(`${treatment.insulin}U`);
    if (treatment.carbs) parts.push(`${treatment.carbs}g carbs`);
    return parts.join(" • ") || "Treatment";
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Multiple Treatments</Dialog.Title>
      <Dialog.Description>
        Several treatments occurred around this time. Select one to edit.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-2">
      {#each treatments as treatment (treatment._id || treatment.mills)}
        <button
          type="button"
          class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
          onclick={() => onSelect(treatment)}
        >
          <div class="flex-1">
            <div class="font-medium text-sm">
              {formatTreatmentSummary(treatment)}
            </div>
            <div class="text-xs text-muted-foreground">
              {treatment.created_at
                ? new Date(treatment.created_at).toLocaleTimeString([], {
                    hour: "numeric",
                    minute: "2-digit",
                  })
                : ""}
            </div>
          </div>
          <Badge variant="outline" class="text-xs">
            {treatment.eventType || "Unknown"}
          </Badge>
        </button>
      {/each}
    </div>
    <Dialog.Footer>
      <button
        type="button"
        class="px-4 py-2 text-sm rounded-md border border-input bg-background hover:bg-accent transition-colors"
        onclick={onClose}
      >
        Cancel
      </button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
