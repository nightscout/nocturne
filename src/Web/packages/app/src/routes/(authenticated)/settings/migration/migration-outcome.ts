import { MigrationJobState, type MigrationJobInfo } from "$api";

/**
 * Whether a finished run earns the completion moment. A Completed run whose
 * collections partly failed stays Completed on the server, so the failure flag
 * is what keeps it from celebrating; a skip alone is an ordinary import.
 */
export function completedCleanly(job: MigrationJobInfo | undefined): boolean {
  return job?.state === MigrationJobState.Completed && !job.hasFailures;
}
