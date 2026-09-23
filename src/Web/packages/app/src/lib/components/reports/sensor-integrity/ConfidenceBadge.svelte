<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import type { ClusterConfidence } from "$lib/api";
  import { confidenceKey, confidenceLabel } from "./format";

  let { confidence }: { confidence: ClusterConfidence | undefined } = $props();
</script>

<Badge variant={confidenceKey(confidence)}>
  {confidenceLabel(confidence)}
</Badge>
