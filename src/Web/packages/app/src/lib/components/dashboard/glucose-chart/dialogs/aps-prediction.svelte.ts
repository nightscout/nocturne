import type { ApsSnapshot } from "$lib/api";
import type { PredictionData } from "$api/predictions.remote";
import { getAll as getApsSnapshots } from "$lib/api/generated/apsSnapshots.generated.remote";
import { apsSnapshotToPrediction } from "$lib/utils/aps-snapshot-to-prediction";
import { inspectionSearchWindow } from "./inspection-window";

export interface ApsPrediction {
  readonly snapshot: ApsSnapshot | null;
  readonly predictionData: PredictionData | null;
}

/**
 * Resolves the APS snapshot nearest an inspected instant, and the prediction curves derived from
 * it, for as long as the dialog reading them is open.
 *
 * Call during component initialization: the fetch is owned by an `$effect` that re-runs when the
 * dialog opens or the instant moves, and a superseded fetch is dropped rather than allowed to
 * overwrite the newer one. A tenant with no APS integration has no snapshots, so a failure leaves
 * both values null instead of surfacing an error.
 */
export function useApsPrediction(
  open: () => boolean,
  timestamp: () => Date,
): ApsPrediction {
  let snapshot = $state<ApsSnapshot | null>(null);
  let predictionData = $state<PredictionData | null>(null);

  $effect(() => {
    if (!open()) return;
    snapshot = null;
    predictionData = null;
    let cancelled = false;
    const { from, to } = inspectionSearchWindow(timestamp());
    getApsSnapshots({ from, to, limit: 1, sort: "timestamp_desc" })
      .then((result) => {
        if (!cancelled && result?.data?.length) {
          snapshot = result.data[0];
          predictionData = apsSnapshotToPrediction(result.data[0]);
        }
      })
      .catch(() => {
        /* APS data is optional */
      });
    return () => {
      cancelled = true;
    };
  });

  return {
    get snapshot() {
      return snapshot;
    },
    get predictionData() {
      return predictionData;
    },
  };
}
