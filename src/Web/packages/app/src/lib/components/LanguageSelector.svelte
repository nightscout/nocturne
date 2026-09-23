<script lang="ts">
  import { Globe } from "@lucide/svelte";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import {
    preferredLanguage,
    supportedLocales,
    getNativeLanguageLabel,
    setLanguage,
    isSupportedLocale,
  } from "$lib/stores/appearance-store.svelte";

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
    if (isSupportedLocale(value)) await setLanguage(value, onLanguageChange);
  }
</script>

<Select type="single" value={preferredLanguage.current} onValueChange={handleChange}>
  <SelectTrigger class={className} size={compact ? "sm" : "default"}>
    {#if compact}
      <Globe class="size-4" />
      <span class="uppercase text-xs font-medium">{preferredLanguage.current}</span>
    {:else}
      <span>{getNativeLanguageLabel(preferredLanguage.current)}</span>
    {/if}
  </SelectTrigger>
  <SelectContent>
    {#each supportedLocales as locale (locale)}
      <SelectItem value={locale}>
        {getNativeLanguageLabel(locale)}
      </SelectItem>
    {/each}
  </SelectContent>
</Select>
