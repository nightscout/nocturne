<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Slider } from "#lib/components/ui/slider/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import * as Popover from "#lib/components/ui/popover/index.js";
  import Settings from "@lucide/svelte/icons/settings";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import type { ClockSettings } from "#lib/api/index.js";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";

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

  // A face that shows alerts hands them to anyone with its link, so only a
  // member who may read alerts can turn it on.
  const canShareAlerts = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.read")
  );

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
      <h4 class="font-medium">Display settings</h4>
      <div class="flex items-center justify-between gap-2">
        <Label>Glucose units</Label>
        <Select.Root
          type="single"
          value={settings.glucoseUnits ?? "mg/dl"}
          onValueChange={(v) => updateSetting("glucoseUnits", v)}
        >
          <Select.Trigger class="w-32">
            {settings.glucoseUnits === "mmol" ? "mmol/L" : "mg/dL"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="mg/dl">mg/dL</Select.Item>
            <Select.Item value="mmol">mmol/L</Select.Item>
          </Select.Content>
        </Select.Root>
      </div>
      <div class="flex items-center justify-between gap-2">
        <Label>Time format</Label>
        <Select.Root
          type="single"
          value={settings.timeFormat ?? "12"}
          onValueChange={(v) => updateSetting("timeFormat", v)}
        >
          <Select.Trigger class="w-32">
            {settings.timeFormat === "24" ? "24-hour" : "12-hour"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="12">12-hour</Select.Item>
            <Select.Item value="24">24-hour</Select.Item>
          </Select.Content>
        </Select.Root>
      </div>
      <Separator />
      <div class="flex items-center justify-between">
        <Label>BG-colored background</Label>
        <Checkbox
          checked={settings.bgColor ?? false}
          onCheckedChange={(v: boolean) => updateSetting("bgColor", !!v)}
          disabled={hasBackgroundImage}
        />
      </div>
      <div class="space-y-2">
        <Label>Background image URL</Label>
        <Input
          type="url"
          placeholder="https://..."
          value={settings.backgroundImage ?? ""}
          oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
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
              onValueChange={(v: number) => updateSetting("backgroundOpacity", v)}
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
          onValueChange={(v: number) => updateSetting("staleMinutes", v)}
          min={0}
          max={60}
          step={1}
        />
      </div>
      <div class="flex items-center justify-between">
        <Label>Always show time</Label>
        <Checkbox
          checked={settings.alwaysShowTime ?? false}
          onCheckedChange={(v: boolean) => updateSetting("alwaysShowTime", !!v)}
        />
      </div>
      {#if canShareAlerts}
        <div class="flex items-center justify-between">
          <Label>Show firing alerts</Label>
          <Checkbox
            checked={settings.showAlerts ?? false}
            onCheckedChange={(v: boolean) =>
              onSettingsChange({
                ...settings,
                showAlerts: !!v,
                allowMarkSeen: v ? settings.allowMarkSeen : false,
              })}
          />
        </div>
        <div class="flex items-center justify-between">
          <Label>Let viewers mark alerts as seen</Label>
          <Checkbox
            checked={settings.allowMarkSeen ?? false}
            onCheckedChange={(v: boolean) => updateSetting("allowMarkSeen", !!v)}
            disabled={!settings.showAlerts}
          />
        </div>
      {/if}
      <div class="flex items-center justify-between">
        <Label>Screensaver mode (bouncing)</Label>
        <Checkbox
          checked={settings.screensaverMode ?? false}
          onCheckedChange={(v: boolean) => updateSetting("screensaverMode", !!v)}
        />
      </div>
      <Separator />
      <Button
        variant="outline"
        size="sm"
        onclick={onReset}
        class="w-full"
      >
        <RotateCcw class="size-3" />
        Reset to default
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
