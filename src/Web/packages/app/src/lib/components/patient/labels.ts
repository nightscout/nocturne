import {
  DiabetesType,
  BiologicalSex,
  DeviceCategory,
  AidAlgorithm,
  InsulinCategory,
  InsulinRole,
} from "$api";

export const diabetesTypeLabels: Record<DiabetesType, string> = {
  [DiabetesType.Type1]: "Type 1",
  [DiabetesType.Type2]: "Type 2",
  [DiabetesType.LADA]: "LADA",
  [DiabetesType.MODY]: "MODY",
  [DiabetesType.Gestational]: "Gestational",
  [DiabetesType.Other]: "Other",
};

export const biologicalSexLabels: Record<BiologicalSex, string> = {
  [BiologicalSex.Female]: "Female",
  [BiologicalSex.Male]: "Male",
};

export const deviceCategoryLabels: Record<DeviceCategory, string> = {
  [DeviceCategory.InsulinPump]: "Insulin Pump",
  [DeviceCategory.CGM]: "CGM",
  [DeviceCategory.GlucoseMeter]: "Glucose Meter",
  [DeviceCategory.InsulinPen]: "Insulin Pen",
  [DeviceCategory.SmartPen]: "Smart Pen",
  [DeviceCategory.Uploader]: "Uploader",
};

export const aidAlgorithmLabels: Record<AidAlgorithm, string> = {
  [AidAlgorithm.OpenAps]: "OpenAPS",
  [AidAlgorithm.AndroidAps]: "AndroidAPS",
  [AidAlgorithm.Loop]: "Loop",
  [AidAlgorithm.Trio]: "Trio",
  [AidAlgorithm.IAPS]: "iAPS",
  [AidAlgorithm.ControlIQ]: "Control-IQ",
  [AidAlgorithm.CamAPSFX]: "CamAPS FX",
  [AidAlgorithm.Omnipod5Algorithm]: "Omnipod 5",
  [AidAlgorithm.MedtronicSmartGuard]: "SmartGuard",
  [AidAlgorithm.None]: "None",
  [AidAlgorithm.Unknown]: "Unknown",
};

export const insulinCategoryLabels: Record<InsulinCategory, string> = {
  [InsulinCategory.RapidActing]: "Rapid Acting",
  [InsulinCategory.ShortActing]: "Short Acting",
  [InsulinCategory.IntermediateActing]: "Intermediate Acting",
  [InsulinCategory.LongActing]: "Long Acting",
  [InsulinCategory.UltraLongActing]: "Ultra Long Acting",
  [InsulinCategory.Premixed]: "Premixed",
};

export const insulinCategoryDescriptions: Record<InsulinCategory, string> = {
  [InsulinCategory.RapidActing]: "e.g. Humalog, NovoRapid, Fiasp",
  [InsulinCategory.ShortActing]: "e.g. Humulin R, Actrapid",
  [InsulinCategory.IntermediateActing]: "e.g. Humulin N, Insulatard",
  [InsulinCategory.LongActing]: "e.g. Lantus, Levemir, Tresiba",
  [InsulinCategory.UltraLongActing]: "e.g. Toujeo",
  [InsulinCategory.Premixed]: "e.g. NovoMix 30, Humalog Mix",
};

export const insulinRoleLabels: Record<InsulinRole, string> = {
  [InsulinRole.Bolus]: "Bolus",
  [InsulinRole.Basal]: "Basal",
  [InsulinRole.Both]: "Both",
};

export const insulinRoleDescriptions: Record<InsulinRole, string> = {
  [InsulinRole.Bolus]: "Used for meal and correction boluses",
  [InsulinRole.Basal]: "Used for background insulin coverage",
  [InsulinRole.Both]: "Used for both basal and bolus",
};
