<script lang="ts">
  import * as Tabs from "$lib/components/ui/tabs";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Alert from "$lib/components/ui/alert";
  import {
    Users,
    Shield,
    ShieldCheck,
    Globe,
    Pencil,
    Trash2,
    TriangleAlert,
    Plus,
  } from "lucide-svelte";
  import type { TenantMemberDto, TenantMemberRoleDto } from "$api";

  interface Props {
    subjects: TenantMemberDto[];
    currentUserSubjectId: string | undefined;
    platformAdminError?: string | null;
    platformAdminSavingId?: string | null;
    openNewSubject: () => void;
    openEditSubject: (subject: TenantMemberDto) => void;
    togglePlatformAdmin: (subject: TenantMemberDto) => void;
    deleteSubjectHandler: (id: string) => void;
    getSubjectIcon: (subject: TenantMemberDto) => any;
    isSystemSubjectCheck: (subject: TenantMemberDto) => boolean;
    formatDate: (date: any) => string;
  }

  let {
    subjects,
    currentUserSubjectId,
    platformAdminError = null,
    platformAdminSavingId = null,
    openNewSubject,
    openEditSubject,
    togglePlatformAdmin,
    deleteSubjectHandler,
    getSubjectIcon,
    isSystemSubjectCheck,
    formatDate,
  }: Props = $props();
</script>

<Tabs.Content value="users">
  <Card>
    <CardHeader class="flex flex-row items-center justify-between">
      <div>
        <CardTitle>Users</CardTitle>
        <CardDescription>
          User accounts and their roles. For device access, use OAuth device authorization flow.
        </CardDescription>
      </div>
      <Button onclick={openNewSubject}>
        <Plus class="h-4 w-4 mr-2" />
        New User
      </Button>
    </CardHeader>
    <CardContent>
      {#if platformAdminError}
        <Alert.Root variant="destructive" class="mb-4">
          <TriangleAlert class="h-4 w-4" />
          <Alert.Description>{platformAdminError}</Alert.Description>
        </Alert.Root>
      {/if}
      {#if subjects.length === 0}
        <div class="text-center py-8 text-muted-foreground">
          <Users class="h-12 w-12 mx-auto mb-3 opacity-50" />
          <p>No users found</p>
          <p class="text-sm">Create your first user account to get started</p>
        </div>
      {:else}
        <div class="space-y-3">
          {#each subjects as subject (subject.id)}
            {@const Icon = getSubjectIcon(subject)}
            {@const isPublicSubject =
              isSystemSubjectCheck(subject) && subject.name === "Public"}
            {@const isPlatformAdmin = (
              subject as TenantMemberDto & { isPlatformAdmin?: boolean }
            ).isPlatformAdmin}
            <div
              class="flex items-center justify-between p-4 rounded-lg border {isPublicSubject
                ? 'bg-primary/5 border-primary/20'
                : ''}"
            >
              <div class="flex items-center gap-3">
                <div
                  class="p-2 rounded-lg {isPublicSubject
                    ? 'bg-primary/10'
                    : 'bg-muted'}"
                >
                  <Icon
                    class="h-5 w-5 {isPublicSubject
                      ? 'text-primary'
                      : ''}"
                  />
                </div>
                <div>
                  <div class="font-medium flex items-center gap-2">
                    {subject.name}
                    {#if isPublicSubject}
                      <Badge variant="secondary">
                        <Globe class="h-3 w-3 mr-1" />
                        Unauthenticated Access
                      </Badge>
                    {/if}
                    {#if subject.roles && subject.roles.some((r: TenantMemberRoleDto) => r.name === "admin")}
                      <Badge variant="default">
                        Admin
                      </Badge>
                    {/if}
                    {#if isPlatformAdmin}
                      <Badge variant="default">
                        <ShieldCheck class="h-3 w-3 mr-1" />
                        Platform Admin
                      </Badge>
                    {/if}
                  </div>
                  {#if isPublicSubject}
                    <div class="text-sm text-muted-foreground">
                      Defines what unauthenticated users can access
                    </div>
                  {/if}
                  <div class="text-sm text-muted-foreground">
                    {#if subject.roles && subject.roles.length > 0}
                      Roles: {subject.roles
                        .map((r: TenantMemberRoleDto) => r.name ?? "")
                        .filter((n: string) => n !== "admin")
                        .join(", ") || "Admin"}
                    {:else}
                      No roles assigned
                    {/if}
                  </div>
                  <div class="text-xs text-muted-foreground mt-1">
                    Created: {formatDate(subject.sysCreatedAt)}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-2">
                {#if !isSystemSubjectCheck(subject) && subject.id !== currentUserSubjectId}
                  <Button
                    variant="ghost"
                    size="icon"
                    disabled={platformAdminSavingId === subject.id}
                    onclick={() => togglePlatformAdmin(subject)}
                    title={isPlatformAdmin
                      ? 'Revoke platform admin'
                      : 'Grant platform admin'}
                    aria-label={isPlatformAdmin
                      ? `Revoke platform admin from ${subject.name}`
                      : `Grant platform admin to ${subject.name}`}
                  >
                    {#if isPlatformAdmin}
                      <ShieldCheck class="h-4 w-4 text-primary" />
                    {:else}
                      <Shield class="h-4 w-4 text-muted-foreground" />
                    {/if}
                  </Button>
                {/if}
                <Button
                  variant="ghost"
                  size="icon"
                  onclick={() => openEditSubject(subject)}
                >
                  <Pencil class="h-4 w-4" />
                </Button>
                {#if !isSystemSubjectCheck(subject)}
                  <Button
                    variant="ghost"
                    size="icon"
                    onclick={() => deleteSubjectHandler(subject.id!)}
                  >
                    <Trash2 class="h-4 w-4" />
                  </Button>
                {/if}
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </CardContent>
  </Card>
</Tabs.Content>