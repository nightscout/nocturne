<script lang="ts">
  import type { WidgetId } from "$lib/api/generated/nocturne-api-client";

  let { widgets }: { widgets: WidgetId[]; maxWidgets?: number } = $props();
</script>

<div data-testid="widget-grid">{widgets.join(",")}</div>
