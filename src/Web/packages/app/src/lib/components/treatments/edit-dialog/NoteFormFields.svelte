<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { FileText } from "lucide-svelte";

  interface Props {
    form: {
      text: string;
      eventType: string;
      isAnnouncement: boolean;
    };
  }

  let { form = $bindable() }: Props = $props();
</script>

<div class="space-y-2">
  <Label for="text" class="flex items-center gap-1.5">
    <FileText class="h-3.5 w-3.5" />
    Text
  </Label>
  <Textarea
    id="text"
    bind:value={form.text}
    placeholder="Note text..."
    rows={3}
  />
</div>

<div class="space-y-2">
  <Label for="eventType">Event Type</Label>
  <Input
    id="eventType"
    bind:value={form.eventType}
    placeholder="e.g. Announcement, Note"
  />
</div>

<div class="flex items-center gap-2">
  <Checkbox
    id="isAnnouncement"
    bind:checked={form.isAnnouncement}
  />
  <Label
    for="isAnnouncement"
    class="font-normal cursor-pointer"
  >
    Announcement
  </Label>
</div>
