<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { toast } from "svelte-sonner";
  import * as mealMatchingRemote from "$lib/data/meal-matching.remote";
  import type {
    InAppNotificationDto,
    ConnectorFoodEntry,
    SuggestedMealMatch,
  } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    notification?: InAppNotificationDto | null;
    match?: SuggestedMealMatch | null;
    onComplete?: () => void;
  }

  let {
    open = $bindable(),
    onOpenChange,
    notification = null,
    match = null,
    onComplete,
  }: Props = $props();

  // Form state
  let carbs = $state<number>(0);
  let selectedTime = $state<string>(""); // HH:mm format for time input
  let isLoading = $state(false);
  let foodEntry = $state<ConnectorFoodEntry | null>(null);

  // Extract data from notification OR match
  const treatmentId = $derived(
    match?.treatmentId ??
      notification?.metadata?.["treatmentId"]?.toString() ??
      ""
  );
  const treatmentCarbs = $derived(
    match?.treatmentCarbs ?? (Number(notification?.metadata?.["treatmentCarbs"]) || 0)
  );
  const treatmentMills = $derived(
    match?.treatmentMills ?? (Number(notification?.metadata?.["treatmentMills"]) || 0)
  );
  const foodEntryCarbs = $derived(
    match?.carbs ?? (Number(notification?.metadata?.["foodEntryCarbs"]) || 0)
  );
  const consumedAtMills = $derived(
    match?.consumedAt
      ? new Date(match.consumedAt).getTime()
      : Number(notification?.metadata?.["consumedAtMills"]) || 0
  );
  const foodEntryId = $derived(match?.foodEntryId ?? notification?.sourceId ?? "");

  // Get food name from match (immediately) or foodEntry (after load)
  const foodName = $derived(
    foodEntry?.food?.name ?? foodEntry?.mealName ?? match?.foodName ?? match?.mealName ?? "Food"
  );

  // Format time as HH:mm for time input
  function formatTimeInput(mills: number): string {
    if (!mills) return "";
    const date = new Date(mills);
    const hours = date.getHours().toString().padStart(2, "0");
    const minutes = date.getMinutes().toString().padStart(2, "0");
    return `${hours}:${minutes}`;
  }

  // Format date as YYYY-MM-DD for date input
  function formatDateInput(mills: number): string {
    if (!mills) return "";
    const date = new Date(mills);
    const year = date.getFullYear();
    const month = (date.getMonth() + 1).toString().padStart(2, "0");
    const day = date.getDate().toString().padStart(2, "0");
    return `${year}-${month}-${day}`;
  }

  // Format date for display (e.g., "Jan 23")
  function formatDateDisplay(mills: number): string {
    if (!mills) return "";
    const date = new Date(mills);
    return date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  }

  // Track selected date separately
  let selectedDate = $state<string>("");

  // Derived times for comparison
  const bolusTimeInput = $derived(formatTimeInput(treatmentMills));
  const loggedTimeInput = $derived(formatTimeInput(consumedAtMills));
  const bolusDateInput = $derived(formatDateInput(treatmentMills));
  const bolusDateDisplay = $derived(formatDateDisplay(treatmentMills));

  // Check which preset is selected (if any)
  const isBolusTime = $derived(selectedTime === bolusTimeInput);
  const isLoggedTime = $derived(selectedTime === loggedTimeInput && loggedTimeInput !== bolusTimeInput);

  // Calculate offset minutes from selected date and time
  const timeOffsetMinutes = $derived(() => {
    if (!treatmentMills || !selectedTime || !selectedDate) return 0;
    const [hours, minutes] = selectedTime.split(":").map(Number);
    const [year, month, day] = selectedDate.split("-").map(Number);
    const customDate = new Date(year, month - 1, day, hours, minutes, 0, 0);
    return Math.round((customDate.getTime() - treatmentMills) / 60000);
  });

  // Initialize form when dialog opens
  $effect(() => {
    if (open && (notification || match)) {
      carbs = foodEntryCarbs;
      selectedTime = formatTimeInput(treatmentMills);
      selectedDate = formatDateInput(treatmentMills);
      loadFoodEntry();
    }
  });

  async function loadFoodEntry() {
    if (!foodEntryId) return;
    try {
      foodEntry = await mealMatchingRemote.getFoodEntry({ id: foodEntryId });
    } catch (err) {
      console.error("Failed to load food entry:", err);
    }
  }

  function resetAndClose() {
    carbs = 0;
    selectedTime = "";
    selectedDate = "";
    foodEntry = null;
    isLoading = false;
    open = false;
    onOpenChange(false);
  }

  async function handleAccept() {
    if (!foodEntryId || !treatmentId) {
      toast.error("Missing required data");
      return;
    }

    isLoading = true;
    try {
      await mealMatchingRemote.acceptMatch({
        foodEntryId,
        treatmentId,
        carbs,
        timeOffsetMinutes: timeOffsetMinutes(),
      });
      toast.success("Meal match accepted");
      onComplete?.();
      resetAndClose();
    } catch (err) {
      console.error("Failed to accept meal match:", err);
      toast.error("Failed to accept meal match");
    } finally {
      isLoading = false;
    }
  }

  async function handleDismiss() {
    if (!foodEntryId) {
      toast.error("Missing food entry ID");
      return;
    }

    isLoading = true;
    try {
      await mealMatchingRemote.dismissMatch({ foodEntryId });
      toast.success("Meal match dismissed");
      onComplete?.();
      resetAndClose();
    } catch (err) {
      console.error("Failed to dismiss meal match:", err);
      toast.error("Failed to dismiss meal match");
    } finally {
      isLoading = false;
    }
  }

  // Calculate scale factor based on carbs adjustment
  const scaleFactor = $derived(() => {
    const originalCarbs = foodEntry?.carbs ?? foodEntryCarbs;
    if (!originalCarbs || originalCarbs === 0) return 1;
    return carbs / originalCarbs;
  });

  // Scaled values for display
  const scaledServings = $derived(() => {
    const original = foodEntry?.servings ?? 1;
    const factor = scaleFactor();
    return Math.round(original * factor * 100) / 100;
  });

  const scaledProtein = $derived(() => {
    const original = foodEntry?.protein ?? 0;
    const factor = scaleFactor();
    return Math.round(original * factor);
  });

  const scaledFat = $derived(() => {
    const original = foodEntry?.fat ?? 0;
    const factor = scaleFactor();
    return Math.round(original * factor);
  });

  // Check if values have been scaled
  const isScaled = $derived(() => {
    const originalCarbs = foodEntry?.carbs ?? foodEntryCarbs;
    return carbs !== originalCarbs;
  });
</script>

<Dialog.Root bind:open onOpenChange={(value) => !value && resetAndClose()}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Confirm "{foodName}"</Dialog.Title>
      <Dialog.Description>
        When did you eat this, and how much?
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-6">
      {#if foodEntry}
        <div class="rounded-lg border p-3">
          <div class="font-medium">
            {foodEntry.food?.name ?? foodEntry.mealName ?? "Food entry"}
          </div>
          {#if foodEntry.servingDescription}
            <div class="text-sm text-muted-foreground">
              {#if isScaled()}
                <span class="line-through opacity-50">{foodEntry.servings ?? 1}</span>
                <span class="font-medium text-foreground">{scaledServings()}</span>
              {:else}
                {foodEntry.servings ?? 1}
              {/if}
              x {foodEntry.servingDescription}
            </div>
          {/if}
          <div class="mt-1 text-sm text-muted-foreground">
            {#if isScaled()}
              <span class="line-through opacity-50">{foodEntry.carbs}g</span>
              <span class="font-medium text-foreground">{carbs}g</span> carbs
            {:else}
              {foodEntry.carbs}g carbs
            {/if}
            {#if foodEntry.protein}
              · {#if isScaled()}
                <span class="line-through opacity-50">{foodEntry.protein}g</span>
                <span class="font-medium text-foreground">{scaledProtein()}g</span>
              {:else}
                {foodEntry.protein}g
              {/if} protein
            {/if}
            {#if foodEntry.fat}
              · {#if isScaled()}
                <span class="line-through opacity-50">{foodEntry.fat}g</span>
                <span class="font-medium text-foreground">{scaledFat()}g</span>
              {:else}
                {foodEntry.fat}g
              {/if} fat
            {/if}
          </div>
        </div>
      {/if}

      <div class="space-y-2">
        <Label for="carbs">Carbs (g)</Label>
        <div class="flex items-center gap-2">
          <Input
            id="carbs"
            type="number"
            step="1"
            min="0"
            bind:value={carbs}
            class="tabular-nums"
          />
          {#if treatmentCarbs > 0 && carbs !== treatmentCarbs}
            <Button
              type="button"
              variant="outline"
              size="sm"
              onclick={() => (carbs = treatmentCarbs)}
            >
              Scale to {treatmentCarbs}g
            </Button>
          {/if}
        </div>
        <p class="text-xs text-muted-foreground">
          Treatment has {treatmentCarbs}g total carbs
        </p>
      </div>

      <div class="space-y-2">
        <Label for="eat-time">When did you eat?</Label>
        <div class="flex items-center gap-2">
          <Input
            id="eat-date"
            type="date"
            bind:value={selectedDate}
            class="w-36"
          />
          <Input
            id="eat-time"
            type="time"
            bind:value={selectedTime}
            class="w-28"
          />
          {#if timeOffsetMinutes() !== 0}
            <span class="text-sm text-muted-foreground">
              ({timeOffsetMinutes() > 0 ? "+" : ""}{timeOffsetMinutes()} min)
            </span>
          {/if}
        </div>
        <div class="flex items-center gap-2 mt-2">
          <Button
            type="button"
            variant={isBolusTime && selectedDate === bolusDateInput ? "default" : "outline"}
            size="sm"
            onclick={() => {
              selectedTime = bolusTimeInput;
              selectedDate = bolusDateInput;
            }}
          >
            Bolus time ({bolusDateDisplay})
          </Button>
          {#if loggedTimeInput && loggedTimeInput !== bolusTimeInput}
            <Button
              type="button"
              variant={isLoggedTime ? "default" : "outline"}
              size="sm"
              onclick={() => (selectedTime = loggedTimeInput)}
            >
              Logged time
            </Button>
          {/if}
        </div>
      </div>
    </div>

    <Dialog.Footer class="gap-2 sm:gap-0">
      <Button
        type="button"
        variant="outline"
        onclick={handleDismiss}
        disabled={isLoading}
      >
        Dismiss
      </Button>
      <div class="flex-1"></div>
      <Button type="button" variant="outline" onclick={resetAndClose}>
        Cancel
      </Button>
      <Button type="button" onclick={handleAccept} disabled={isLoading}>
        {isLoading ? "Saving..." : "Accept"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
