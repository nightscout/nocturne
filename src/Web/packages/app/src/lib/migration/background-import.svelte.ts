import { MigrationJobState } from "#lib/api/index.js";
import type { MigrationJobStatus } from "#lib/api/index.js";
import * as migrationRemote from "#lib/api/generated/migrations.generated.remote.js";
import { fresh } from "#lib/api/fresh-query.js";
import { errorStatus } from "#lib/forms/submit-error.js";

// localStorage, so the app finds the import the setup wizard or the settings page started in
// another tab. No import to watch costs the app no request.
const WATCHED_IMPORT_KEY = "nocturne.import.watched-job";

/** Tells a watcher already started in this tab that there is an import to follow. */
const WATCHED_IMPORT_EVENT = "nocturne:import-watched";

const POLL_INTERVAL_MS = 5000;
const MAX_CONSECUTIVE_FAILURES = 5;

type WatchedImport = {
  jobId: string;
  /** The full-screen notice was closed, so only the banner is left to show. */
  acknowledged: boolean;
};

function readWatchedImport(): WatchedImport | undefined {
  try {
    const stored = globalThis.localStorage?.getItem(WATCHED_IMPORT_KEY);
    if (!stored) return undefined;
    const parsed: unknown = JSON.parse(stored);
    if (
      parsed &&
      typeof parsed === "object" &&
      "jobId" in parsed &&
      typeof parsed.jobId === "string"
    ) {
      return {
        jobId: parsed.jobId,
        acknowledged: "acknowledged" in parsed && parsed.acknowledged === true,
      };
    }
  } catch {
    // Blocked site data or a value another version wrote: there is no import to watch.
  }
  return undefined;
}

function writeWatchedImport(watched: WatchedImport | undefined) {
  try {
    if (watched) {
      globalThis.localStorage?.setItem(WATCHED_IMPORT_KEY, JSON.stringify(watched));
    } else {
      globalThis.localStorage?.removeItem(WATCHED_IMPORT_KEY);
    }
  } catch {
    // Blocked site data costs only the notice in the app.
  }
}

/**
 * Names the import the app follows while it runs. `acknowledged` skips the full-screen notice,
 * for an import started from a page that already shows its progress.
 */
export function watchImport(jobId: string, { acknowledged = false } = {}) {
  const current = readWatchedImport();
  writeWatchedImport({
    jobId,
    acknowledged: acknowledged || (current?.jobId === jobId && current.acknowledged),
  });
  globalThis.dispatchEvent?.(new Event(WATCHED_IMPORT_EVENT));
}

function isLive(state: MigrationJobState | undefined): boolean {
  return state === MigrationJobState.Running || state === MigrationJobState.Pending;
}

/**
 * Follows the import named by {@link watchImport} until it ends. `status` is set only while the
 * import is live, and `ended` reports how it finished, once.
 */
export function createImportWatcher(options: {
  ended?: (state: MigrationJobState, status: MigrationJobStatus) => void;
} = {}) {
  let status = $state<MigrationJobStatus>();
  let acknowledged = $state(false);

  /**
   * Follows the watched import, and one named later in this tab. Returns the function that
   * stops it.
   */
  function start(): () => void {
    let stop = follow();
    const refollow = () => {
      stop();
      stop = follow();
    };
    globalThis.addEventListener?.(WATCHED_IMPORT_EVENT, refollow);
    return () => {
      globalThis.removeEventListener?.(WATCHED_IMPORT_EVENT, refollow);
      stop();
    };
  }

  function follow(): () => void {
    const watched = readWatchedImport();
    if (!watched) return () => {};

    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;
    acknowledged = watched.acknowledged;

    function stopWatching() {
      active = false;
      status = undefined;
      writeWatchedImport(undefined);
    }

    async function poll() {
      // A hidden tab shows no one the progress, and an import can run for hours.
      if (globalThis.document?.visibilityState === "hidden") {
        timer = setTimeout(poll, POLL_INTERVAL_MS);
        return;
      }

      try {
        const latest = await fresh(migrationRemote.getStatus(watched!.jobId));
        if (!active) return;
        failures = 0;
        if (isLive(latest.state)) {
          status = latest;
        } else {
          stopWatching();
          if (latest.state !== undefined) options.ended?.(latest.state, latest);
          return;
        }
      } catch (err) {
        if (!active) return;
        // 403 and 404: this session cannot see the job, so it is another tenant's or account's.
        const refused = [403, 404].includes(errorStatus(err) ?? 0);
        if (refused || ++failures >= MAX_CONSECUTIVE_FAILURES) {
          stopWatching();
          return;
        }
      }
      timer = setTimeout(poll, POLL_INTERVAL_MS);
    }

    void poll();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }

  return {
    start,
    /** The live import's status, or undefined when no import is running. */
    get status() {
      return status;
    },
    get acknowledged() {
      return acknowledged;
    },
    acknowledge() {
      acknowledged = true;
      const watched = readWatchedImport();
      if (watched) writeWatchedImport({ ...watched, acknowledged: true });
    },
  };
}

export type ImportWatcher = ReturnType<typeof createImportWatcher>;
