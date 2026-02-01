<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Slider } from "$lib/components/ui/slider";
  import { Separator } from "$lib/components/ui/separator";
  import * as Popover from "$lib/components/ui/popover";
  import { Settings, RotateCcw } from "lucide-svelte";
  import type { ClockSettings } from "$lib/api/api-client";

  interface Props {
    settings: ClockSettings;
    hasBackgroundImage: boolean;
    open: boolean;
    onOpenChange: (open: boolean) => void;
    onSettingsChange: (settings: ClockSettings) => void;
    onReset: () => void;
  }

  let {
    settings,
    hasBackgroundImage,
    open,
    onOpenChange,
    onSettingsChange,
    onReset,
  }: Props = $props();

  function updateSetting<K extends keyof ClockSettings>(
    key: K,
    value: ClockSettings[K]
  ) {
    onSettingsChange({ ...settings, [key]: value });
  }
</script>

<Popover.Root {open} {onOpenChange}>
  <Popover.Trigger>
    <Button variant="outline" size="icon">
      <Settings class="size-4" />
    </Button>
  </Popover.Trigger>
  <Popover.Content class="w-72" side="bottom" align="end">
    <div class="space-y-4">
      <h4 class="font-medium">Display Settings</h4>
      <div class="flex items-center justify-between">
        <Label>BG-colored background</Label>
        <Checkbox
          checked={settings.bgColor ?? false}
          onCheckedChange={(v) => updateSetting("bgColor", !!v)}
          disabled={hasBackgroundImage}
        />
      </div>
      <div class="space-y-2">
        <Label>Background Image URL</Label>
        <Input
          type="url"
          placeholder="https://..."
          value={settings.backgroundImage ?? ""}
          oninput={(e) =>
            updateSetting(
              "backgroundImage",
              e.currentTarget.value || undefined
            )}
        />
        {#if hasBackgroundImage}
          <div class="space-y-2">
            <Label>
              Image brightness: {settings.backgroundOpacity}%
            </Label>
            <Slider
              type="single"
              value={settings.backgroundOpacity ?? 100}
              onValueChange={(v) => updateSetting("backgroundOpacity", v)}
              min={10}
              max={100}
              step={5}
            />
          </div>
        {/if}
      </div>
      <Separator />
      <div class="space-y-2">
        <Label>
          Stale threshold: {settings.staleMinutes ?? 13} min
        </Label>
        <Slider
          type="single"
          value={settings.staleMinutes ?? 13}
          onValueChange={(v) => updateSetting("staleMinutes", v)}
          min={0}
          max={60}
          step={1}
        />
      </div>
      <div class="flex items-center justify-between">
        <Label>Always show time</Label>
        <Checkbox
          checked={settings.alwaysShowTime ?? false}
          onCheckedChange={(v) => updateSetting("alwaysShowTime", !!v)}
        />
      </div>
      <Separator />
      <Button
        variant="outline"
        size="sm"
        onclick={onReset}
        class="w-full gap-2"
      >
        <RotateCcw class="size-3" />
        Reset to default
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
