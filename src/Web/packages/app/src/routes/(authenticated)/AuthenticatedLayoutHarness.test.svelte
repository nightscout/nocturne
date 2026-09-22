<script lang="ts">
  import type { ComponentProps } from "svelte";
  import Layout from "./+layout.svelte";

  const { data }: { data: ComponentProps<typeof Layout>["data"] } = $props();
</script>

<Layout {data}>
  <p>page content</p>
</Layout>
