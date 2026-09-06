<script lang="ts">
  import { DeadEndCard } from "$lib/components/shared";
  import { Link2Off } from "lucide-svelte";
</script>

<svelte:head>
  <title>Share link not working</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<DeadEndCard icon={Link2Off} title="This share link isn't working">
  <!-- A rotated link, an inactive tenant and an API that is briefly unready all land here and
       are indistinguishable from the browser, so the copy names none of them. Prompting for a
       replacement link first would be actively harmful: rotating is how the owner would supply
       one, and it permanently breaks the link for every other recipient. -->
  <p>
    It may have been turned off or replaced, or the service may be temporarily
    unavailable.
  </p>
  <p>
    Try again in a few minutes. If it still doesn't work, ask the person who
    shared it to check the link.
  </p>
</DeadEndCard>
