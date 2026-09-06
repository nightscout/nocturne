<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Activity, Syringe, Utensils } from "lucide-svelte";

  type InspectionContext = "glucose" | "delivery" | "treatment";

  interface ContextOption {
    type: InspectionContext;
    label: string;
    preview: string;
  }

  interface Props {
    open: boolean;
    options: ContextOption[];
    onSelect: (type: InspectionContext) => void;
    onClose: () => void;
  }

  let { open = $bindable(), options, onSelect, onClose }: Props = $props();

  const icons = {
    glucose: Activity,
    delivery: Syringe,
    treatment: Utensils,
  };

  const colors = {
    glucose: "text-green-600 dark:text-green-400",
    delivery: "text-blue-600 dark:text-blue-400",
    treatment: "text-orange-600 dark:text-orange-400",
  };
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md print:hidden">
    <Dialog.Header>
      <Dialog.Title>Inspect Point</Dialog.Title>
      <Dialog.Description>
        Multiple data types available at this time. Choose what to inspect.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-2">
      {#each options as option (option.type)}
        {@const Icon = icons[option.type]}
        <button
          type="button"
          class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
          onclick={() => onSelect(option.type)}
        >
          <div class="{colors[option.type]}">
            <Icon class="size-5" />
          </div>
          <div class="flex-1">
            <div class="font-medium text-sm">{option.label}</div>
            <div class="text-xs text-muted-foreground">{option.preview}</div>
          </div>
        </button>
      {/each}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={onClose}>
        Cancel
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
