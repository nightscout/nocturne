<script lang="ts">
  import { ConfirmationBackground } from "@nocturne/watercolour";

  let { message }: { message: string } = $props();
</script>

<!-- The toast is a positioned stacking context, so the wash fills it from
     behind its copy rather than sitting inside the title line. -->
<ConfirmationBackground
  palette="moss"
  motion="auto"
  fit="fill"
  class="-z-10 rounded-lg"
/>
{message}
