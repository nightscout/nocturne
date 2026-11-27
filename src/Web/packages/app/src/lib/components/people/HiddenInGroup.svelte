<script lang="ts">
  import { plural } from "#lib/utils/plural.js";

  const { count }: { count: number } = $props();
</script>

{plural(count, ["You cannot see # person in this group.", "You cannot see # people in this group."])}
