import { MigrationJobState } from "$api";
import type { MigrationJobInfo } from "$api";
import * as migrationRemote from "$api/generated/migrations.generated.remote";

// sessionStorage outlives a reload but not a new tab or a later visit. That is the lifetime
// wanted here: one pass through setup.
const SESSION_JOB_KEY = "nocturne.setup.migration-job-id";

const IN_FLIGHT_STATES: MigrationJobState[] = [
  MigrationJobState.Running,
  MigrationJobState.Pending,
  MigrationJobState.Validating,
];

function readSessionJobId(): string | undefined {
  try {
    return globalThis.sessionStorage?.getItem(SESSION_JOB_KEY) ?? undefined;
  } catch {
    return undefined;
  }
}

function rememberSessionJobId(jobId: string): string {
  try {
    globalThis.sessionStorage?.setItem(SESSION_JOB_KEY, jobId);
  } catch {
    // Blocked site data costs only the duplicate-start guard across a reload.
  }
  return jobId;
}

/**
 * The job out of `history` that this setup session may watch: one still in flight, or the
 * completed run this session started.
 *
 * A tenant's migration runs are kept for ever and survive a data deletion. A finished run is
 * therefore evidence of an import only when this session started it. A user starting over
 * would otherwise take an ancient run for their own, and be shown its records as if they had
 * just arrived. The id lives in session storage, not component state, so a reload mid-wizard
 * still recognises the session's own job.
 */
export function findSessionJob(
  history: MigrationJobInfo[] | undefined
): string | undefined {
  const inFlight = history?.find(
    (job) => job.state !== undefined && IN_FLIGHT_STATES.includes(job.state)
  );
  if (inFlight?.id) return rememberSessionJobId(inFlight.id);

  const sessionJobId = readSessionJobId();
  const resumable =
    sessionJobId !== undefined &&
    history?.some(
      (job) =>
        job.id === sessionJobId && job.state === MigrationJobState.Completed
    );
  return resumable ? sessionJobId : undefined;
}

/**
 * Attaches this session to a migration, starting one from the saved connector when
 * {@link findSessionJob} finds none this session may claim.
 */
export async function startOrResumeMigration(
  connectorName: string
): Promise<string | undefined> {
  const history = await migrationRemote.getHistory().run();

  const existing = findSessionJob(history);
  if (existing) return existing;

  const job = await migrationRemote.startFromConnector(connectorName);
  return job?.id ? rememberSessionJobId(job.id) : undefined;
}
