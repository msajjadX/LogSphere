using LogSphere.Core.Models;
using LogSphere.Core.Workers;
using Xunit;

namespace LogSphere.Tests;

public class AlertScheduleTests
{
    private static AlertRule Rule(Action<AlertRule>? mutate = null)
    {
        var r = new AlertRule { Name = "t" };
        mutate?.Invoke(r);
        return r;
    }

    // 2026-07-24 is a Friday. 09:00 UTC = 14:00 in Asia/Karachi (+05).
    private static readonly DateTimeOffset FridayUtc9 = new(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SundayUtc9 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_schedule_means_always_active()
    {
        Assert.True(AlertWorker.IsActiveNow(Rule(), FridayUtc9));
        Assert.True(AlertWorker.IsActiveNow(Rule(), SundayUtc9));
    }

    [Fact]
    public void Day_filter_skips_unselected_days()
    {
        var weekdaysOnly = Rule(r => r.ActiveDays = new short[] { 1, 2, 3, 4, 5 }); // Mon-Fri
        Assert.True(AlertWorker.IsActiveNow(weekdaysOnly, FridayUtc9));
        Assert.False(AlertWorker.IsActiveNow(weekdaysOnly, SundayUtc9));
    }

    [Fact]
    public void Hour_window_gates_in_the_rules_time_zone()
    {
        // active 09:00-17:00 Karachi time; 09:00 UTC = 14:00 PKT (inside), 03:00 UTC = 08:00 PKT (outside)
        var office = Rule(r =>
        {
            r.ActiveFromMinute = 9 * 60;
            r.ActiveToMinute = 17 * 60;
            r.TimeZone = "Asia/Karachi";
        });
        Assert.True(AlertWorker.IsActiveNow(office, FridayUtc9));
        Assert.False(AlertWorker.IsActiveNow(office, new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Overnight_window_wraps_midnight()
    {
        var night = Rule(r =>
        {
            r.ActiveFromMinute = 22 * 60; // 22:00
            r.ActiveToMinute = 6 * 60;    // 06:00 next day
        });
        Assert.True(AlertWorker.IsActiveNow(night, new DateTimeOffset(2026, 7, 24, 23, 30, 0, TimeSpan.Zero)));
        Assert.True(AlertWorker.IsActiveNow(night, new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero)));
        Assert.False(AlertWorker.IsActiveNow(night, new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Time_zone_shifts_the_day_boundary()
    {
        // Saturday 20:00 UTC is already Sunday 01:00 in Karachi — a Sunday-only rule fires
        var sundayKarachi = Rule(r =>
        {
            r.ActiveDays = new short[] { 0 };
            r.TimeZone = "Asia/Karachi";
        });
        Assert.True(AlertWorker.IsActiveNow(sundayKarachi, new DateTimeOffset(2026, 7, 25, 20, 0, 0, TimeSpan.Zero)));
        Assert.False(AlertWorker.IsActiveNow(sundayKarachi, FridayUtc9));
    }

    [Fact]
    public void Unknown_time_zone_falls_back_to_utc_not_never()
    {
        var rule = Rule(r =>
        {
            r.ActiveFromMinute = 8 * 60;
            r.ActiveToMinute = 10 * 60;
            r.TimeZone = "Not/AZone";
        });
        Assert.True(AlertWorker.IsActiveNow(rule, FridayUtc9)); // 09:00 UTC inside 08-10 UTC
    }

    [Fact]
    public void Equal_from_and_to_means_no_hour_restriction()
    {
        var rule = Rule(r => { r.ActiveFromMinute = 300; r.ActiveToMinute = 300; });
        Assert.True(AlertWorker.IsActiveNow(rule, FridayUtc9));
    }
}
