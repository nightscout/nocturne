//! Meal detection and processing

use chrono::{DateTime, Utc};
use crate::types::{MealData, Profile, Treatment, GlucoseReading};
use crate::cob;
use crate::Result;

/// Generate meal data from treatment history
///
/// This implements the meal detection from `lib/meal/index.js` and `lib/meal/total.js`.
/// COB is calculated using glucose deviation analysis from the cob module.
pub fn generate(
    profile: &Profile,
    treatments: &[Treatment],
    glucose_data: &[GlucoseReading],
    clock: DateTime<Utc>,
) -> Result<MealData> {
    // Find carb treatments within meal absorption window
    let max_absorption_hours = profile.max_meal_absorption_time;
    let clock_millis = clock.timestamp_millis();
    let carb_window = clock_millis - (max_absorption_hours * 60.0 * 60.0 * 1000.0) as i64;

    let mut carbs = 0.0;
    let mut ns_carbs = 0.0;
    let mut bw_carbs = 0.0;
    let mut journal_carbs = 0.0;
    let mut last_carb_time: i64 = 0;
    let mut bw_found = false;

    for treatment in treatments {
        let treatment_time = treatment.effective_date();

        if treatment_time < carb_window || treatment_time > clock_millis {
            continue;
        }

        if let Some(c) = treatment.carbs {
            if c >= 1.0 {
                carbs += c;
                last_carb_time = last_carb_time.max(treatment_time);

                // Categorize carb source
                if let Some(ns) = treatment.ns_carbs {
                    ns_carbs += ns;
                } else if let Some(bw) = treatment.bw_carbs {
                    bw_carbs += bw;
                    bw_found = true;
                } else if let Some(jc) = treatment.journal_carbs {
                    journal_carbs += jc;
                } else {
                    // Default to NS carbs
                    ns_carbs += c;
                }
            }
        }
    }

    // Calculate COB using glucose deviation analysis
    let cob_result = cob::calculate(profile, glucose_data, treatments, clock)?;

    // Use deviation-based COB, but cap at max_cob and entered carbs
    let meal_cob = cob_result.meal_cob.min(profile.max_cob).min(carbs);

    Ok(MealData {
        carbs,
        ns_carbs,
        bw_carbs,
        journal_carbs,
        meal_cob,
        current_deviation: cob_result.current_deviation,
        max_deviation: cob_result.max_deviation,
        min_deviation: cob_result.min_deviation,
        slope_from_max_deviation: cob_result.slope_from_max,
        slope_from_min_deviation: cob_result.slope_from_min,
        all_deviations: vec![], // COB module returns i32, MealData expects f64 - handled separately if needed
        last_carb_time,
        bw_found,
    }.rounded())
}

/// Find meal treatments from history
pub fn find_meals(
    profile: &Profile,
    treatments: &[Treatment],
    clock: DateTime<Utc>,
) -> Vec<Treatment> {
    let max_absorption_hours = profile.max_meal_absorption_time;
    let clock_millis = clock.timestamp_millis();
    let carb_window = clock_millis - (max_absorption_hours * 60.0 * 60.0 * 1000.0) as i64;

    treatments
        .iter()
        .filter(|t| {
            let time = t.effective_date();
            time >= carb_window && time <= clock_millis && t.has_carbs()
        })
        .cloned()
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::Duration;

    fn make_profile() -> Profile {
        Profile {
            max_meal_absorption_time: 6.0,
            max_cob: 120.0,
            ..Default::default()
        }
    }

    #[test]
    fn test_find_recent_carbs() {
        let now = Utc::now();
        let profile = make_profile();

        let treatments = vec![
            Treatment::carbs(50.0, now - Duration::hours(1)),
        ];

        let meal_data = generate(&profile, &treatments, &[], now).unwrap();

        assert_eq!(meal_data.carbs, 50.0);
        assert_eq!(meal_data.ns_carbs, 50.0);
    }

    #[test]
    fn test_ignore_old_carbs() {
        let now = Utc::now();
        let profile = make_profile();

        // Carbs from 7 hours ago (beyond 6h absorption window)
        let treatments = vec![
            Treatment::carbs(50.0, now - Duration::hours(7)),
        ];

        let meal_data = generate(&profile, &treatments, &[], now).unwrap();

        assert_eq!(meal_data.carbs, 0.0);
    }

    #[test]
    fn test_multiple_carb_entries() {
        let now = Utc::now();
        let profile = make_profile();

        let treatments = vec![
            Treatment::carbs(30.0, now - Duration::hours(1)),
            Treatment::carbs(20.0, now - Duration::hours(2)),
        ];

        let meal_data = generate(&profile, &treatments, &[], now).unwrap();

        assert_eq!(meal_data.carbs, 50.0);
    }

    #[test]
    fn test_cob_capped_by_max() {
        let now = Utc::now();
        let mut profile = make_profile();
        profile.max_cob = 100.0;

        let treatments = vec![
            Treatment::carbs(150.0, now - Duration::minutes(30)),
        ];

        let meal_data = generate(&profile, &treatments, &[], now).unwrap();

        assert!(meal_data.meal_cob <= 100.0);
    }
}
