<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import type { ClusterConfidence } from "$lib/api";
  import { confidenceBadgeVariant, confidenceLabel } from "./format";

  let { confidence }: { confidence: ClusterConfidence | undefined } = $props();
</script>

<Badge variant={confidenceBadgeVariant(confidence)}>
  {confidenceLabel(confidence)}
</Badge>
