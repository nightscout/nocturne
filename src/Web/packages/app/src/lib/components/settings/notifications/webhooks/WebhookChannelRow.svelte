<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Link2, Settings2 } from "lucide-svelte";
  import {
    getWebhookSettings,
    saveWebhookSettings,
    testWebhookSettings,
  } from "$lib/data/notifications/webhooks.remote";
  import WebhookSettingsModal from "./WebhookSettingsModal.svelte";

  const settingsQuery = $derived(getWebhookSettings());

  let enabled = $state(false);
  let urls = $state<string[]>([]);
  let hasSecret = $state(false);
  let secret = $state("");
  let showModal = $state(false);
  let saving = $state(false);
  let status = $state<string | null>(null);

  $effect(() => {
    if (!settingsQuery.current) return;
    enabled = settingsQuery.current.enabled ?? false;
    urls = [...(settingsQuery.current.urls ?? [])];
    hasSecret = settingsQuery.current.hasSecret ?? false;
    secret = settingsQuery.current.secret ?? "";
  });

  async function persistSettings(payload: { urls: string[]; secret: string }) {
    saving = true;
    status = null;
    try {
      const response = await saveWebhookSettings({
        enabled,
        urls: payload.urls,
        secret: payload.secret ? payload.secret : null,
      });
      enabled = response?.enabled ?? enabled;
      urls = [...(response?.urls ?? [])];
      hasSecret = response?.hasSecret ?? hasSecret;
      secret = response?.secret ?? secret;
      status = "Webhook settings saved.";
      showModal = false;
    } catch (err) {
      console.error("Failed to save webhook settings:", err);
      status = "Failed to save webhook settings.";
    } finally {
      saving = false;
    }
  }

  async function testSettings(payload: { urls: string[]; secret: string }) {
    saving = true;
    status = null;

    try {
      // Save first to generate the secret if needed
      const saveResponse = await saveWebhookSettings({
        enabled,
        urls: payload.urls,
        secret: payload.secret ? payload.secret : null,
      });
      enabled = saveResponse?.enabled ?? enabled;
      urls = [...(saveResponse?.urls ?? [])];
      hasSecret = saveResponse?.hasSecret ?? hasSecret;
      secret = saveResponse?.secret ?? secret;

      const response = await testWebhookSettings({
        urls: payload.urls,
        secret: saveResponse?.secret ?? payload.secret ?? null,
      });

      if (response?.ok) {
        status = "Test sent to all webhooks.";
      } else if (response?.failedUrls?.length) {
        status = `Failed: ${response.failedUrls.join(", ")}`;
      } else {
        status = "Test failed.";
      }
    } catch (err) {
      console.error("Failed to test webhook settings:", err);
      status = "Failed to test webhook settings.";
    } finally {
      saving = false;
    }
  }

  async function handleToggle(nextValue: boolean) {
    enabled = nextValue;

    if (enabled) {
      showModal = true;
      return;
    }

    try {
      saving = true;
      const response = await saveWebhookSettings({
        enabled: false,
        urls,
        secret: null,
      });
      enabled = response?.enabled ?? false;
      urls = [...(response?.urls ?? urls)];
      hasSecret = response?.hasSecret ?? hasSecret;
    } catch (err) {
      console.error("Failed to disable webhook settings:", err);
    } finally {
      saving = false;
    }
  }
</script>

<div class="flex items-center justify-between p-3 rounded-lg border">
  <div class="flex items-center gap-3">
    <Link2 class="h-5 w-5 text-muted-foreground" />
    <div>
      <Label>Webhooks</Label>
      <p class="text-sm text-muted-foreground">
        Send alert events to external services
      </p>
    </div>
  </div>
  <div class="flex items-center gap-2">
    {#if enabled}
      <Button
        type="button"
        variant="outline"
        size="sm"
        class="gap-1"
        onclick={() => (showModal = true)}
      >
        <Settings2 class="h-4 w-4" />
        Edit
      </Button>
    {/if}
    <Switch checked={enabled} onCheckedChange={handleToggle} disabled={saving} />
  </div>
</div>
{#if status}
  <div class="text-xs text-muted-foreground">{status}</div>
{/if}

<WebhookSettingsModal
  bind:open={showModal}
  bind:urls
  bind:secret
  {hasSecret}
  {saving}
  onSave={persistSettings}
  onTest={testSettings}
  onClose={() => {
    showModal = false;
  }}
/>
