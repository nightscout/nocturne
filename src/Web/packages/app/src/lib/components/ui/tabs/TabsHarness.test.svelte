<script lang="ts">
  import * as Tabs from "./index";

  let { value = "first" }: { value?: string } = $props();
</script>

<Tabs.Root {value}>
  <Tabs.List>
    <Tabs.Trigger value="first">First</Tabs.Trigger>
    <Tabs.Trigger value="second">Second</Tabs.Trigger>
  </Tabs.List>
  <Tabs.Content value="first">First panel</Tabs.Content>
  <Tabs.Content value="second">Second panel</Tabs.Content>
</Tabs.Root>
