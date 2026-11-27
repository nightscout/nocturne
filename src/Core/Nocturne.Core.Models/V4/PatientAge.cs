namespace Nocturne.Core.Models.V4;

/// <summary>A person's age from their date of birth.</summary>
public static class PatientAge
{
    /// <summary>Completed years between <paramref name="dateOfBirth"/> and <paramref name="today"/>.</summary>
    public static int InYears(DateOnly dateOfBirth, DateOnly today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age))
            age--;
        return age;
    }
}
