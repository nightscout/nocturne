<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Activity, Pencil, Syringe, Utensils } from "lucide-svelte";

  interface Props {
    onNavigateGlucose?: () => void;
    onNavigateDelivery?: () => void;
    onNavigateTreatment?: () => void;
    onEditEntry?: () => void;
    onClose: () => void;
  }

  let {
    onNavigateGlucose,
    onNavigateDelivery,
    onNavigateTreatment,
    onEditEntry,
    onClose,
  }: Props = $props();
</script>

<Dialog.Footer class="flex gap-2">
  {#if onNavigateGlucose}
    <Button variant="outline" size="sm" onclick={onNavigateGlucose}>
      <Activity class="mr-1.5 h-4 w-4" />
      Glucose
    </Button>
  {/if}
  {#if onNavigateDelivery}
    <Button variant="outline" size="sm" onclick={onNavigateDelivery}>
      <Syringe class="mr-1.5 h-4 w-4" />
      Delivery
    </Button>
  {/if}
  {#if onNavigateTreatment}
    <Button variant="outline" size="sm" onclick={onNavigateTreatment}>
      <Utensils class="mr-1.5 h-4 w-4" />
      Treatments
    </Button>
  {/if}
  {#if onEditEntry}
    <Button variant="outline" size="sm" onclick={onEditEntry}>
      <Pencil class="mr-1.5 h-4 w-4" />
      Edit
    </Button>
  {/if}
  <Button variant="secondary" size="sm" onclick={onClose}>
    Close
  </Button>
</Dialog.Footer>
