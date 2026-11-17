<script lang="ts">
  import { useModifierKey } from "@nocturne/ui/modifier-key";

  const modifier = useModifierKey();
</script>

<p data-testid="label">{modifier.label}</p>
<p data-testid="is-mac">{modifier.isMac}</p>
