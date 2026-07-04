import * as patientRemote from "$api/generated/patientRecords.generated.remote";
import { getCatalog as getInsulinCatalog } from "$api/generated/insulinCatalogs.generated.remote";
import { getBodyWeights, create as createBodyWeight } from "$api/generated/bodyWeights.generated.remote";
import {
  type PatientDevice,
  type PatientInsulin,
  type InsulinFormulation,
  type DiscoveredSource,
  DiabetesType,
} from "$api";
import { FormGuard } from "$lib/forms";
import { z } from "zod";

/** Convert a date value from the API into a YYYY-MM-DD string for date inputs. */
function toDateInput(value: string | Date | null | undefined): string {
  if (!value) return "";
  return new Date(value).toISOString().split("T")[0];
}

const ClinicalFieldsSchema = z.object({
  diabetesType: z.string().min(1, "Diabetes type is required"),
  diabetesTypeOther: z.string().optional(),
  diagnosisDate: z.string().optional(),
  dateOfBirth: z.string().optional(),
  preferredName: z.string().optional(),
  pronouns: z.string().optional(),
  timezone: z.string().optional(),
});

const browserTimezone =
  typeof Intl !== "undefined"
    ? Intl.DateTimeFormat().resolvedOptions().timeZone
    : "";

/** Reactive clinical form state bound to the patient record API */
export class ClinicalState {
  diabetesType = $state("");
  diabetesTypeOther = $state("");
  diagnosisDate = $state("");
  dateOfBirth = $state("");
  preferredName = $state("");
  pronouns = $state("");
  timezone = $state("");
  /** True when the server has no timezone and we've pre-filled the field from the browser — used to surface a hint to confirm. */
  timezoneAutoDetected = $state(false);

  readonly #record = patientRemote.getPatientRecord();
  readonly form = patientRemote.updatePatientRecord;
  readonly guard: FormGuard<z.infer<typeof ClinicalFieldsSchema>>;

  /** Expose record for hidden form inputs (id, createdAt, etc.) */
  get record() { return this.#record.current; }

  constructor(el: () => HTMLFormElement | null) {
    // Sync fields from server when record loads
    $effect(() => {
      const r = this.#record.current;
      if (r) {
        this.diabetesType = r.diabetesType ?? "";
        this.diabetesTypeOther = r.diabetesTypeOther ?? "";
        this.diagnosisDate = toDateInput(r.diagnosisDate);
        this.dateOfBirth = toDateInput(r.dateOfBirth);
        this.preferredName = r.preferredName ?? "";
        this.pronouns = r.pronouns ?? "";
        if (r.timezone) {
          this.timezone = r.timezone;
          this.timezoneAutoDetected = false;
        } else {
          // Pre-fill the browser tz so the user just needs to confirm. Alerts with
          // time-of-day windows fall back to UTC without this — typically the wrong
          // wall-clock for anyone outside UTC.
          this.timezone = browserTimezone;
          this.timezoneAutoDetected = !!browserTimezone;
        }
      }
    });

    this.guard = new FormGuard({
      form: this.form,
      schema: ClinicalFieldsSchema,
      el,
      initial: () => {
        const r = this.#record.current;
        if (!r) return null;
        return {
          diabetesType: r.diabetesType ?? "",
          diabetesTypeOther: r.diabetesTypeOther ?? "",
          diagnosisDate: toDateInput(r.diagnosisDate),
          dateOfBirth: toDateInput(r.dateOfBirth),
          preferredName: r.preferredName ?? "",
          pronouns: r.pronouns ?? "",
          // Initial reflects the *server* value, NOT the pre-filled browser tz. Diverging
          // from `values` here is intentional: it makes the form dirty when the field is
          // auto-populated, so the Save button enables and the user is nudged to commit.
          timezone: r.timezone ?? "",
        };
      },
      values: () => ({
        diabetesType: this.diabetesType,
        diabetesTypeOther: this.diabetesType === DiabetesType.Other ? this.diabetesTypeOther : "",
        diagnosisDate: this.diagnosisDate,
        dateOfBirth: this.dateOfBirth,
        preferredName: this.preferredName,
        pronouns: this.pronouns,
        timezone: this.timezone,
      }),
      navBlockMessage: "You have unsaved changes. Leave anyway?",
      onreset: (snapshot) => {
        this.diabetesType = snapshot.diabetesType;
        this.diabetesTypeOther = snapshot.diabetesTypeOther ?? "";
        this.diagnosisDate = snapshot.diagnosisDate ?? "";
        this.dateOfBirth = snapshot.dateOfBirth ?? "";
        this.preferredName = snapshot.preferredName ?? "";
        this.pronouns = snapshot.pronouns ?? "";
        this.timezone = snapshot.timezone ?? "";
        this.timezoneAutoDetected = false;
      },
    });
  }
}

/** Reactive device list state with CRUD, discovered-source registration, and rank reordering */
export class DeviceListState {
  readonly #devices = patientRemote.getDevices();
  readonly #discovered = patientRemote.getDiscoveredSources();
  readonly createForm = patientRemote.createDevice;
  readonly updateForm = patientRemote.updateDevice;

  get items(): PatientDevice[] { return (this.#devices.current ?? []) as PatientDevice[]; }

  /** Distinct (dataSource, device) combinations seen recently in unattributed readings. */
  get discoveredSources(): DiscoveredSource[] {
    return (this.#discovered.current ?? []) as DiscoveredSource[];
  }

  remove = async (id: string): Promise<void> => {
    await patientRemote.deleteDevice(id);
  };

  /**
   * Persist the given device order as {@link PatientDevice.rank} — each id's position becomes its
   * rank. One request for the whole list.
   */
  reorder = async (orderedIds: string[]): Promise<void> => {
    await patientRemote.reorderDevices(
      orderedIds.map((id, index) => ({ id, rank: index })),
    );
  };
}

/** Reactive insulin list state with CRUD and catalog */
export class InsulinListState {
  readonly #insulins = patientRemote.getInsulins();
  readonly #catalog = getInsulinCatalog(undefined);
  readonly createForm = patientRemote.createInsulin;
  readonly updateForm = patientRemote.updateInsulin;

  get items(): PatientInsulin[] { return (this.#insulins.current ?? []) as PatientInsulin[]; }
  get catalog(): InsulinFormulation[] { return (this.#catalog.current ?? []) as InsulinFormulation[]; }

  remove = async (id: string): Promise<void> => {
    await patientRemote.deleteInsulin(id);
  };
}

/** Reactive weight state for initial body weight entry */
export class WeightState {
  weightKg = $state("");
  saving = $state(false);
  saveError = $state<string | null>(null);

  readonly #existing = getBodyWeights({ count: 1, skip: 0 });

  constructor() {
    $effect(() => {
      const records = this.#existing.current;
      if (records && records.length > 0) {
        this.weightKg = String(records[0].weightKg ?? "");
      }
    });
  }

  save = async (): Promise<boolean> => {
    if (!this.weightKg) return true;
    this.saving = true;
    this.saveError = null;
    try {
      await createBodyWeight({
        weightKg: Number(this.weightKg),
        mills: Date.now(),
      });
      return true;
    } catch {
      this.saveError = "Failed to save weight. Please try again.";
      return false;
    } finally {
      this.saving = false;
    }
  };
}
