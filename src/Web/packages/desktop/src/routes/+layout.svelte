<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { page } from "$app/state";
  import { check, type Update } from "@tauri-apps/plugin-updater";
  import { relaunch } from "@tauri-apps/plugin-process";
  import { Button } from "@nocturne/ui/ui/button";
  import { Download, Loader2 } from "@lucide/svelte";

  let { children } = $props();

  let pending = $state<Update | null>(null);
  let installing = $state(false);

  // The floating clock is a chromeless overlay window — keep the update banner off it.
  const isOverlay = $derived(page.url.pathname.startsWith("/float"));

  onMount(async () => {
    if (isOverlay) return;
    try {
      // Quietly check on launch; surface a non-blocking banner if there's an update.
      const update = await check();
      if (update?.available) pending = update;
    } catch {
      // Offline or no manifest yet — updates are best-effort, never block the app.
    }
  });

  async function installUpdate() {
    if (!pending) return;
    installing = true;
    try {
      await pending.downloadAndInstall();
      await relaunch();
    } catch {
      installing = false;
    }
  }
</script>

{#if pending && !isOverlay}
  <div
    class="bg-primary/10 border-primary/20 flex items-center justify-between gap-3 border-b px-4 py-2 text-sm"
  >
    <span class="flex items-center gap-2">
      <Download class="h-4 w-4" />
      Version {pending.version} is available.
    </span>
    <Button size="sm" onclick={installUpdate} disabled={installing}>
      {#if installing}
        <Loader2 class="mr-1 h-3.5 w-3.5 animate-spin" /> Updating…
      {:else}
        Restart &amp; update
      {/if}
    </Button>
  </div>
{/if}

{@render children()}
