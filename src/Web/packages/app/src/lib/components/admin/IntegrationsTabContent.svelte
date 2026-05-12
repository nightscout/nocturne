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
  import { Badge } from "$lib/components/ui/badge";
  import { Bot, Trash2 } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import type { PlatformSettingsSummary } from "$api";

  const DISPLAY_NAMES: Record<string, string> = {
    discord: "Discord",
    slack: "Slack",
    telegram: "Telegram",
    whatsapp: "WhatsApp",
  };

  let { platforms, onSave, onDelete } = $props<{
    platforms: PlatformSettingsSummary[];
    onSave: (
      category: string,
      enabled: boolean,
      fields: Record<string, string>
    ) => Promise<void>;
    onDelete: (category: string) => Promise<void>;
  }>();

  type PlatformState = {
    enabled: boolean;
    fieldValues: Record<string, string>;
    saving: boolean;
    deleting: boolean;
  };

  function buildInitialState(platform: PlatformSettingsSummary): PlatformState {
    const fieldValues: Record<string, string> = {};
    for (const field of platform.fields ?? []) {
      fieldValues[field.name ?? ""] = "";
    }
    return {
      enabled: platform.enabled ?? false,
      fieldValues,
      saving: false,
      deleting: false,
    };
  }

  let states = $state<Record<string, PlatformState>>(
    Object.fromEntries(
      platforms.map((p) => [p.category ?? "", buildInitialState(p)])
    )
  );

  function hasAnyConfiguredFields(platform: PlatformSettingsSummary): boolean {
    return (platform.configuredFields ?? []).length > 0;
  }

  async function handleSave(platform: PlatformSettingsSummary) {
    const category = platform.category ?? "";
    const state = states[category];
    if (!state) return;

    state.saving = true;
    try {
      await onSave(category, state.enabled, state.fieldValues);
      // Clear field values after successful save (secrets are stored, not echoed)
      for (const key of Object.keys(state.fieldValues)) {
        state.fieldValues[key] = "";
      }
      toast.success("Settings saved. Restart the frontend for changes to take effect.");
    } catch {
      toast.error("Failed to save settings");
    } finally {
      state.saving = false;
    }
  }

  async function handleDelete(platform: PlatformSettingsSummary) {
    const category = platform.category ?? "";
    const displayName = DISPLAY_NAMES[category] ?? category;
    if (!confirm(`Remove all ${displayName} credentials? This cannot be undone.`)) return;

    const state = states[category];
    if (!state) return;

    state.deleting = true;
    try {
      await onDelete(category);
      state.enabled = false;
      for (const key of Object.keys(state.fieldValues)) {
        state.fieldValues[key] = "";
      }
      toast.success(`${displayName} configuration removed.`);
    } catch {
      toast.error("Failed to remove settings");
    } finally {
      state.deleting = false;
    }
  }
</script>

<Tabs.Content value="integrations">
  <div class="space-y-4">
    {#each platforms as platform (platform.category)}
      {@const category = platform.category ?? ""}
      {@const state = states[category]}
      {@const displayName = DISPLAY_NAMES[category] ?? category}
      {@const isConfigured = hasAnyConfiguredFields(platform)}
      <Card>
        <CardHeader class="flex flex-row items-center justify-between">
          <div class="flex items-center gap-3">
            <div class="p-2 rounded-lg bg-muted">
              <Bot class="h-5 w-5" />
            </div>
            <CardTitle>{displayName}</CardTitle>
            {#if isConfigured}
              <Badge variant="secondary">Configured</Badge>
            {/if}
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
              {@const fieldConfigured = (platform.configuredFields ?? []).includes(name)}
              <div class="space-y-1.5">
                <div class="flex items-center gap-2">
                  <Label for="field-{category}-{name}">{field.label ?? name}</Label>
                  {#if fieldConfigured}
                    <Badge variant="outline" class="text-xs">Set</Badge>
                  {/if}
                </div>
                <Input
                  id="field-{category}-{name}"
                  type="password"
                  placeholder={fieldConfigured ? "Leave blank to keep current value" : "Not set"}
                  bind:value={state.fieldValues[name]}
                />
              </div>
            {/each}
          </div>
        </CardContent>
        <CardFooter class="flex justify-between">
          {#if isConfigured}
            <Button
              variant="destructive"
              size="sm"
              onclick={() => handleDelete(platform)}
              disabled={state.deleting || state.saving}
            >
              <Trash2 class="h-4 w-4 mr-1.5" />
              {state.deleting ? "Removing..." : "Remove"}
            </Button>
          {:else}
            <div></div>
          {/if}
          <Button onclick={() => handleSave(platform)} disabled={state.saving || state.deleting}>
            {state.saving ? "Saving..." : "Save"}
          </Button>
        </CardFooter>
      </Card>
    {/each}
  </div>
</Tabs.Content>
