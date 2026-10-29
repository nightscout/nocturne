<!--
  The view's connection to the MCP Apps host. The tool result arrives as a notification. Each call
  to the host is offered only when the host's capabilities grant it: display mode when it lists both
  modes, refine_report with tools/call, the selected event's sentence with update-model-context,
  and the web report with open-link.
-->
<script lang="ts">
  import {
    App,
    applyDocumentTheme,
    applyHostFonts,
    applyHostStyleVariables,
    type McpUiHostCapabilities,
    type McpUiHostContext,
  } from "@modelcontextprotocol/ext-apps";
  import Maximize2 from "@lucide/svelte/icons/maximize-2";
  import Minimize2 from "@lucide/svelte/icons/minimize-2";
  import { Button } from "@nocturne/ui/ui/button";
  import ReportView from "./ReportView.svelte";
  import { readReport, type ReportPatch } from "./payload.js";

  type DisplayMode = "inline" | "fullscreen";
  type ToolResult = { structuredContent?: unknown; isError?: boolean; content?: unknown };

  let { app: given }: { app?: App } = $props();

  let result = $state.raw<ToolResult | null>(null);
  let original = $state.raw<ToolResult | null>(null);
  let locale = $state(navigator.language);
  let connected = $state(true);
  let displayMode = $state<DisplayMode>("inline");
  let canFullscreen = $state(false);
  let capabilities = $state.raw<McpUiHostCapabilities | undefined>(undefined);
  let notice = $state<string | null>(null);
  let busy = $state(false);

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

  // svelte-ignore state_referenced_locally
  const app =
    given ??
    new App({ name: "Nocturne report", version: "0.3.0" }, { availableDisplayModes: ["inline", "fullscreen"] });
  app.ontoolresult = (params) => {
    result = params;
    original = params;
    notice = null;
  };
  app.onhostcontextchanged = applyHostContext;
  app.connect().then(
    () => {
      applyHostContext(app.getHostContext());
      capabilities = app.getHostCapabilities();
    },
    () => (connected = false),
  );

  async function toggleDisplayMode() {
    const next: DisplayMode = displayMode === "inline" ? "fullscreen" : "inline";
    const granted = await app.requestDisplayMode({ mode: next });
    if (granted.mode === "inline" || granted.mode === "fullscreen") displayMode = granted.mode;
  }

  /** The text of a tool result that failed: the API's list of what to change. */
  function errorText(refined: ToolResult): string {
    const blocks = Array.isArray(refined.content) ? refined.content : [];
    const text = blocks
      .map((b: unknown) => (b && typeof b === "object" && "text" in b && typeof b.text === "string" ? b.text : ""))
      .join("\n")
      .trim();
    return text || "The report could not be drawn again.";
  }

  async function refine(patch: ReportPatch) {
    const spec = readReport(result?.structuredContent)?.spec;
    if (!spec) return;
    busy = true;
    notice = null;
    try {
      const refined: ToolResult = await app.callServerTool({ name: "refine_report", arguments: { spec, patch } });
      if (refined.isError) notice = errorText(refined);
      else result = refined;
    } catch {
      notice = "The host did not draw the report again.";
    } finally {
      busy = false;
    }
  }

  function selectEvent(text: string) {
    void app.updateModelContext({ content: [{ type: "text", text }] }).catch(() => {});
  }

  function openLink(url: string) {
    void app.openLink({ url }).catch(() => {});
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
  <ReportView
    {result}
    {original}
    {locale}
    {displayMode}
    {notice}
    {busy}
    refine={capabilities?.serverTools ? refine : undefined}
    selectEvent={capabilities?.updateModelContext ? selectEvent : undefined}
    openLink={capabilities?.openLinks ? openLink : undefined}
  />
{:else}
  <p class="p-4 text-sm text-muted-foreground" role="alert">
    This view did not connect to its host. Open it from an assistant that supports MCP Apps.
  </p>
{/if}
