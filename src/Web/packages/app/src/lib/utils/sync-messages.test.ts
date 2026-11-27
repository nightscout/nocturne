import { describe, expect, it } from "vitest";
import { SyncDataType } from "@nocturne/api-client";
import { dataTypeLabel, dataTypePhrase } from "./data-type-labels.svelte";
import { formatSyncMessage } from "./sync-messages.svelte";

describe("sync messages", () => {
  it("count the records in the in-sentence form of the data type", () => {
    expect(
      formatSyncMessage("PublishingDataType", { count: "12", dataType: "Glucose" })
    ).toBe("Publishing 12 glucose readings...");
  });

  it("count one record in the singular", () => {
    expect(
      formatSyncMessage("PublishingDataType", { count: "1", dataType: "Glucose" })
    ).toBe("Publishing 1 glucose reading...");
    expect(
      formatSyncMessage("PublishingDataType", { count: "1", dataType: "Entries" })
    ).toBe("Publishing 1 legacy entry...");
  });

  it("name the data type alone when the API sends no usable count", () => {
    expect(formatSyncMessage("PublishingDataType", { dataType: "Glucose" })).toBe(
      "Publishing glucose readings..."
    );
    expect(
      formatSyncMessage("PublishingDataType", { count: "many", dataType: "Glucose" })
    ).toBe("Publishing glucose readings...");
  });

  it("name a data type in its in-sentence form, never its heading", () => {
    const named = Object.values(SyncDataType).filter(
      (key) => dataTypeLabel(key) !== dataTypePhrase(key)
    );
    expect(named.length).toBeGreaterThan(0);
    for (const key of named) {
      for (const type of ["ProcessingDataType", "PublishingDataType"] as const) {
        const message = formatSyncMessage(type, { count: "3", dataType: key });
        expect(message).toContain(dataTypePhrase(key));
        expect(message).not.toContain(dataTypeLabel(key));
      }
    }
  });

  it("fill the dates of a fetch", () => {
    expect(
      formatSyncMessage("FetchingData", { from: "2026-11-01", to: "2026-11-27" })
    ).toBe("Fetching data from 2026-11-01 to 2026-11-27...");
  });

  it("read the same with no parameters for a message that takes none", () => {
    expect(formatSyncMessage("Authenticating", null)).toBe("Authenticating...");
    expect(formatSyncMessage("SyncComplete")).toBe("Sync complete");
    expect(formatSyncMessage("SyncFailed")).toBe("Sync failed");
  });
});
