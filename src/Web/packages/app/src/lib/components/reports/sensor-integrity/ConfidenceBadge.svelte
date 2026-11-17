<script lang="ts">
  import { Badge } from "#lib/components/ui/badge/index.js";
  import type { ClusterConfidence } from "#lib/api/index.js";
  import { confidenceKey, confidenceLabel } from "./format";

  let { confidence }: { confidence: ClusterConfidence | undefined } = $props();
</script>

<Badge variant={confidenceKey(confidence)}>
  {confidenceLabel(confidence)}
</Badge>
