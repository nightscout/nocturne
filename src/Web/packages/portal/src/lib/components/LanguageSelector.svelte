<script lang="ts">
  import { Globe } from "@lucide/svelte";
  import * as Select from "@nocturne/app/ui/select";
  import {
    preferredLanguage,
    supportedLocales,
    getNativeLanguageLabel,
    setLanguage,
    type SupportedLocale,
  } from "@nocturne/app/stores/appearance-store.svelte";

  interface Props {
    /** Compact mode: shows globe icon + language code (for headers) */
    compact?: boolean;
    /** Callback to update backend preference (pass when user is authenticated) */
    onLanguageChange?: (locale: string) => Promise<unknown>;
    /** Additional CSS class */
    class?: string;
  }

  let { compact = false, onLanguageChange, class: className }: Props = $props();

  async function handleChange(value: string | undefined) {
    if (!value) return;
    await setLanguage(value as SupportedLocale, onLanguageChange);
  }
</script>

<Select.Root type="single" value={preferredLanguage.current} onValueChange={handleChange}>
  <Select.Trigger class={className} size={compact ? "sm" : "default"}>
    {#if compact}
      <Globe class="size-4" />
      <span class="uppercase text-xs font-medium">{preferredLanguage.current}</span>
    {:else}
      <span>{getNativeLanguageLabel(preferredLanguage.current)}</span>
    {/if}
  </Select.Trigger>
  <Select.Content>
    {#each supportedLocales as locale}
      <Select.Item value={locale}>
        {getNativeLanguageLabel(locale)}
      </Select.Item>
    {/each}
  </Select.Content>
</Select.Root>