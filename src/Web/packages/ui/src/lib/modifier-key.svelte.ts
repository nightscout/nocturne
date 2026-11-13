import { onMount } from "svelte";

type PlatformSource = {
  platform?: string;
  userAgentData?: { platform?: string };
};

export function isMacPlatform(source: PlatformSource): boolean {
  const platform = source.userAgentData?.platform || source.platform || "";
  return /mac/i.test(platform);
}

/**
 * Reads `false` on the server and during hydration so both render the same label,
 * then the real platform once mounted. Call during component initialisation.
 */
export function useModifierKey() {
  let isMac = $state(false);

  onMount(() => {
    isMac = isMacPlatform(navigator);
  });

  return {
    get isMac() {
      return isMac;
    },
    get label() {
      return isMac ? "⌘" : "Ctrl";
    },
  };
}
