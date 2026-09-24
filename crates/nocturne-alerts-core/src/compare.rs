//! `ComparisonOps` and the elapsed-time numeric conventions.
//!
//! Elapsed time is `ticks * (1.0 / TicksPerX)` (multiplication by a reciprocal
//! constant, not division) and, where a leaf compares in decimal, goes through
//! the .NET `(decimal)double` conversion; see `docs/alerts/engine-semantics.md`
//! §1.3. Both are reproduced exactly, including the conversion's double-rounding.

use chrono::TimeDelta;
use rust_decimal::Decimal;

const TICKS_PER_MINUTE: f64 = 600_000_000.0;
const TICKS_PER_HOUR: f64 = 36_000_000_000.0;
const TICKS_PER_DAY: f64 = 864_000_000_000.0;
const MINUTES_PER_TICK: f64 = 1.0 / TICKS_PER_MINUTE;
const HOURS_PER_TICK: f64 = 1.0 / TICKS_PER_HOUR;
const DAYS_PER_TICK: f64 = 1.0 / TICKS_PER_DAY;

/// .NET `TimeSpan` ticks (100 ns units) for a chrono duration; `None` past
/// the `i64` tick range, which no .NET `TimeSpan` can hold.
pub fn ticks(d: TimeDelta) -> Option<i64> {
    d.num_seconds()
        .checked_mul(10_000_000)?
        .checked_add(i64::from(d.subsec_nanos()) / 100)
}

/// `TimeSpan.TotalMinutes` (double): `ticks * MinutesPerTick`.
pub fn total_minutes(d: TimeDelta) -> Option<f64> {
    ticks(d).map(|t| t as f64 * MINUTES_PER_TICK)
}

/// `TimeSpan.TotalHours` (double): `ticks * HoursPerTick`.
pub fn total_hours(d: TimeDelta) -> Option<f64> {
    ticks(d).map(|t| t as f64 * HOURS_PER_TICK)
}

/// `TimeSpan.TotalDays` (double): `ticks * DaysPerTick`.
pub fn total_days(d: TimeDelta) -> Option<f64> {
    ticks(d).map(|t| t as f64 * DAYS_PER_TICK)
}

/// Exact `f64` powers of ten `1e0..=1e28`, the scale factors the conversion
/// multiplies or divides by.
const F64_POWERS_OF_10: [f64; 29] = [
    1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16,
    1e17, 1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
];

/// Largest decimal scale.
const MAX_SCALE: i32 = 28;

/// The .NET `(decimal)double` conversion (`VarDecFromR8`). The double is
/// scaled by a power of ten estimated from its binary exponent, in `f64`
/// arithmetic, then rounded half-to-even to a 15-digit integer; the scaling
/// can itself round, so the result is not always the correctly rounded
/// 15-digit value. Trailing zeros are stripped from the scale, at most 14.
/// Magnitudes below about `1e-28` become zero; `None` where .NET throws
/// (NaN, infinity, beyond the decimal range), which callers map to `false`.
pub fn decimal_from_f64_cs(v: f64) -> Option<Decimal> {
    let biased_exponent = ((v.to_bits() >> 52) & 0x7FF) as i32;
    let exp = biased_exponent - 1022;
    if exp < -94 {
        return Some(Decimal::ZERO);
    }
    if exp > 96 {
        return None;
    }

    let mut dbl = v.abs();
    // log10(2) as a 16-bit fixed-point multiplier.
    let mut power = 14 - ((exp * 19728) >> 16);
    if power >= 0 {
        power = power.min(MAX_SCALE);
        dbl *= F64_POWERS_OF_10[power as usize];
    } else if power != -1 || dbl >= 1e15 {
        dbl /= F64_POWERS_OF_10[(-power) as usize];
    } else {
        power = 0;
    }
    if dbl < 1e14 && power < MAX_SCALE {
        dbl *= 10.0;
        power += 1;
    }

    let mut mantissa = dbl.round_ties_even() as i128;
    if mantissa == 0 {
        return Some(Decimal::ZERO);
    }

    let mut d = if power < 0 {
        Decimal::try_from_i128_with_scale(mantissa * 10i128.pow(power.unsigned_abs()), 0).ok()?
    } else {
        let mut strippable = power.min(14);
        while strippable > 0 && mantissa % 10 == 0 {
            mantissa /= 10;
            power -= 1;
            strippable -= 1;
        }
        Decimal::try_from_i128_with_scale(mantissa, power.unsigned_abs()).ok()?
    };
    d.set_sign_negative(v.is_sign_negative());
    Some(d)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cast_rounds_to_15_significant_digits() {
        // 1/3 minute elapsed: double = 0.3333333333333333…, C# decimal cast
        // keeps 15 significant digits.
        let d = decimal_from_f64_cs(1.0 / 3.0).unwrap();
        assert_eq!(d.to_string(), "0.333333333333333");
    }

    /// `(value, .NET (decimal)value .ToString())`, from a `dotnet run` probe.
    const DOTNET_VECTORS: [(f64, &str); 23] = [
        (48.81111111111805, "48.811111111118"),
        (749.3333333334166, "749.333333333416"),
        (582.3666666665555, "582.366666666556"),
        (96599.99999916334, "96599.9999991634"),
        (1.0 / 3.0, "0.333333333333333"),
        (15.0, "15"),
        (0.1, "0.1"),
        (1e-20, "0.00000000000000000001"),
        (1e-28, "0.0000000000000000000000000001"),
        (1.5e-29, "0"),
        (4e-29, "0"),
        (123456789012345678.0, "123456789012346000"),
        (0.30000000000000004, "0.3"),
        (1e15, "1000000000000000"),
        (999999999999999.5, "1000000000000000"),
        (100000000000000.5, "100000000000000"),
        (2.5, "2.5"),
        (0.00012345678901234567, "0.000123456789012346"),
        (-12.3456789, "-12.3456789"),
        (1e-5, "0.00001"),
        (60.0, "60"),
        (1440.0000000001, "1440.0000000001"),
        (-0.0, "0"),
    ];

    #[test]
    fn cast_matches_dotnet_var_dec_from_r8() {
        for (v, expected) in DOTNET_VECTORS {
            let d = decimal_from_f64_cs(v).unwrap_or_else(|| panic!("{v:?} overflowed"));
            assert_eq!(d.to_string(), expected, "(decimal){v:?}");
        }
    }

    #[test]
    fn cast_is_none_where_dotnet_throws() {
        assert_eq!(decimal_from_f64_cs(7.922816251426434e28), None);
        assert_eq!(decimal_from_f64_cs(f64::NAN), None);
        assert_eq!(decimal_from_f64_cs(f64::INFINITY), None);
        assert_eq!(decimal_from_f64_cs(f64::NEG_INFINITY), None);
    }

    #[test]
    fn whole_minutes_cast_exactly() {
        let d = TimeDelta::minutes(15);
        let cast = decimal_from_f64_cs(total_minutes(d).unwrap()).unwrap();
        assert_eq!(cast, Decimal::from(15));
    }

    #[test]
    fn ticks_past_the_i64_range_are_none() {
        assert_eq!(ticks(TimeDelta::MAX), None);
        assert_eq!(ticks(TimeDelta::MIN), None);
        assert_eq!(ticks(TimeDelta::minutes(1)), Some(600_000_000));
    }
}
