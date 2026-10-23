using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

/// <summary>A meal: announced ones are logged and bolused at the profile's carb ratio, hidden ones neither.</summary>
public readonly record struct SimulatedMeal(long Mills, double Grams, bool Announced = true);

/// <summary>
/// A closed loop run in five-minute steps against a body with known true settings, while the profile
/// the loop and the calculator read may be wrong. Insulin acts on a triangular curve; the body's
/// curve (<see cref="BodyDiaSteps"/>) may differ from the one the loop and the reported IOB use
/// (<see cref="ModelDiaSteps"/>), and carbs absorb evenly over <see cref="AbsorptionSteps"/> while the
/// loop's carbs on board assume three hours. A basal of the true rate holds glucose flat.
/// </summary>
public sealed record LoopSimulation
{
    public const long StepMs = 5 * LocalSlotBinning.MinuteMs;
    private const double StepHours = 5.0 / 60;
    private const int ModelAbsorptionSteps = 36;

    public required TimeZoneInfo Zone { get; init; }

    /// <summary>The body's basal need by local hour, U/h.</summary>
    public Func<int, double> TrueBasal { get; init; } = _ => 1.0;
    public double TrueIsf { get; init; } = 50;
    public double TrueCr { get; init; } = 10;
    public double ProfileBasal { get; init; } = 1.0;
    public double ProfileIsf { get; init; } = 50;
    public double ProfileCr { get; init; } = 10;

    /// <summary>Automatic boluses for half of what the loop wants, as oref's SMB does; else temp basals alone.</summary>
    public bool Smb { get; init; }

    public double LoopTarget { get; init; } = 110;
    public (double Low, double High) TargetRange { get; init; } = (90, 130);

    /// <summary>Uniform noise on each reading, ± this, mg/dL, from a fixed seed.</summary>
    public double NoiseMgdl { get; init; } = 2;

    public int BodyDiaSteps { get; init; } = 48;
    public int BodyPeakSteps { get; init; } = 15;
    public int ModelDiaSteps { get; init; } = 48;
    public int ModelPeakSteps { get; init; } = 15;
    public int AbsorptionSteps { get; init; } = 36;

    public IReadOnlyList<SimulatedMeal> Meals { get; init; } = [];
    public IReadOnlyList<(long Mills, double Units)> Corrections { get; init; } = [];

    public List<GlucoseSample> Glucose { get; private set; } = [];
    public List<CarbEvent> Carbs { get; private set; } = [];
    public List<InsulinDose> Doses { get; private set; } = [];
    public List<DeliveredBasalSegment> Delivered { get; private set; } = [];
    public List<IobCobTick> IobCob { get; private set; } = [];

    /// <summary>Runs from <paramref name="from"/> to <paramref name="to"/>, starting in steady state at the loop's target.</summary>
    public LoopSimulation Run(long from, long to)
    {
        (Glucose, Carbs, Doses, Delivered, IobCob) = ([], [], [], [], []);
        var body = Activity(BodyDiaSteps, BodyPeakSteps);
        var modelRemaining = Remaining(Activity(ModelDiaSteps, ModelPeakSteps));
        var history = Math.Max(BodyDiaSteps, ModelDiaSteps);
        var steps = (int)((to - from) / StepMs);
        var absolute = new double[steps + history + 1];
        var scheduled = new double[steps + history + 1];
        var steady = TrueBasal(LocalSlotBinning.LocalTime(from, Zone).Hour) * StepHours;
        for (var i = 0; i <= history; i++)
        {
            absolute[i] = steady;
            scheduled[i] = ProfileBasal * StepHours;
        }
        var carbEffect = new double[steps + AbsorptionSteps + 1];
        var cob = new double[steps + ModelAbsorptionSteps + 1];
        var random = new Random(2392);
        var glucose = LoopTarget;
        var meals = Meals.ToLookup(m => (int)((m.Mills - from) / StepMs));
        var corrections = Corrections.ToLookup(c => (int)((c.Mills - from) / StepMs), c => c.Units);

        for (var j = 0; j < steps; j++)
        {
            var t = from + j * StepMs;
            var k = j + history;
            foreach (var meal in meals[j])
            {
                for (var s = 0; s < AbsorptionSteps; s++)
                    carbEffect[j + s] += TrueIsf / TrueCr * meal.Grams / AbsorptionSteps;
                if (!meal.Announced)
                    continue;
                for (var s = 0; s < ModelAbsorptionSteps; s++)
                    cob[j + s] += meal.Grams * (ModelAbsorptionSteps - s) / ModelAbsorptionSteps;
                Carbs.Add(new CarbEvent(t, meal.Grams));
                Bolus(t, meal.Grams / ProfileCr, automatic: false, absolute, k);
            }
            foreach (var units in corrections[j])
                Bolus(t, units, automatic: false, absolute, k);

            double iob = 0;
            for (var i = Math.Max(0, k - ModelDiaSteps + 1); i <= k; i++)
                iob += (absolute[i] - scheduled[i]) * modelRemaining[k - i];
            var eventual = glucose - ProfileIsf * iob + ProfileIsf / ProfileCr * cob[j];
            var required = (eventual - LoopTarget) / ProfileIsf;
            var rate = Math.Clamp(ProfileBasal + 2 * required, 0, 4 * ProfileBasal);
            if (Smb && required > 0)
            {
                var smb = Math.Floor(Math.Min(0.5 * required, 1) / 0.05) * 0.05;
                if (smb > 0)
                {
                    Bolus(t, smb, automatic: true, absolute, k);
                    iob += smb;
                }
                rate = ProfileBasal;
            }

            Glucose.Add(new GlucoseSample(t, Math.Round(glucose + (random.NextDouble() * 2 - 1) * NoiseMgdl, 1)));
            IobCob.Add(new IobCobTick(t, iob, IobCobSource.Device, cob[j], IobCobSource.Device));
            Delivered.Add(new DeliveredBasalSegment(t, t + StepMs, rate, BasalDeliveryOrigin.Algorithm));
            absolute[k + 1] += rate * StepHours;
            scheduled[k + 1] += ProfileBasal * StepHours;

            double acting = 0;
            for (var i = Math.Max(0, k - BodyDiaSteps + 1); i <= k; i++)
                acting += absolute[i] * body[k - i];
            var need = TrueBasal(LocalSlotBinning.LocalTime(t, Zone).Hour) * StepHours;
            glucose = Math.Max(39, glucose + TrueIsf * (need - acting) + carbEffect[j]);
        }
        return this;
    }

    public List<RateSegment> Schedule(long from, long to) => [new RateSegment(from, to, ProfileBasal)];

    public TherapyTimeline Therapy(long from, long to) => new([new TherapySegment(from, to, new TherapySnapshot(
        dia: ModelDiaSteps * StepHours,
        peakMinutes: ModelPeakSteps * 5,
        carbsPerHour: 30,
        timezone: Zone,
        ccpPercentage: null,
        ccpTimeshiftMs: 0,
        sensitivityEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = ProfileIsf }],
        carbRatioEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = ProfileCr }],
        basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = ProfileBasal }]))]);

    private void Bolus(long mills, double units, bool automatic, double[] absolute, int step)
    {
        absolute[step] += units;
        Doses.Add(new InsulinDose(mills, units, automatic));
    }

    /// <summary>The share of a dose acting in each step after it, on a triangle peaking at <paramref name="peak"/>.</summary>
    private static double[] Activity(int dia, int peak)
    {
        var weights = new double[dia];
        for (var k = 0; k < dia; k++)
            weights[k] = k < peak ? (k + 0.5) / peak : (dia - k - 0.5) / (dia - peak);
        var total = weights.Sum();
        return [.. weights.Select(w => w / total)];
    }

    /// <summary>The share of a dose still to act <c>k</c> steps after it.</summary>
    private static double[] Remaining(double[] activity) =>
        [.. Enumerable.Range(0, activity.Length).Select(k => 1 - activity.Take(k).Sum())];
}
