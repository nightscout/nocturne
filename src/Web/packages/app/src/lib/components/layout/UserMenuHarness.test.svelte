<script lang="ts">
  import * as Sidebar from "$lib/components/ui/sidebar";
  import UserMenu from "./UserMenu.svelte";
</script>

<Sidebar.Provider>
  <Sidebar.Menu>
    <UserMenu user={{ subjectId: "guest-1", name: "Guest User", roles: [], permissions: [] }} isGuestSession />
  </Sidebar.Menu>
</Sidebar.Provider>
