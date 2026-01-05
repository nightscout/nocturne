//! Basal rate schedule lookups

use chrono::{DateTime, Timelike, Utc};
use crate::types::Profile;

/// Look up the basal rate at a specific time
pub fn basal_lookup(profile: &Profile, time: DateTime<Utc>) -> f64 {
    if profile.basal_profile.is_empty() {
        return profile.current_basal;
    }

    let now_minutes = time.hour() * 60 + time.minute();

    // Sort by index
    let mut schedule: Vec<_> = profile.basal_profile.iter().collect();
    schedule.sort_by_key(|e| e.i);

    // Default to last entry
    let mut rate = schedule.last().map(|e| e.rate).unwrap_or(profile.current_basal);

    for i in 0..schedule.len() {
        let entry = schedule[i];
        let next_minutes = if i + 1 < schedule.len() {
            schedule[i + 1].minutes
        } else {
            24 * 60
        };

        if now_minutes >= entry.minutes && now_minutes < next_minutes {
            rate = entry.rate;
            break;
        }
    }

    (rate * 1000.0).round() / 1000.0
}

/// Get the maximum daily basal rate from the schedule
pub fn max_daily_basal(profile: &Profile) -> f64 {
    if profile.basal_profile.is_empty() {
        return profile.current_basal;
    }

    profile.basal_profile
        .iter()
        .map(|e| e.rate)
        .fold(0.0_f64, |a, b| a.max(b))
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;
    use crate::types::BasalScheduleEntry;

    fn make_profile_with_schedule() -> Profile {
        Profile {
            current_basal: 1.0,
            basal_profile: vec![
                BasalScheduleEntry::new(0, 0.8, 0),       // 00:00
                BasalScheduleEntry::new(1, 1.0, 360),     // 06:00
                BasalScheduleEntry::new(2, 1.2, 720),     // 12:00
                BasalScheduleEntry::new(3, 0.9, 1080),    // 18:00
            ],
            ..Default::default()
        }
    }

    #[test]
    fn test_basal_lookup_morning() {
        let profile = make_profile_with_schedule();
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 8, 0, 0).unwrap();

        let rate = basal_lookup(&profile, time);
        assert!((rate - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_basal_lookup_afternoon() {
        let profile = make_profile_with_schedule();
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 14, 0, 0).unwrap();

        let rate = basal_lookup(&profile, time);
        assert!((rate - 1.2).abs() < 0.001);
    }

    #[test]
    fn test_basal_lookup_night() {
        let profile = make_profile_with_schedule();
        let time = Utc.with_ymd_and_hms(2024, 1, 1, 3, 0, 0).unwrap();

        let rate = basal_lookup(&profile, time);
        assert!((rate - 0.8).abs() < 0.001);
    }

    #[test]
    fn test_max_daily_basal() {
        let profile = make_profile_with_schedule();

        let max = max_daily_basal(&profile);
        assert!((max - 1.2).abs() < 0.001);
    }

    #[test]
    fn test_empty_schedule_uses_current() {
        let profile = Profile {
            current_basal: 0.75,
            basal_profile: vec![],
            ..Default::default()
        };

        let rate = basal_lookup(&profile, Utc::now());
        assert!((rate - 0.75).abs() < 0.001);
    }
}
