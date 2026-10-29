<!--
  The view's connection to the MCP Apps host. It calls nothing on the host beyond connect():
  the tool result arrives as a notification.
-->
<script lang="ts">
  import {
    App,
    applyDocumentTheme,
    applyHostFonts,
    applyHostStyleVariables,
    type McpUiHostContext,
  } from "@modelcontextprotocol/ext-apps";
  import ReportView from "./ReportView.svelte";

  let result = $state.raw<{ structuredContent?: unknown } | null>(null);
  let locale = $state(navigator.language);
  let connected = $state(true);

  function applyHostContext(context: McpUiHostContext | undefined) {
    if (!context) return;
    if (context.theme) {
      applyDocumentTheme(context.theme);
      document.documentElement.classList.toggle("dark", context.theme === "dark");
    }
    if (context.styles?.variables) applyHostStyleVariables(context.styles.variables);
    if (context.styles?.css?.fonts) applyHostFonts(context.styles.css.fonts);
    if (context.locale) locale = context.locale;
  }

  const app = new App(
    { name: "Nocturne report", version: "0.1.0" },
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
</script>

{#if connected}
  <ReportView {result} {locale} />
{:else}
  <p class="p-4 text-sm text-muted-foreground" role="alert">
    This view did not connect to its host. Open it from an assistant that supports MCP Apps.
  </p>
{/if}
