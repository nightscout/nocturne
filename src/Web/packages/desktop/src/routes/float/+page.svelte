<script lang="ts">
  import { onMount } from "svelte";
  import { invoke } from "@tauri-apps/api/core";
  import { getCurrentWindow } from "@tauri-apps/api/window";
  import { Pin, PinOff, X } from "@lucide/svelte";
  import { preferences } from "$lib/preferences.svelte";

  // The floating window hosts the web app's public clock page so the clock face renders from one
  // place (no native re-implementation). This component only owns the window chrome: a full-window
  // overlay that drags the borderless window and closes it on double-click, plus hover controls for
  // opacity and always-on-top. The clock is display-only, so the overlay can sit over the iframe
  // and own the mouse without stealing any interaction the clock needs.

  const win = getCurrentWindow();

  let serverUrl = $state<string | null>(null);
  let message = $state<string | null>(null);
  let opacity = $state(preferences.floatOpacity);
  let alwaysOnTop = $state(preferences.floatAlwaysOnTop);

  const src = $derived(
    serverUrl && preferences.clockId
      ? `${serverUrl}/clock/${preferences.clockId}?embed=1`
      : null,
  );

  onMount(async () => {
    // The window is transparent; clear the app's default body background so reduced opacity shows
    // the desktop behind the clock rather than an opaque panel.
    document.body.style.background = "transparent";

    if (!preferences.clockId) {
      message = "No clock selected. Choose one in Settings.";
    }
    try {
      serverUrl = await invoke<string | null>("companion_server_url");
      if (!serverUrl) message = "Not connected to a Nocturne server.";
    } catch {
      message = "Could not read the server address.";
    }

    // Restore the persisted always-on-top choice (the window is built on-top by default).
    await win.setAlwaysOnTop(alwaysOnTop);
  });

  function setOpacity(value: number) {
    opacity = value;
    preferences.floatOpacity = value;
  }

  async function toggleAlwaysOnTop() {
    alwaysOnTop = !alwaysOnTop;
    preferences.floatAlwaysOnTop = alwaysOnTop;
    await win.setAlwaysOnTop(alwaysOnTop);
  }

  async function close() {
    await win.close();
  }
</script>

<svelte:head>
  <title>Nocturne Glucose</title>
</svelte:head>

<div class="group fixed inset-0 overflow-hidden bg-transparent">
  {#if src}
    <!-- Opacity applies to the clock only; the hover controls below stay fully opaque. -->
    <div class="absolute inset-0" style="opacity: {opacity}">
      <iframe
        {src}
        title="Nocturne glucose clock"
        class="h-full w-full border-0 bg-transparent"
      ></iframe>
    </div>
  {:else}
    <div
      class="absolute inset-0 flex items-center justify-center bg-neutral-950 p-4 text-center text-sm text-white/70"
    >
      {message ?? "Loading…"}
    </div>
  {/if}

  <!-- Drag anywhere to move; double-click anywhere to close. -->
  <div
    class="absolute inset-0"
    data-tauri-drag-region
    ondblclick={close}
    role="presentation"
  ></div>

  <!-- Controls, revealed on hover, stacked above the drag overlay so they stay clickable. While
       hidden they're click-through, leaving the corner draggable. -->
  <div
    class="pointer-events-none absolute right-2 top-2 flex items-center gap-2 rounded-full bg-black/55 px-2.5 py-1.5 opacity-0 backdrop-blur transition-opacity duration-200 group-hover:pointer-events-auto group-hover:opacity-100"
  >
    <input
      type="range"
      min="0.3"
      max="1"
      step="0.05"
      value={opacity}
      oninput={(e) => setOpacity(Number(e.currentTarget.value))}
      aria-label="Opacity"
      class="h-1 w-20 cursor-pointer"
    />
    <button
      type="button"
      onclick={toggleAlwaysOnTop}
      title={alwaysOnTop ? "Always on top: on" : "Always on top: off"}
      aria-label="Toggle always on top"
      class="text-white/80 hover:text-white"
    >
      {#if alwaysOnTop}
        <Pin class="h-4 w-4" />
      {:else}
        <PinOff class="h-4 w-4" />
      {/if}
    </button>
    <button
      type="button"
      onclick={close}
      title="Close (or double-click anywhere)"
      aria-label="Close"
      class="text-white/80 hover:text-white"
    >
      <X class="h-4 w-4" />
    </button>
  </div>
</div>
