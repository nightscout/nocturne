using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities.OwnedTypes;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Shared EF Core configuration for TreatmentEntity owned types.
/// Used by both NocturneDbContext and MigrationDataContext to keep column mappings in sync.
/// </summary>
public static class TreatmentEntityConfiguration
{
    public static void ConfigureOwnedTypes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TreatmentEntity>(entity =>
        {
            entity.OwnsOne(t => t.GlucoseData, glucose =>
            {
                glucose.Property(g => g.Glucose).HasColumnName("glucose");
                glucose.Property(g => g.GlucoseType).HasColumnName("glucoseType").HasMaxLength(50);
                glucose.Property(g => g.Mgdl).HasColumnName("mgdl");
                glucose.Property(g => g.Mmol).HasColumnName("mmol");
                glucose.Property(g => g.Units).HasColumnName("units").HasMaxLength(10);
            });

            entity.OwnsOne(t => t.Nutritional, nutr =>
            {
                nutr.Property(n => n.Protein).HasColumnName("protein");
                nutr.Property(n => n.Fat).HasColumnName("fat");
                nutr.Property(n => n.FoodType).HasColumnName("foodType").HasMaxLength(255);
                nutr.Property(n => n.CarbTime).HasColumnName("carbTime");
                nutr.Property(n => n.AbsorptionTime).HasColumnName("absorptionTime");
            });

            entity.OwnsOne(t => t.Basal, basal =>
            {
                basal.Property(b => b.Rate).HasColumnName("rate");
                basal.Property(b => b.Percent).HasColumnName("percent");
                basal.Property(b => b.Absolute).HasColumnName("absolute");
                basal.Property(b => b.Relative).HasColumnName("relative");
                basal.Property(b => b.DurationType).HasColumnName("durationType").HasMaxLength(50);
                basal.Property(b => b.EndMills).HasColumnName("endmills");
                basal.Property(b => b.DurationInMilliseconds).HasColumnName("duration_in_milliseconds");
            });

            entity.OwnsOne(t => t.BolusCalc, bolus =>
            {
                bolus.Property(b => b.InsulinRecommendationForCarbs).HasColumnName("insulin_recommendation_for_carbs");
                bolus.Property(b => b.InsulinRecommendationForCorrection).HasColumnName("insulin_recommendation_for_correction");
                bolus.Property(b => b.InsulinProgrammed).HasColumnName("insulin_programmed");
                bolus.Property(b => b.InsulinDelivered).HasColumnName("insulin_delivered");
                bolus.Property(b => b.InsulinOnBoard).HasColumnName("insulin_on_board");
                bolus.Property(b => b.BloodGlucoseInput).HasColumnName("blood_glucose_input");
                bolus.Property(b => b.BloodGlucoseInputSource).HasColumnName("blood_glucose_input_source").HasMaxLength(50);
                bolus.Property(b => b.CalculationType).HasColumnName("calculation_type").HasMaxLength(20);
                bolus.Property(b => b.BolusCalcJson).HasColumnName("boluscalc").HasColumnType("jsonb").HasDefaultValue("{}");
                bolus.Property(b => b.BolusCalculatorResult).HasColumnName("bolus_calculator_result");
                bolus.Property(b => b.EnteredInsulin).HasColumnName("enteredinsulin");
                bolus.Property(b => b.SplitNow).HasColumnName("splitNow");
                bolus.Property(b => b.SplitExt).HasColumnName("splitExt");
                bolus.Property(b => b.CR).HasColumnName("CR");
                bolus.Property(b => b.PreBolus).HasColumnName("preBolus");
            });

            entity.OwnsOne(t => t.ProfileData, profile =>
            {
                profile.Property(p => p.Profile).HasColumnName("profile").HasMaxLength(255);
                profile.Property(p => p.ProfileJson).HasColumnName("profileJson").HasColumnType("jsonb").HasDefaultValue("null");
                profile.Property(p => p.EndProfile).HasColumnName("endprofile").HasMaxLength(255);
                profile.Property(p => p.CircadianPercentageProfile).HasColumnName("CircadianPercentageProfile");
                profile.Property(p => p.Percentage).HasColumnName("percentage");
                profile.Property(p => p.Timeshift).HasColumnName("timeshift");
                profile.Property(p => p.InsulinNeedsScaleFactor).HasColumnName("insulinNeedsScaleFactor");
            });

            entity.OwnsOne(t => t.Aaps, aaps =>
            {
                aaps.Property(a => a.PumpId).HasColumnName("pump_id");
                aaps.Property(a => a.PumpSerial).HasColumnName("pump_serial").HasMaxLength(255);
                aaps.Property(a => a.PumpType).HasColumnName("pump_type").HasMaxLength(100);
                aaps.Property(a => a.EndId).HasColumnName("end_id");
                aaps.Property(a => a.IsValid).HasColumnName("is_valid");
                aaps.Property(a => a.IsReadOnly).HasColumnName("is_read_only");
                aaps.Property(a => a.IsBasalInsulin).HasColumnName("is_basal_insulin");
                aaps.Property(a => a.OriginalDuration).HasColumnName("original_duration");
                aaps.Property(a => a.OriginalProfileName).HasColumnName("original_profile_name").HasMaxLength(255);
                aaps.Property(a => a.OriginalPercentage).HasColumnName("original_percentage");
                aaps.Property(a => a.OriginalTimeshift).HasColumnName("original_timeshift");
                aaps.Property(a => a.OriginalCustomizedName).HasColumnName("original_customized_name").HasMaxLength(255);
                aaps.Property(a => a.OriginalEnd).HasColumnName("original_end");
            });

            entity.OwnsOne(t => t.Loop, loop =>
            {
                loop.Property(l => l.RemoteCarbs).HasColumnName("remoteCarbs");
                loop.Property(l => l.RemoteAbsorption).HasColumnName("remoteAbsorption");
                loop.Property(l => l.RemoteBolus).HasColumnName("remoteBolus");
                loop.Property(l => l.Otp).HasColumnName("otp").HasMaxLength(255);
                loop.Property(l => l.ReasonDisplay).HasColumnName("reasonDisplay").HasMaxLength(255);
            });
        });
    }
}
