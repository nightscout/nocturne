<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import { AlertTriangle } from "lucide-svelte";
  import { AlertRuleSeverity } from "$api-clients";
  import type { DeviceCapabilityCatalog } from "$api-clients";
  import type { ChannelDef } from "./types";
  import { deviceKindLabel } from "$lib/utils/device-kind-labels";

  interface Props {
    /** The single device_action channel being edited. */
    channel: ChannelDef;
    /** The server-owned capability catalog (kinds + capabilities). */
    catalog: DeviceCapabilityCatalog | null;
    /** The rule's severity — drives the non-critical hardware warning. */
    severity: AlertRuleSeverity;
    /** Index used to scope element ids within the channel list. */
    index: number;
  }

  let { channel = $bindable(), catalog, severity, index }: Props = $props();

  const kinds = $derived<string[]>(catalog?.kinds ?? []);

  // The selected kind is the channel's destination. Empty until one is chosen.
  const selectedKind = $derived(channel.destination ?? "");

  // Capabilities the catalog allows for the chosen kind.
  const kindCapabilities = $derived(
    (catalog?.capabilities ?? []).filter(
      (c) => selectedKind !== "" && (c.kinds ?? []).includes(selectedKind),
    ),
  );

  const selectedCapabilities = $derived<string[]>(
    channel.metadata?.capabilities ?? [],
  );

  function isSelected(key: string): boolean {
    return selectedCapabilities.includes(key);
  }

  // True when any selected capability is a hardware actuator. Used for the
  // non-critical soft warning.
  const hasHardwareSelected = $derived(
    (catalog?.capabilities ?? []).some(
      (c) => c.isHardware === true && selectedCapabilities.includes(c.key ?? ""),
    ),
  );

  const showHardwareWarning = $derived(
    hasHardwareSelected && severity !== AlertRuleSeverity.Critical,
  );

  function selectKind(kind: string): void {
    channel.destination = kind;
    // Drop any capabilities the new kind doesn't expose, then keep `notify`
    // checked if the new kind offers it.
    const allowed = new Set(
      (catalog?.capabilities ?? [])
        .filter((c) => (c.kinds ?? []).includes(kind))
        .map((c) => c.key ?? ""),
    );
    const kept = selectedCapabilities.filter((k) => allowed.has(k));
    if (allowed.has("notify") && !kept.includes("notify")) kept.unshift("notify");
    channel.metadata = { capabilities: kept };
  }

  function toggleCapability(key: string, checked: boolean): void {
    const current = new Set(selectedCapabilities);
    if (checked) current.add(key);
    else current.delete(key);
    channel.metadata = { capabilities: [...current] };
  }
</script>

<div class="space-y-3">
  <div class="space-y-1.5">
    <Label class="text-xs" for="device-kind-{index}">Device kind</Label>
    <Select.Root
      type="single"
      value={selectedKind}
      onValueChange={(v) => selectKind(v)}
    >
      <Select.Trigger
        id="device-kind-{index}"
        class="h-8 w-48 text-sm"
        data-testid="device-kind-trigger"
      >
        {selectedKind ? deviceKindLabel(selectedKind) : "Select a kind"}
      </Select.Trigger>
      <Select.Content>
        {#each kinds as kind (kind)}
          <Select.Item value={kind} label={deviceKindLabel(kind)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <p class="text-xs text-muted-foreground">
      Targets every registered device of this kind.
    </p>
  </div>

  {#if selectedKind}
    <div class="space-y-2">
      <Label class="text-xs">Capabilities</Label>
      {#if kindCapabilities.length === 0}
        <p class="text-xs text-muted-foreground">
          No capabilities are available for this kind.
        </p>
      {:else}
        <div class="space-y-2">
          {#each kindCapabilities as cap (cap.key)}
            {@const key = cap.key ?? ""}
            <div class="flex items-start gap-2">
              <Checkbox
                id="device-cap-{index}-{key}"
                checked={isSelected(key)}
                onCheckedChange={(c: boolean) => toggleCapability(key, !!c)}
                data-testid="device-cap-{key}"
              />
              <div class="grid gap-0.5 leading-none">
                <Label
                  class="text-sm font-normal"
                  for="device-cap-{index}-{key}"
                >
                  {cap.label}
                </Label>
                {#if cap.isHardware}
                  <span
                    class="text-[11px] text-muted-foreground"
                    data-testid="device-cap-hardware-note"
                  >
                    Device must allow this.
                  </span>
                {/if}
              </div>
            </div>
          {/each}
        </div>
      {/if}

      {#if showHardwareWarning}
        <div
          class="flex items-start gap-2 rounded-md border border-status-warning/40 bg-status-warning/10 p-2 text-xs text-status-warning"
          data-testid="device-hardware-warning"
        >
          <AlertTriangle class="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <span>
            Hardware actuators on a non-critical alert can be disruptive.
          </span>
        </div>
      {/if}
    </div>
  {/if}
</div>
