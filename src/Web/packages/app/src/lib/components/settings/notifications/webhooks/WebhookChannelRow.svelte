<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Link2, Settings2 } from "lucide-svelte";
  import {
    getWebhookSettings,
    saveWebhookSettings,
    testWebhookSettings,
  } from "$api/notifications/webhooks.remote";
  import WebhookSettingsModal from "./WebhookSettingsModal.svelte";

  const settingsQuery = getWebhookSettings();

  let enabled = $state(false);
  let urls = $state<string[]>([]);
  let hasSecret = $state(false);
  let secret = $state("");
  let showModal = $state(false);
  let status = $state<string | null>(null);
  let testing = $state(false);

  // Form instance (form() returns the form object directly)
  const saveForm = saveWebhookSettings;

  const saving = $derived(saveForm.pending > 0 || testing);

  $effect(() => {
    if (!settingsQuery.current) return;
    enabled = settingsQuery.current.enabled ?? false;
    urls = [...(settingsQuery.current.urls ?? [])];
    hasSecret = settingsQuery.current.hasSecret ?? false;
    secret = settingsQuery.current.secret ?? "";
  });

  function applyResponse(response: any) {
    if (!response) return;
    enabled = response.enabled ?? enabled;
    urls = [...(response.urls ?? [])];
    hasSecret = response.hasSecret ?? hasSecret;
    secret = response.secret ?? secret;
  }

  // Track the intended action when the form is submitted
  let pendingAction = $state<"save" | "disable" | "test">("save");

  let formEl = $state<HTMLFormElement | null>(null);

  // Hidden input values that get updated before form submission
  let formEnabled = $state(true);
  let formUrls = $state<string[]>([]);
  let formSecret = $state("");

  async function handleToggle(nextValue: boolean) {
    enabled = nextValue;

    if (enabled) {
      showModal = true;
      return;
    }

    // Disable: submit form with enabled=false
    pendingAction = "disable";
    formEnabled = false;
    formUrls = urls;
    formSecret = "";
    queueMicrotask(() => {
      formEl?.requestSubmit();
    });
  }

  async function handleModalSave(payload: { urls: string[]; secret: string }) {
    pendingAction = "save";
    formEnabled = enabled;
    formUrls = payload.urls;
    formSecret = payload.secret;
    queueMicrotask(() => {
      formEl?.requestSubmit();
    });
  }

  async function handleModalTest(payload: { urls: string[]; secret: string }) {
    pendingAction = "test";
    formEnabled = enabled;
    formUrls = payload.urls;
    formSecret = payload.secret;
    queueMicrotask(() => {
      formEl?.requestSubmit();
    });
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

<!-- Single form for all save operations -->
<form
  bind:this={formEl}
  class="hidden"
  {...saveForm.enhance(async ({ submit }) => {
    status = null;
    await submit();
    if (saveForm.result) {
      applyResponse(saveForm.result);

      if (pendingAction === "save") {
        status = "Webhook settings saved.";
        showModal = false;
      } else if (pendingAction === "disable") {
        // No status message needed for disable
      } else if (pendingAction === "test") {
        // After saving, run the test command
        testing = true;
        try {
          const response = await testWebhookSettings({
            urls: formUrls,
            secret: (saveForm.result as any)?.secret ?? formSecret ?? null,
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
          testing = false;
        }
      }
    } else {
      if (pendingAction === "disable") {
        console.error("Failed to disable webhook settings");
      } else {
        status = "Failed to save webhook settings.";
      }
    }
  })}
>
  <input type="checkbox" name="b:enabled" checked={formEnabled} class="hidden" />
  {#each formUrls as url}
    <input type="hidden" name="urls[]" value={url} />
  {/each}
  <input type="hidden" name="secret" value={formSecret || ""} />
</form>

<WebhookSettingsModal
  bind:open={showModal}
  bind:urls
  bind:secret
  {hasSecret}
  {saving}
  onSave={handleModalSave}
  onTest={handleModalTest}
  onClose={() => {
    showModal = false;
  }}
/>
