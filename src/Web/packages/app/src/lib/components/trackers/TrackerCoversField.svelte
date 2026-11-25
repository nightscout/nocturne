<script lang="ts">
  import { PumpCapability } from "#lib/api/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import type { TrackerDraft } from "./draft";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();

  const capabilityLabels: Record<PumpCapability, string> = {
    [PumpCapability.Site]: "Infusion site",
    [PumpCapability.Reservoir]: "Insulin",
    [PumpCapability.Power]: "Pump battery",
  };

  function toggle(capability: PumpCapability, checked: boolean) {
    draft.covers = checked
      ? [...draft.covers, capability]
      : draft.covers.filter((c) => c !== capability);
  }
</script>

<div class="space-y-3" data-testid="tracker-covers">
  <div>
    <Label>Replaced with this part</Label>
    <p class="text-xs text-muted-foreground">
      Select what is new each time this tracker restarts. For a pod, select
      all three.
    </p>
  </div>
  <div class="grid grid-cols-2 gap-2">
    {#each Object.values(PumpCapability) as capability (capability)}
      <div class="flex items-center gap-2">
        <Checkbox
          id="covers-{capability}"
          checked={draft.covers.includes(capability)}
          onCheckedChange={(checked) => toggle(capability, checked === true)}
        />
        <Label for="covers-{capability}" variant="option" class="cursor-pointer">
          {capabilityLabels[capability]}
        </Label>
      </div>
    {/each}
  </div>
</div>
