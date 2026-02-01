<script lang="ts">
  import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
  } from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import WebhookSettingsForm from "./WebhookSettingsForm.svelte";

  interface Props {
    open: boolean;
    urls: string[];
    secret: string;
    hasSecret: boolean;
    onSave: (payload: { urls: string[]; secret: string }) => Promise<void>;
    onTest: (payload: { urls: string[]; secret: string }) => Promise<void>;
    onClose: () => void;
    saving?: boolean;
  }

  let {
    open = $bindable(),
    urls = $bindable([]),
    secret = $bindable(""),
    hasSecret,
    onSave,
    onTest,
    onClose,
    saving = false,
  }: Props = $props();
  let hasUrls = $derived(
    urls.filter((url) => url.trim().length > 0).length > 0
  );

  function handleClose() {
    onClose();
  }

  async function handleSave() {
    await onSave({ urls, secret });
  }

  async function handleTest() {
    await onTest({ urls, secret });
  }
</script>

<Dialog bind:open onOpenChange={(value) => !value && handleClose()}>
  <DialogContent class="sm:max-w-2xl">
    <DialogHeader>
      <DialogTitle>Webhook Settings</DialogTitle>
      <DialogDescription>
        Configure URLs and signature secrets for alarm notifications.
      </DialogDescription>
    </DialogHeader>

    <WebhookSettingsForm bind:urls {secret} {hasSecret} disabled={saving} />

    <DialogFooter class="gap-2">
      <Button type="button" variant="outline" onclick={handleClose}>
        Cancel
      </Button>
      <Button
        type="button"
        variant="outline"
        onclick={handleTest}
        disabled={saving || !hasUrls}
      >
        Send Test
      </Button>
      <Button type="button" onclick={handleSave} disabled={saving || !hasUrls}>
        {saving ? "Saving..." : "Save"}
      </Button>
    </DialogFooter>
  </DialogContent>
</Dialog>
