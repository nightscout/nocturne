import { isoNow } from "$lib/utils/now";
import { page } from "$app/state";
import { satisfiesScope } from "$lib/authorization/scopes";
import {
  getActiveAlerts,
  acknowledgeExcursion,
  markSeen,
  snoozeInstance,
} from "$api/generated/alerts.generated.remote";
import type { ActiveExcursionResponse } from "$api-clients";
import { markSeenOnThisDevice, seenHere } from "./seen-here.svelte";

/** The snooze lengths every alert surface offers, in minutes. */
export const SNOOZE_MINUTES = [5, 15, 30, 60] as const;

/**
 * What the caller may do to an active alert, and the commands that do it, for the banner and
 * the critical takeover. Call during component initialisation: it reads the page's permissions
 * and subscribes to the shared active-alerts query.
 */
export function createAlertActions() {
  const activeAlerts = getActiveAlerts();
  let busyId = $state<string | null>(null);

  const permissions = $derived(page.data.effectivePermissions ?? []);
  // Without alerts.readwrite the server mutes the alert for this member only, so the button
  // has to say that rather than promise to stop it for everyone.
  const acknowledgesForEveryone = $derived(satisfiesScope(permissions, "alerts.readwrite"));
  const canSnooze = $derived(satisfiesScope(permissions, "alerts.acknowledge"));
  // A share or guest link cannot acknowledge, mute or snooze; it records that it saw the
  // alarm, which takes it off this device and changes nothing elsewhere.
  const hasAccount = $derived(!!page.data.hasAccount);
  const viewer = $derived({ hasAccount, isShareHost: !!page.data.isShareHost });

  async function run(id: string, action: () => Promise<unknown>): Promise<void> {
    busyId = id;
    try {
      await action();
    } finally {
      busyId = null;
    }
  }

  return {
    get alerts(): ActiveExcursionResponse[] {
      return activeAlerts.current ?? [];
    },
    get busyId() {
      return busyId;
    },
    get acknowledgesForEveryone() {
      return acknowledgesForEveryone;
    },
    get canSnooze() {
      return canSnooze;
    },
    get hasAccount() {
      return hasAccount;
    },

    /** Not yet acknowledged, muted by the caller or seen on this device. */
    isOutstanding(a: ActiveExcursionResponse): boolean {
      return !a.acknowledgedAt && !a.mutedByCaller && !seenHere(a, viewer);
    },

    /** Snooze targets the excursion's instance; an excursion with none cannot be snoozed. */
    snoozableInstance(a: ActiveExcursionResponse): string | undefined {
      return canSnooze ? a.activeInstances?.[0]?.id : undefined;
    },

    acknowledge(id: string): Promise<void> {
      // The override drops the alert from every surface at once; the single-flight refresh
      // confirms it. Who acknowledged is taken from the session server-side.
      return run(id, () =>
        acknowledgeExcursion({ excursionId: id, request: {} }).updates(
          activeAlerts.withOverride((current) =>
            (current ?? []).map((a) =>
              a.id !== id
                ? a
                : acknowledgesForEveryone
                  ? { ...a, acknowledgedAt: isoNow() }
                  : { ...a, mutedByCaller: true }
            )
          )
        )
      );
    },

    snooze(a: ActiveExcursionResponse, minutes: number): Promise<void> {
      const instanceId = a.activeInstances?.[0]?.id ?? "";
      return run(a.id ?? "", async () => {
        await snoozeInstance({ instanceId, request: { minutes } });
        await activeAlerts.refresh();
      });
    },

    seen(id: string): Promise<void> {
      return run(id, async () => {
        try {
          await markSeen(id);
          markSeenOnThisDevice(id);
        } catch {
          // The alarm closed, or the press was throttled: the refresh shows which, and the
          // alert stays up until it does.
          await activeAlerts.refresh();
        }
      });
    },
  };
}

export type AlertActions = ReturnType<typeof createAlertActions>;
