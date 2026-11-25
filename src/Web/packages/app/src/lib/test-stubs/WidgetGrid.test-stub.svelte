<script lang="ts">
  import type { WidgetId } from "@nocturne/api-client";

  let { widgets }: { widgets: WidgetId[]; maxWidgets?: number } = $props();
</script>

<div data-testid="widget-grid">{widgets.join(",")}</div>
