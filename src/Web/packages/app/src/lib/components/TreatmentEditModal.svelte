<script lang="ts">
  import { enhance } from "$app/forms";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import * as Card from "$lib/components/ui/card";
  import type { Treatment } from "$lib/api";
  import { formatDateForInput } from "$lib/utils/formatting";

  // Helper to get API secret from cookie or local storage
  function getAPISecret(): string | null {
    if (typeof document !== "undefined") {
      const cookies = document.cookie.split(";");
      const secretCookie = cookies.find((c) =>
        c.trim().startsWith("api-secret=")
      );
      if (secretCookie) {
        return secretCookie.split("=")[1];
      }
    }
    return null;
  }

  let {
    treatment,
    onSave,
    onCancel,
  }: {
    treatment: Treatment;
    onSave: (treatment: Treatment) => void;
    onCancel: () => void;
  } = $props();
  // svelte-ignore state_referenced_locally
  let editedTreatment = $state<Treatment>({ ...treatment });
  let isSubmitting = $state(false);

  // Update hidden form field when treatment data changes
  let treatmentDataJson = $derived(JSON.stringify(editedTreatment));

  // Event type options
  const eventTypeOptions = [
    "Meal Bolus",
    "Snack Bolus",
    "Correction Bolus",
    "Carb Correction",
    "Note",
    "Exercise",
    "Site Change",
    "Insulin Change",
    "D.A.D. Alert",
    "Sensor Start",
    "Sensor Change",
    "Pump Battery Change",
  ];

  function updateDateTime(value: string) {
    if (value) {
      editedTreatment.created_at = new Date(value).toISOString();
    }
  }
</script>

<div
  class="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4"
>
  <Card.Root class="max-w-2xl w-full max-h-[90vh] overflow-y-auto">
    <Card.Header>
      <Card.Title>Edit Treatment</Card.Title>
      <Card.Description>
        Update the details of this {treatment.eventType} treatment
      </Card.Description>
    </Card.Header>

    <form
      method="POST"
      action="?/updateTreatment"
      use:enhance={() => {
        isSubmitting = true;
        return async ({ result, update }) => {
          isSubmitting = false;
          if (result.type === "success") {
            onSave(editedTreatment);
            onCancel();
          }
          await update();
        };
      }}
    >
      <!-- Hidden fields for form data -->
      <input type="hidden" name="treatmentId" value={treatment._id} />
      <input type="hidden" name="treatmentData" value={treatmentDataJson} />
      <input type="hidden" name="apiSecret" value={getAPISecret() || ""} />

      <Card.Content class="space-y-4">
        <!-- Event Type -->
        <div>
          <Label for="eventType">Event Type</Label>
          <select
            id="eventType"
            bind:value={editedTreatment.eventType}
            class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
          >
            {#each eventTypeOptions as option}
              <option value={option}>{option}</option>
            {/each}
          </select>
        </div>

        <!-- Date and Time -->
        <div>
          <Label for="datetime">Date & Time</Label>
          <Input
            id="datetime"
            type="datetime-local"
            value={formatDateForInput(editedTreatment.created_at)}
            onchange={(e) => updateDateTime(e.currentTarget.value)}
          />
        </div>

        <!-- Insulin -->
        <div>
          <Label for="insulin">Insulin (units)</Label>
          <Input
            id="insulin"
            type="number"
            step="0.1"
            min="0"
            bind:value={editedTreatment.insulin}
            placeholder="0.0"
          />
        </div>

        <!-- Carbs -->
        <div>
          <Label for="carbs">Carbohydrates (grams)</Label>
          <Input
            id="carbs"
            type="number"
            step="1"
            min="0"
            bind:value={editedTreatment.carbs}
            placeholder="0"
          />
        </div>

        <!-- Food -->
        <div>
          <Label for="food">Food Description</Label>
          <Input
            id="food"
            type="text"
            bind:value={editedTreatment.foodType}
            placeholder="e.g., Pizza, Apple, etc."
          />
        </div>

        <!-- Absorption Time -->
        <div>
          <Label for="absorptionTime">Absorption Time (minutes)</Label>
          <Input
            id="absorptionTime"
            type="number"
            step="1"
            min="0"
            bind:value={editedTreatment.absorptionTime}
            placeholder="0"
          />
        </div>

        <!-- Notes -->
        <div>
          <Label for="notes">Notes</Label>
          <Textarea
            id="notes"
            bind:value={editedTreatment.notes}
            placeholder="Additional notes about this treatment..."
            rows={3}
          />
        </div>

        <!-- Reason -->
        <div>
          <Label for="reason">Reason</Label>
          <Input
            id="reason"
            type="text"
            bind:value={editedTreatment.reason}
            placeholder="Reason for this treatment"
          />
        </div>
      </Card.Content>

      <Card.Footer class="flex gap-3">
        <Button
          type="button"
          variant="secondary"
          class="flex-1"
          onclick={onCancel}
          disabled={isSubmitting}
        >
          Cancel
        </Button>
        <Button type="submit" class="flex-1" disabled={isSubmitting}>
          {isSubmitting ? "Saving..." : "Save Changes"}
        </Button>
      </Card.Footer>
    </form>
  </Card.Root>
</div>
