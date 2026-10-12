/** Shared by the connector and its documentation; API keys remain untranslated. */
export function googleHealthDataTypeLabel(
  type: string | null | undefined
): string {
  const labels: Record<string, string> = {
    steps: "Steps",
    heartrate: "Heart rate",
    weight: "Weight",
    sleep: "Sleep sessions and stages",
    activeenergyburned: "Active energy burned",
    totalcalories: "Total calories",
    distance: "Distance",
    floors: "Floors",
    exercise: "Workouts",
    bodyfat: "Body fat",
    height: "Height",
    nutritionlog: "Nutrition log",
    hydrationlog: "Hydration",
    bloodglucose: "Blood glucose",
    oxygensaturation: "Oxygen saturation",
    heartratevariability: "Heart rate variability",
    dailyrestingheartrate: "Resting heart rate",
    dailyoxygensaturation: "Daily oxygen saturation",
    dailyrespiratoryrate: "Daily respiratory rate",
    respiratoryratesleepsummary: "Sleep respiratory rate",
    corebodytemperature: "Core body temperature",
    menstrualperiod: "Menstrual period",
    ovulationtest: "Ovulation test",
    cervicalmucus: "Cervical mucus",
    sexualactivity: "Sexual activity",
  };
  return labels[(type ?? "").replaceAll("-", "")] ?? "Unknown data type";
}
