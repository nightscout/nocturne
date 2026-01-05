//! Carb ratio schedule lookups

use chrono::{DateTime, Timelike, Utc};
use crate::types::Profile;

/// Look up the carb ratio at a specific time
pub fn carb_ratio_lookup(profile: &Profile, time: DateTime<Utc>) -> f64 {
    // If no schedule defined, return the single carb ratio
    if profile.carb_ratio_profile.is_empty() {
        return profile.carb_ratio;
    }

    let now_minutes = time.hour() * 60 + time.minute();

    // Sort by index
    let mut schedule: Vec<_> = profile.carb_ratio_profile.iter().collect();
    schedule.sort_by_key(|e| e.i);

    // Default to last entry (wraps around midnight)
    let mut ratio = schedule
        .last()
        .map(|e| e.ratio)
        .unwrap_or(profile.carb_ratio);

    // Find the matching time window
    for i in 0..schedule.len() {
        let entry = schedule[i];
        let next_minutes = if i + 1 < schedule.len() {
            schedule[i + 1].minutes
        } else {
            24 * 60 // End of day
        };

        if now_minutes >= entry.minutes && now_minutes < next_minutes {
            ratio = entry.ratio;
            break;
        }
    }

    ratio
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;
    use crate::types::CarbRatioScheduleEntry;

    #[test]
    fn test_carb_ratio_lookup_no_schedule() {
        let profile = Profile {
            carb_ratio: 10.0,
            ..Default::default()
        };

        let ratio = carb_ratio_lookup(&profile, Utc::now());
        assert!((ratio - 10.0).abs() < 0.1);
    }

    #[test]
    fn test_carb_ratio_lookup_single_entry() {
        let profile = Profile {
            carb_ratio: 10.0,
            carb_ratio_profile: vec![
                CarbRatioScheduleEntry::new(0, 8.0, 0), // 00:00
            ],
            ..Default::default()
        };

        // Any time should return the single entry
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 12, 0, 0).unwrap();
        let ratio = carb_ratio_lookup(&profile, time);
        assert!((ratio - 8.0).abs() < 0.1);
    }

    #[test]
    fn test_carb_ratio_lookup_multiple_entries() {
        let profile = Profile {
            carb_ratio: 10.0,
            carb_ratio_profile: vec![
                CarbRatioScheduleEntry::new(0, 8.0, 0),     // 00:00 - midnight
                CarbRatioScheduleEntry::new(1, 10.0, 360),  // 06:00 - morning
                CarbRatioScheduleEntry::new(2, 12.0, 720),  // 12:00 - noon
                CarbRatioScheduleEntry::new(3, 9.0, 1080),  // 18:00 - evening
            ],
            ..Default::default()
        };

        // 03:00 - should be in midnight window (8.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 3, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 8.0).abs() < 0.1);

        // 08:00 - should be in morning window (10.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 8, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 10.0).abs() < 0.1);

        // 14:00 - should be in noon window (12.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 14, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 12.0).abs() < 0.1);

        // 20:00 - should be in evening window (9.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 20, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 9.0).abs() < 0.1);
    }

    #[test]
    fn test_carb_ratio_lookup_boundary_conditions() {
        let profile = Profile {
            carb_ratio: 10.0,
            carb_ratio_profile: vec![
                CarbRatioScheduleEntry::new(0, 8.0, 0),     // 00:00
                CarbRatioScheduleEntry::new(1, 12.0, 720),  // 12:00
            ],
            ..Default::default()
        };

        // Exactly at midnight - should be first entry (8.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 0, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 8.0).abs() < 0.1);

        // Exactly at noon - should switch to second entry (12.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 12, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 12.0).abs() < 0.1);

        // 11:59 - still first entry (8.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 11, 59, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 8.0).abs() < 0.1);

        // 23:59 - last entry wraps, should be second entry (12.0)
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 23, 59, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 12.0).abs() < 0.1);
    }

    #[test]
    fn test_carb_ratio_lookup_unsorted_entries() {
        // Entries are not in order by index - should still work
        let profile = Profile {
            carb_ratio: 10.0,
            carb_ratio_profile: vec![
                CarbRatioScheduleEntry::new(2, 12.0, 720),  // 12:00 - added first
                CarbRatioScheduleEntry::new(0, 8.0, 0),     // 00:00 - added second
                CarbRatioScheduleEntry::new(1, 10.0, 360),  // 06:00 - added third
            ],
            ..Default::default()
        };

        // Should sort by index and work correctly
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 8, 0, 0).unwrap();
        assert!((carb_ratio_lookup(&profile, time) - 10.0).abs() < 0.1);
    }
}
