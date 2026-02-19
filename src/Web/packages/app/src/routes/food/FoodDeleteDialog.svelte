<script lang="ts">
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import * as RadioGroup from "$lib/components/ui/radio-group";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Loader2, Trash2 } from "lucide-svelte";
  import type { FoodRecord } from "./types";

  interface Props {
    open: boolean;
    food: FoodRecord | null;
    attributionCount: number;
    isLoading?: boolean;
    onClose: () => void;
    onConfirm: (mode: "clear" | "remove") => void;
  }

  let {
    open = $bindable(false),
    food,
    attributionCount,
    isLoading = false,
    onClose,
    onConfirm,
  }: Props = $props();

  let attributionMode = $state<"clear" | "remove">("clear");

  function handleOpenChange(value: boolean) {
    if (!value) {
      attributionMode = "clear";
      onClose();
    }
  }
</script>

<AlertDialog.Root bind:open onOpenChange={handleOpenChange}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <Trash2 class="h-5 w-5" />
        Delete Food
      </AlertDialog.Title>
      <AlertDialog.Description>
        {#if attributionCount > 0}
          This food is used in {attributionCount} meal attribution{attributionCount !== 1 ? "s" : ""}. Choose how to handle them.
        {:else}
          Are you sure you want to delete this food? This action cannot be undone.
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>

    {#if food}
      <div class="rounded-lg border bg-muted/50 p-4 my-2">
        <p class="font-medium">{food.name}</p>
        <p class="text-sm text-muted-foreground">
          {food.carbs}g carbs
          {#if food.category}
            &middot; {food.category}{#if food.subcategory} / {food.subcategory}{/if}
          {/if}
        </p>
      </div>
    {/if}

    {#if attributionCount > 0}
      <div class="space-y-3 my-2">
        <p class="text-sm font-medium">What should happen to the {attributionCount} attribution{attributionCount !== 1 ? "s" : ""}?</p>
        <RadioGroup.Root bind:value={attributionMode}>
          <div class="flex items-start gap-3">
            <RadioGroup.Item value="clear" id="mode-clear" />
            <Label for="mode-clear" class="cursor-pointer leading-normal">
              <span class="font-medium">Set to "Other"</span>
              <span class="block text-sm text-muted-foreground">
                Keep the attributions but remove the food link.
              </span>
            </Label>
          </div>
          <div class="flex items-start gap-3">
            <RadioGroup.Item value="remove" id="mode-remove" />
            <Label for="mode-remove" class="cursor-pointer leading-normal">
              <span class="font-medium">Remove attributions</span>
              <span class="block text-sm text-muted-foreground">
                Delete the attribution entries entirely.
              </span>
            </Label>
          </div>
        </RadioGroup.Root>
      </div>
    {/if}

    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={onClose} disabled={isLoading}>
        Cancel
      </AlertDialog.Cancel>
      <Button
        variant="destructive"
        disabled={isLoading}
        onclick={() => onConfirm(attributionMode)}
      >
        {#if isLoading}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          Deleting...
        {:else}
          Delete Food
        {/if}
      </Button>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
