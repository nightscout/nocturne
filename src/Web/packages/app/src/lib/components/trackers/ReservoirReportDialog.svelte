<script lang="ts">
  import { tick } from "svelte";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import * as Select from "$lib/components/ui/select";
  import { Droplet } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import { create as createReservoirReport } from "$api/generated/reservoirReports.generated.remote";

  type ReportKind = "Reading" | "Fill";

  interface ReservoirReportDialogProps {
    open: boolean;
    /** Preselected kind when opening (e.g. "Fill" from a reservoir tracker row). */
    defaultKind?: ReportKind;
    onClose?: () => void;
    onReported?: () => void;
  }

  let {
    open = $bindable(false),
    defaultKind = "Reading",
    onClose,
    onReported,
  }: ReservoirReportDialogProps = $props();

  let kind = $state<ReportKind>("Reading");
  let units = $state<number | undefined>(undefined);
  let observedAt = $state("");
  let isSubmitting = $state(false);

  const kindLabels: Record<ReportKind, string> = {
    Reading: "Current level",
    Fill: "Fresh fill",
  };

  // Format date for datetime-local input (YYYY-MM-DDTHH:mm)
  function formatDateTimeLocal(date: Date): string {
    const pad = (n: number) => n.toString().padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  // Reset form when dialog opens
  $effect(() => {
    if (open) {
      kind = defaultKind;
      units = undefined;
      observedAt = formatDateTimeLocal(new Date());
    }
  });

  async function handleSubmit() {
    if (!units || units <= 0 || isSubmitting) return;
    isSubmitting = true;
    try {
      await createReservoirReport({
        units,
        kind,
        timestamp: observedAt ? new Date(observedAt).toISOString() : undefined,
        utcOffset: -new Date().getTimezoneOffset(),
        app: "Nocturne",
      });
      toast.success(
        kind === "Fill" ? `Fill of ${units}U recorded` : `Reservoir level of ${units}U recorded`
      );
      open = false;
      await tick();
      onReported?.();
    } catch (err) {
      console.error("Failed to report reservoir value:", err);
      toast.error(err instanceof Error ? err.message : "Failed to record reservoir value");
    } finally {
      isSubmitting = false;
    }
  }

  function handleClose() {
    open = false;
    onClose?.();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Record Reservoir Value</Dialog.Title>
      <Dialog.Description>
        Enter the units shown on your pump, or the amount you just filled into a
        fresh reservoir or pod. A fill also resets the reservoir age.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="reportKind">Type</Label>
        <Select.Root type="single" bind:value={kind}>
          <Select.Trigger id="reportKind">
            {kindLabels[kind]}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="Reading" label={kindLabels.Reading} />
            <Select.Item value="Fill" label={kindLabels.Fill} />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="reservoirUnits">Units</Label>
        <Input
          id="reservoirUnits"
          type="number"
          inputmode="decimal"
          min="0.5"
          max="1000"
          step="0.5"
          placeholder={kind === "Fill" ? "e.g. 85" : "e.g. 62"}
          bind:value={units}
        />
      </div>
      <div class="space-y-2">
        <Label for="observedAt">Observed At</Label>
        <Input id="observedAt" type="datetime-local" bind:value={observedAt} />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleClose} disabled={isSubmitting}>
        Cancel
      </Button>
      <Button onclick={handleSubmit} disabled={isSubmitting || !units || units <= 0}>
        <Droplet class="h-4 w-4 mr-2" />
        Record
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
