//! Profile types for user settings and schedules

use chrono::{NaiveTime, Timelike};

#[cfg(feature = "serde")]
use serde::{Deserialize, Serialize};

use crate::insulin::InsulinCurve;

/// Main profile containing all user settings
#[derive(Debug, Clone)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
#[cfg_attr(feature = "serde", serde(rename_all = "camelCase"))]
pub struct Profile {
    /// Duration of insulin action (hours)
    pub dia: f64,

    /// Current scheduled basal rate (U/hr)
    pub current_basal: f64,

    /// Maximum IOB allowed (units)
    pub max_iob: f64,

    /// Maximum daily basal rate from schedule
    pub max_daily_basal: f64,

    /// Absolute maximum basal rate (U/hr)
    pub max_basal: f64,

    /// Minimum BG target (mg/dL)
    pub min_bg: f64,

    /// Maximum BG target (mg/dL)
    pub max_bg: f64,

    /// Insulin sensitivity factor (mg/dL per unit)
    #[cfg_attr(feature = "serde", serde(alias = "sensitivity"))]
    pub sens: f64,

    /// Carb ratio (grams per unit)
    pub carb_ratio: f64,

    /// Insulin curve type
    #[cfg_attr(feature = "serde", serde(default))]
    pub curve: InsulinCurve,

    /// Insulin peak time (minutes) - computed from curve or custom setting
    #[cfg_attr(feature = "serde", serde(default = "default_peak_time"))]
    pub peak: u32,

    /// Use custom peak time instead of curve default
    #[cfg_attr(feature = "serde", serde(default))]
    pub use_custom_peak_time: bool,

    /// Custom insulin peak time (minutes)
    #[cfg_attr(feature = "serde", serde(default = "default_peak_time"))]
    pub insulin_peak_time: u32,

    /// Minimum autosens ratio
    #[cfg_attr(feature = "serde", serde(default = "default_autosens_min"))]
    pub autosens_min: f64,

    /// Maximum autosens ratio
    #[cfg_attr(feature = "serde", serde(default = "default_autosens_max"))]
    pub autosens_max: f64,

    /// Minimum 5-minute carb impact (mg/dL/5min)
    #[cfg_attr(feature = "serde", serde(default = "default_min_5m_carbimpact"))]
    pub min_5m_carbimpact: f64,

    /// Maximum COB (grams)
    #[cfg_attr(feature = "serde", serde(default = "default_max_cob"))]
    pub max_cob: f64,

    /// Maximum meal absorption time (hours)
    #[cfg_attr(feature = "serde", serde(default = "default_max_meal_absorption_time"))]
    pub max_meal_absorption_time: f64,

    // ============ SMB Settings ============
    /// Always enable SMB (unless disabled by high temp target)
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_smb_always: bool,

    /// Enable SMB while COB is positive
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_smb_with_cob: bool,

    /// Enable SMB for eating soon temp targets
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_smb_with_temptarget: bool,

    /// Enable SMB for 6h after carb entry
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_smb_after_carbs: bool,

    /// Enable SMB when BG is above high threshold
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_smb_high_bg: bool,

    /// BG threshold for high BG SMB (mg/dL)
    #[cfg_attr(feature = "serde", serde(default = "default_smb_high_bg_target"))]
    pub enable_smb_high_bg_target: f64,

    /// Allow SMB with high temp targets
    #[cfg_attr(feature = "serde", serde(default))]
    pub allow_smb_with_high_temptarget: bool,

    /// Maximum minutes of basal as SMB with COB
    #[cfg_attr(feature = "serde", serde(default = "default_max_smb_basal_minutes"))]
    pub max_smb_basal_minutes: u32,

    /// Maximum minutes of basal as SMB with UAM (IOB > COB)
    #[cfg_attr(feature = "serde", serde(default = "default_max_smb_basal_minutes"))]
    pub max_uam_smb_basal_minutes: u32,

    /// Minimum interval between SMBs (minutes)
    #[cfg_attr(feature = "serde", serde(default = "default_smb_interval"))]
    pub smb_interval: u32,

    /// Minimum bolus increment (units)
    #[cfg_attr(feature = "serde", serde(default = "default_bolus_increment"))]
    pub bolus_increment: f64,

    /// SMB delivery ratio (0.0-1.0)
    #[cfg_attr(feature = "serde", serde(default = "default_smb_delivery_ratio"))]
    pub smb_delivery_ratio: f64,

    // ============ UAM Settings ============
    /// Enable Unannounced Meal detection
    #[cfg_attr(feature = "serde", serde(default))]
    pub enable_uam: bool,

    // ============ Dynamic ISF Settings ============
    /// Use dynamic ISF calculation
    #[cfg_attr(feature = "serde", serde(alias = "useNewFormula", default))]
    pub use_dynamic_isf: bool,

    /// Use sigmoid function for dynamic ISF
    #[cfg_attr(feature = "serde", serde(default))]
    pub sigmoid: bool,

    /// Adjustment factor for logarithmic dynamic ISF
    #[cfg_attr(feature = "serde", serde(default = "default_adjustment_factor"))]
    pub adjustment_factor: f64,

    /// Adjustment factor for sigmoid dynamic ISF
    #[cfg_attr(feature = "serde", serde(default = "default_adjustment_factor_sigmoid"))]
    pub adjustment_factor_sigmoid: f64,

    /// Weight percentage for TDD averaging
    #[cfg_attr(feature = "serde", serde(default = "default_weight_percentage"))]
    pub weight_percentage: f64,

    /// Adjust basal based on TDD ratio
    #[cfg_attr(feature = "serde", serde(default))]
    pub tdd_adj_basal: bool,

    // ============ Temp Target Settings ============
    /// Whether a temp target is currently set
    #[cfg_attr(feature = "serde", serde(default))]
    pub temptarget_set: bool,

    /// High temp target raises sensitivity
    #[cfg_attr(feature = "serde", serde(default))]
    pub high_temptarget_raises_sensitivity: bool,

    /// Low temp target lowers sensitivity
    #[cfg_attr(feature = "serde", serde(default))]
    pub low_temptarget_lowers_sensitivity: bool,

    /// Exercise mode enabled
    #[cfg_attr(feature = "serde", serde(default))]
    pub exercise_mode: bool,

    /// Half basal exercise target (mg/dL)
    #[cfg_attr(feature = "serde", serde(default = "default_half_basal_exercise_target"))]
    pub half_basal_exercise_target: f64,

    // ============ Safety Settings ============
    /// Skip setting neutral temps
    #[cfg_attr(feature = "serde", serde(default))]
    pub skip_neutral_temps: bool,

    /// Rewind resets autosens
    #[cfg_attr(feature = "serde", serde(default = "default_true"))]
    pub rewind_resets_autosens: bool,

    /// A52 risk enable (Bolus Wizard warning)
    #[cfg_attr(feature = "serde", serde(default))]
    pub a52_risk_enable: bool,

    /// Suspend zeros IOB
    #[cfg_attr(feature = "serde", serde(default = "default_true"))]
    pub suspend_zeros_iob: bool,

    // ============ Schedules ============
    /// Basal rate schedule
    #[cfg_attr(feature = "serde", serde(default))]
    pub basal_profile: Vec<BasalScheduleEntry>,

    /// ISF schedule
    #[cfg_attr(feature = "serde", serde(default))]
    pub isf_profile: ISFProfile,

    /// Carb ratio schedule
    #[cfg_attr(feature = "serde", serde(default))]
    pub carb_ratio_profile: Vec<CarbRatioScheduleEntry>,

    // ============ Pump Model ============
    /// Pump model for rounding rules
    #[cfg_attr(feature = "serde", serde(default))]
    pub model: Option<String>,

    /// Output units (mg/dL or mmol/L)
    #[cfg_attr(feature = "serde", serde(default))]
    pub out_units: Option<String>,
}

// Default value functions for serde
fn default_peak_time() -> u32 { 75 }
fn default_autosens_min() -> f64 { 0.7 }
fn default_autosens_max() -> f64 { 1.2 }
fn default_min_5m_carbimpact() -> f64 { 8.0 }
fn default_max_cob() -> f64 { 120.0 }
fn default_max_meal_absorption_time() -> f64 { 6.0 }
fn default_smb_high_bg_target() -> f64 { 110.0 }
fn default_max_smb_basal_minutes() -> u32 { 30 }
fn default_smb_interval() -> u32 { 3 }
fn default_bolus_increment() -> f64 { 0.1 }
fn default_smb_delivery_ratio() -> f64 { 0.5 }
fn default_adjustment_factor() -> f64 { 0.8 }
fn default_adjustment_factor_sigmoid() -> f64 { 0.5 }
fn default_weight_percentage() -> f64 { 0.65 }
fn default_half_basal_exercise_target() -> f64 { 160.0 }
fn default_true() -> bool { true }

impl Default for Profile {
    fn default() -> Self {
        Self {
            dia: 5.0,
            current_basal: 1.0,
            max_iob: 0.0,
            max_daily_basal: 1.0,
            max_basal: 3.5,
            min_bg: 100.0,
            max_bg: 120.0,
            sens: 50.0,
            carb_ratio: 10.0,
            curve: InsulinCurve::RapidActing,
            peak: 75,
            use_custom_peak_time: false,
            insulin_peak_time: 75,
            autosens_min: 0.7,
            autosens_max: 1.2,
            min_5m_carbimpact: 8.0,
            max_cob: 120.0,
            max_meal_absorption_time: 6.0,
            enable_smb_always: false,
            enable_smb_with_cob: false,
            enable_smb_with_temptarget: false,
            enable_smb_after_carbs: false,
            enable_smb_high_bg: false,
            enable_smb_high_bg_target: 110.0,
            allow_smb_with_high_temptarget: false,
            max_smb_basal_minutes: 30,
            max_uam_smb_basal_minutes: 30,
            smb_interval: 3,
            bolus_increment: 0.1,
            smb_delivery_ratio: 0.5,
            enable_uam: false,
            use_dynamic_isf: false,
            sigmoid: false,
            adjustment_factor: 0.8,
            adjustment_factor_sigmoid: 0.5,
            weight_percentage: 0.65,
            tdd_adj_basal: false,
            temptarget_set: false,
            high_temptarget_raises_sensitivity: false,
            low_temptarget_lowers_sensitivity: false,
            exercise_mode: false,
            half_basal_exercise_target: 160.0,
            skip_neutral_temps: false,
            rewind_resets_autosens: true,
            a52_risk_enable: false,
            suspend_zeros_iob: true,
            basal_profile: vec![],
            isf_profile: ISFProfile::default(),
            carb_ratio_profile: vec![],
            model: None,
            out_units: None,
        }
    }
}

impl Profile {
    /// Create a new ProfileBuilder
    pub fn builder() -> ProfileBuilder {
        ProfileBuilder::default()
    }

    /// Get the effective peak time based on curve and custom settings
    pub fn effective_peak_time(&self) -> u32 {
        if self.use_custom_peak_time {
            // Apply limits based on curve
            match self.curve {
                InsulinCurve::RapidActing => self.insulin_peak_time.clamp(50, 120),
                InsulinCurve::UltraRapid => self.insulin_peak_time.clamp(35, 100),
                InsulinCurve::Bilinear => 75, // Fixed for bilinear
            }
        } else {
            self.curve.default_peak()
        }
    }

    /// Get the effective DIA, enforcing minimums
    pub fn effective_dia(&self) -> f64 {
        match self.curve {
            InsulinCurve::Bilinear => self.dia.max(3.0),
            InsulinCurve::RapidActing | InsulinCurve::UltraRapid => self.dia.max(5.0),
        }
    }
}

/// Builder for Profile
#[derive(Debug, Default)]
pub struct ProfileBuilder {
    profile: Profile,
}

impl ProfileBuilder {
    pub fn dia(mut self, dia: f64) -> Self {
        self.profile.dia = dia;
        self
    }

    pub fn sens(mut self, sens: f64) -> Self {
        self.profile.sens = sens;
        self
    }

    pub fn carb_ratio(mut self, carb_ratio: f64) -> Self {
        self.profile.carb_ratio = carb_ratio;
        self
    }

    pub fn curve(mut self, curve: InsulinCurve) -> Self {
        self.profile.curve = curve;
        self
    }

    pub fn current_basal(mut self, basal: f64) -> Self {
        self.profile.current_basal = basal;
        self
    }

    pub fn max_iob(mut self, max_iob: f64) -> Self {
        self.profile.max_iob = max_iob;
        self
    }

    pub fn max_basal(mut self, max_basal: f64) -> Self {
        self.profile.max_basal = max_basal;
        self
    }

    pub fn min_bg(mut self, min_bg: f64) -> Self {
        self.profile.min_bg = min_bg;
        self
    }

    pub fn max_bg(mut self, max_bg: f64) -> Self {
        self.profile.max_bg = max_bg;
        self
    }

    pub fn basal_profile(mut self, profile: Vec<BasalScheduleEntry>) -> Self {
        self.profile.basal_profile = profile;
        self
    }

    pub fn isf_profile(mut self, profile: ISFProfile) -> Self {
        self.profile.isf_profile = profile;
        self
    }

    pub fn build(self) -> Profile {
        self.profile
    }
}

/// Entry in a basal rate schedule
#[derive(Debug, Clone)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
pub struct BasalScheduleEntry {
    /// Index in schedule
    pub i: u32,

    /// Start time as HH:MM:SS string
    #[cfg_attr(feature = "serde", serde(default))]
    pub start: Option<String>,

    /// Basal rate (U/hr)
    pub rate: f64,

    /// Minutes from midnight
    pub minutes: u32,
}

impl BasalScheduleEntry {
    /// Create a new basal schedule entry
    pub fn new(i: u32, rate: f64, minutes: u32) -> Self {
        Self {
            i,
            start: None,
            rate,
            minutes,
        }
    }

    /// Create with a specific start time
    pub fn with_start(i: u32, rate: f64, start: NaiveTime) -> Self {
        let minutes = start.hour() * 60 + start.minute();
        Self {
            i,
            start: Some(start.format("%H:%M:%S").to_string()),
            rate,
            minutes,
        }
    }
}

/// Entry in a carb ratio schedule
#[derive(Debug, Clone)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
pub struct CarbRatioScheduleEntry {
    /// Index in schedule
    pub i: u32,

    /// Start time as HH:MM:SS string
    #[cfg_attr(feature = "serde", serde(default))]
    pub start: Option<String>,

    /// Carb ratio (grams per unit)
    pub ratio: f64,

    /// Minutes from midnight
    pub minutes: u32,
}

impl CarbRatioScheduleEntry {
    /// Create a new carb ratio schedule entry
    pub fn new(i: u32, ratio: f64, minutes: u32) -> Self {
        Self {
            i,
            start: None,
            ratio,
            minutes,
        }
    }

    /// Create with a specific start time
    pub fn with_start(i: u32, ratio: f64, start: NaiveTime) -> Self {
        let minutes = start.hour() * 60 + start.minute();
        Self {
            i,
            start: Some(start.format("%H:%M:%S").to_string()),
            ratio,
            minutes,
        }
    }
}

/// ISF (Insulin Sensitivity Factor) schedule
#[derive(Debug, Clone, Default)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
pub struct ISFProfile {
    /// List of ISF entries
    pub sensitivities: Vec<ISFEntry>,
}

impl ISFProfile {
    /// Create a single-value ISF profile
    pub fn single(sensitivity: f64) -> Self {
        Self {
            sensitivities: vec![ISFEntry {
                offset: 0,
                sensitivity,
                end_offset: None,
            }],
        }
    }
}

/// Entry in an ISF schedule
#[derive(Debug, Clone)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
pub struct ISFEntry {
    /// Minutes from midnight
    pub offset: u32,

    /// Sensitivity (mg/dL per unit)
    pub sensitivity: f64,

    /// End offset for caching (not serialized)
    #[cfg_attr(feature = "serde", serde(skip))]
    pub end_offset: Option<u32>,
}

/// Autosens data containing sensitivity ratio
#[derive(Debug, Clone, Default)]
#[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
pub struct AutosensData {
    /// Sensitivity ratio (1.0 = normal, >1 = resistant, <1 = sensitive)
    #[cfg_attr(feature = "serde", serde(default = "default_ratio"))]
    pub ratio: f64,
}

fn default_ratio() -> f64 { 1.0 }

impl AutosensData {
    /// Create with a specific ratio
    pub fn with_ratio(ratio: f64) -> Self {
        Self { ratio }
    }
}
