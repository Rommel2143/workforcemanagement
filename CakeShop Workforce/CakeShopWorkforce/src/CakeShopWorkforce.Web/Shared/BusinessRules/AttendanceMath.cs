namespace CakeShopWorkforce.Web.Shared.BusinessRules;

public enum AttendanceStatus
{
    NotScheduled,   // no schedule and did not work
    RestDay,        // scheduled rest day (NOT absent)
    RestDayWork,    // worked on a rest day
    OnLeave,        // approved leave (NOT absent)
    Pending,        // shift has not started yet / still within grace period
    Present,        // clocked in on time
    Late,           // clocked in after start + grace minutes
    Absent,         // scheduled, did not come, no leave
    Unscheduled     // worked without being scheduled
}

/// <summary>The result of checking one employee on one day.</summary>
public sealed class AttendanceResult
{
    public AttendanceStatus Status { get; set; }
    public int LateMinutes { get; set; }
    public decimal WorkedHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public bool MissingTimeOut { get; set; }

    /// <summary>Counts as "Personnel Present" in every KPI.</summary>
    public bool IsPresent => Status is AttendanceStatus.Present or AttendanceStatus.Late
                                   or AttendanceStatus.Unscheduled or AttendanceStatus.RestDayWork;
}

/// <summary>
/// Turns a schedule + time-in/time-out into a status and worked hours.
///
/// Business rules (from the spec):
///  • Rest day and approved leave are NEVER counted as absent.
///  • Late = clocked in more than "grace minutes" after the scheduled start.
///  • Worked hours = time out − time in − unpaid break.
///  • Overtime = worked hours above the store's regular hours per day (default 8).
/// </summary>
public static class AttendanceMath
{
    public static AttendanceResult Evaluate(
        string? assignmentType,          // "Shift", "RestDay", "Leave" or null (nothing scheduled)
        TimeSpan? scheduledStart,
        TimeSpan? timeIn,
        TimeSpan? timeOut,
        int breakMinutes,
        bool markedAbsent,
        DateTime businessDate,
        DateTime now,
        int graceMinutes,
        decimal regularHoursPerDay)
    {
        var result = new AttendanceResult();
        var clockedIn = timeIn is not null;

        // Worked hours (only when both times are known)
        if (clockedIn)
        {
            if (timeOut is null) result.MissingTimeOut = true;
            else result.WorkedHours = Math.Round(ShiftMath.PaidHours(timeIn!.Value, timeOut.Value, breakMinutes), 2);

            result.OvertimeHours = Math.Max(0, result.WorkedHours - regularHoursPerDay);
        }

        switch (assignmentType)
        {
            case "Leave":
                result.Status = AttendanceStatus.OnLeave;
                result.WorkedHours = 0;
                result.OvertimeHours = 0;
                break;

            case "RestDay":
                result.Status = clockedIn ? AttendanceStatus.RestDayWork : AttendanceStatus.RestDay;
                break;

            case "Shift":
                if (markedAbsent)
                {
                    result.Status = AttendanceStatus.Absent;
                    result.WorkedHours = 0;
                    result.OvertimeHours = 0;
                }
                else if (!clockedIn)
                {
                    // Not in yet. Past days = absent. Today = absent only after start + grace.
                    var deadline = businessDate.Date + (scheduledStart ?? TimeSpan.Zero) + TimeSpan.FromMinutes(graceMinutes);
                    result.Status = now >= deadline ? AttendanceStatus.Absent : AttendanceStatus.Pending;
                }
                else
                {
                    var lateBy = scheduledStart is null ? TimeSpan.Zero : timeIn!.Value - scheduledStart.Value;
                    if (lateBy.TotalMinutes > graceMinutes)
                    {
                        result.Status = AttendanceStatus.Late;
                        result.LateMinutes = (int)lateBy.TotalMinutes;
                    }
                    else
                    {
                        result.Status = AttendanceStatus.Present;
                    }
                }
                break;

            default:
                result.Status = clockedIn ? AttendanceStatus.Unscheduled : AttendanceStatus.NotScheduled;
                break;
        }

        return result;
    }

    public static string Label(AttendanceStatus status) => status switch
    {
        AttendanceStatus.NotScheduled => "Not scheduled",
        AttendanceStatus.RestDay => "Rest day",
        AttendanceStatus.RestDayWork => "Rest-day work",
        AttendanceStatus.OnLeave => "On leave",
        AttendanceStatus.Pending => "Not in yet",
        AttendanceStatus.Present => "Present",
        AttendanceStatus.Late => "Late",
        AttendanceStatus.Absent => "Absent",
        AttendanceStatus.Unscheduled => "Unscheduled",
        _ => status.ToString()
    };

    /// <summary>Badge color: success, warning, danger, info, neutral, brand</summary>
    public static string Tone(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Present => "success",
        AttendanceStatus.Late => "warning",
        AttendanceStatus.Absent => "danger",
        AttendanceStatus.OnLeave => "info",
        AttendanceStatus.RestDayWork or AttendanceStatus.Unscheduled => "brand",
        _ => "neutral"
    };
}
