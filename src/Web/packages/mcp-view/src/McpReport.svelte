<!--
  The view's connection to the MCP Apps host. The tool result arrives as a notification; the only
  call to the host is the request to change display mode, offered when the host lists both modes.
-->
<script lang="ts">
  import {
    App,
    applyDocumentTheme,
    applyHostFonts,
    applyHostStyleVariables,
    type McpUiHostContext,
  } from "@modelcontextprotocol/ext-apps";
  import Maximize2 from "@lucide/svelte/icons/maximize-2";
  import Minimize2 from "@lucide/svelte/icons/minimize-2";
  import { Button } from "@nocturne/ui/ui/button";
  import ReportView from "./ReportView.svelte";

  type DisplayMode = "inline" | "fullscreen";

  let result = $state.raw<{ structuredContent?: unknown } | null>(null);
  let locale = $state(navigator.language);
  let connected = $state(true);
  let displayMode = $state<DisplayMode>("inline");
  let canFullscreen = $state(false);

  function applyHostContext(context: McpUiHostContext | undefined) {
    if (!context) return;
    if (context.theme) {
      applyDocumentTheme(context.theme);
      document.documentElement.classList.toggle("dark", context.theme === "dark");
    }
    if (context.styles?.variables) applyHostStyleVariables(context.styles.variables);
    if (context.styles?.css?.fonts) applyHostFonts(context.styles.css.fonts);
    if (context.locale) locale = context.locale;
    if (context.displayMode === "inline" || context.displayMode === "fullscreen") displayMode = context.displayMode;
    if (context.availableDisplayModes)
      canFullscreen = context.availableDisplayModes.includes("fullscreen") && context.availableDisplayModes.includes("inline");
  }

  const app = new App(
    { name: "Nocturne report", version: "0.2.0" },
    { availableDisplayModes: ["inline", "fullscreen"] },
  );
  app.ontoolresult = (params) => {
    result = params;
  };
  app.onhostcontextchanged = applyHostContext;
  app.connect().then(
    () => applyHostContext(app.getHostContext()),
    () => (connected = false),
  );

  async function toggleDisplayMode() {
    const next: DisplayMode = displayMode === "inline" ? "fullscreen" : "inline";
    const granted = await app.requestDisplayMode({ mode: next });
    if (granted.mode === "inline" || granted.mode === "fullscreen") displayMode = granted.mode;
  }
</script>

{#if connected}
  {#if canFullscreen}
    <div class="flex justify-end px-2 pt-2">
      <Button
        variant="ghost"
        size="icon-sm"
        aria-label={displayMode === "inline" ? "Show full screen" : "Show in the conversation"}
        onclick={toggleDisplayMode}
      >
        {#if displayMode === "inline"}
          <Maximize2 class="size-4" />
        {:else}
          <Minimize2 class="size-4" />
        {/if}
      </Button>
    </div>
  {/if}
  <ReportView {result} {locale} {displayMode} />
{:else}
  <p class="p-4 text-sm text-muted-foreground" role="alert">
    This view did not connect to its host. Open it from an assistant that supports MCP Apps.
  </p>
{/if}
