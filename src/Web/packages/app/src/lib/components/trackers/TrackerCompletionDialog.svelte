<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Check } from "lucide-svelte";
  import { CompletionReason } from "$api";
  import * as trackersRemote from "$lib/data/trackers.remote";

  interface TrackerCompletionDialogProps {
    open: boolean;
    instanceId: string | null;
    instanceName?: string;
    onClose: () => void;
    onComplete?: () => void;
  }

  let {
    open = $bindable(false),
    instanceId,
    instanceName = "tracker",
    onClose,
    onComplete,
  }: TrackerCompletionDialogProps = $props();

  let completionReason = $state<CompletionReason>(CompletionReason.Completed);
  let completionNotes = $state("");
  let isSubmitting = $state(false);

  // Completion reason labels
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell Off",
    [CompletionReason.ReplacedEarly]: "Replaced Early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Cancelled",
    [CompletionReason.Missed]: "Missed",
  };

  // Reset form when dialog opens
  $effect(() => {
    if (open) {
      completionReason = CompletionReason.Completed;
      completionNotes = "";
    }
  });

  async function handleComplete() {
    if (!instanceId) return;
    isSubmitting = true;
    try {
      await trackersRemote.completeInstance({
        id: instanceId,
        request: {
          reason: completionReason,
          completionNotes: completionNotes || undefined,
        },
      });
      open = false;
      onComplete?.();
    } catch (err) {
      console.error("Failed to complete tracker:", err);
    } finally {
      isSubmitting = false;
    }
  }

  function handleClose() {
    open = false;
    onClose();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Complete {instanceName}</Dialog.Title>
      <Dialog.Description>
        Mark this tracker as complete with an optional reason and notes.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="reason">Completion Reason</Label>
        <Select.Root type="single" bind:value={completionReason}>
          <Select.Trigger>
            {completionReasonLabels[completionReason]}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value={CompletionReason.Completed} label="Completed" />
            <Select.Item value={CompletionReason.Expired} label="Expired" />
            <Select.Item value={CompletionReason.Failed} label="Failed" />
            <Select.Item value={CompletionReason.FellOff} label="Fell Off" />
            <Select.Item
              value={CompletionReason.ReplacedEarly}
              label="Replaced Early"
            />
            <Select.Item value={CompletionReason.Empty} label="Empty" />
            <Select.Item value={CompletionReason.Refilled} label="Refilled" />
            <Select.Item value={CompletionReason.Attended} label="Attended" />
            <Select.Item
              value={CompletionReason.Rescheduled}
              label="Rescheduled"
            />
            <Select.Item value={CompletionReason.Cancelled} label="Cancelled" />
            <Select.Item value={CompletionReason.Missed} label="Missed" />
            <Select.Item value={CompletionReason.Other} label="Other" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="completionNotes">Notes (optional)</Label>
        <Textarea
          id="completionNotes"
          bind:value={completionNotes}
          placeholder="e.g., Sensor error E2 on day 8"
        />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleClose} disabled={isSubmitting}>
        Cancel
      </Button>
      <Button onclick={handleComplete} disabled={isSubmitting}>
        <Check class="h-4 w-4 mr-2" />
        Complete
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
