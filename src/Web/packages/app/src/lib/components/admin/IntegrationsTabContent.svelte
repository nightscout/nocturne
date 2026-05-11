<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardFooter,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Eye, EyeOff, Bot } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import type { PlatformSettingsSummary } from "$api";

  const DISPLAY_NAMES: Record<string, string> = {
    discord: "Discord",
    slack: "Slack",
    telegram: "Telegram",
    whatsapp: "WhatsApp",
  };

  let { platforms, onSave } = $props<{
    platforms: PlatformSettingsSummary[];
    onSave: (
      category: string,
      enabled: boolean,
      fields: Record<string, string>
    ) => Promise<void>;
  }>();

  type PlatformState = {
    enabled: boolean;
    fieldValues: Record<string, string>;
    showField: Record<string, boolean>;
    saving: boolean;
  };

  function buildInitialState(platform: PlatformSettingsSummary): PlatformState {
    const fieldValues: Record<string, string> = {};
    const showField: Record<string, boolean> = {};
    for (const field of platform.fields ?? []) {
      fieldValues[field.name ?? ""] = "";
      showField[field.name ?? ""] = false;
    }
    return {
      enabled: platform.enabled ?? false,
      fieldValues,
      showField,
      saving: false,
    };
  }

  let states = $state<Record<string, PlatformState>>(
    Object.fromEntries(
      platforms.map((p) => [p.category ?? "", buildInitialState(p)])
    )
  );

  async function handleSave(platform: PlatformSettingsSummary) {
    const category = platform.category ?? "";
    const state = states[category];
    if (!state) return;

    state.saving = true;
    try {
      await onSave(category, state.enabled, state.fieldValues);
      toast.success("Settings saved. Restart the frontend for changes to take effect.");
    } catch {
      toast.error("Failed to save settings");
    } finally {
      state.saving = false;
    }
  }
</script>

<Tabs.Content value="integrations">
  <div class="space-y-4">
    {#each platforms as platform (platform.category)}
      {@const category = platform.category ?? ""}
      {@const state = states[category]}
      {@const displayName = DISPLAY_NAMES[category] ?? category}
      <Card>
        <CardHeader class="flex flex-row items-center justify-between">
          <div class="flex items-center gap-3">
            <div class="p-2 rounded-lg bg-muted">
              <Bot class="h-5 w-5" />
            </div>
            <CardTitle>{displayName}</CardTitle>
          </div>
          <div class="flex items-center gap-2">
            <Label for="switch-{category}" class="text-sm text-muted-foreground">
              {state.enabled ? "Enabled" : "Disabled"}
            </Label>
            <Switch
              id="switch-{category}"
              checked={state.enabled}
              onCheckedChange={(checked) => (state.enabled = checked)}
            />
          </div>
        </CardHeader>
        <CardContent>
          <div class="space-y-4">
            {#each platform.fields ?? [] as field (field.name)}
              {@const name = field.name ?? ""}
              {@const isConfigured = (platform.configuredFields ?? []).includes(name)}
              <div class="space-y-1.5">
                <Label for="field-{category}-{name}">{field.label ?? name}</Label>
                <div class="relative">
                  <Input
                    id="field-{category}-{name}"
                    type={state.showField[name] ? "text" : "password"}
                    placeholder={isConfigured ? "Configured" : "Not set"}
                    bind:value={state.fieldValues[name]}
                    class="pr-10"
                  />
                  <button
                    type="button"
                    class="absolute inset-y-0 right-0 flex items-center px-3 text-muted-foreground hover:text-foreground"
                    onclick={() => (state.showField[name] = !state.showField[name])}
                    aria-label={state.showField[name] ? "Hide field" : "Show field"}
                  >
                    {#if state.showField[name]}
                      <EyeOff class="h-4 w-4" />
                    {:else}
                      <Eye class="h-4 w-4" />
                    {/if}
                  </button>
                </div>
              </div>
            {/each}
          </div>
        </CardContent>
        <CardFooter class="flex justify-end">
          <Button onclick={() => handleSave(platform)} disabled={state.saving}>
            {state.saving ? "Saving..." : "Save"}
          </Button>
        </CardFooter>
      </Card>
    {/each}
  </div>
</Tabs.Content>
