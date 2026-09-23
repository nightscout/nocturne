<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Apple } from "lucide-svelte";

  interface Props {
    form: {
      carbs: number | null | undefined;
      absorptionTime: number | undefined;
      carbTime: number | undefined;
    };
  }

  let { form = $bindable() }: Props = $props();
</script>

<div class="grid grid-cols-3 gap-4">
  <div class="space-y-2">
    <Label for="carbs">
      <Apple class="h-3.5 w-3.5 text-entry-carbs" />
      Carbs (g)
    </Label>
    <Input
      id="carbs"
      type="number"
      step="1"
      min="0"
      bind:value={form.carbs}
    />
  </div>
</div>

<div class="grid grid-cols-2 gap-4">
  <div class="space-y-2">
    <Label for="absorptionTime">Absorption Time (min)</Label>
    <Input
      id="absorptionTime"
      type="number"
      step="1"
      min="0"
      bind:value={form.absorptionTime}
      placeholder="—"
    />
  </div>
  <div class="space-y-2">
    <Label for="carbTime">Carb Time (min)</Label>
    <Input
      id="carbTime"
      type="number"
      step="1"
      bind:value={form.carbTime}
      placeholder="—"
    />
  </div>
</div>
