using System;
using System.Globalization;

namespace LocalEnvManager.Models
{
    /// <summary>
    /// Formats run timestamps for the Execute tab's "上次构建" column. Kept as a
    /// pure static helper (now is injected) so the relative-time rules are unit
    /// testable without a clock or a dispatcher.
    /// </summary>
    public static class RelativeTime
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        /// <summary>Compact relative label: 刚刚 / N 分钟前 / HH:mm / 昨天 HH:mm / MM-dd HH:mm / yyyy-MM-dd.</summary>
        public static string Format(DateTime? time, DateTime now)
        {
            if (time is null) return "—";
            var value = time.Value;
            var delta = now - value;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

            if (delta.TotalSeconds < 60) return "刚刚";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
            if (value.Date == now.Date) return value.ToString("HH:mm", Culture);
            if (value.Date == now.Date.AddDays(-1)) return $"昨天 {value.ToString("HH:mm", Culture)}";
            if (value.Year == now.Year) return value.ToString("MM-dd HH:mm", Culture);
            return value.ToString("yyyy-MM-dd", Culture);
        }

        /// <summary>Full timestamp used in tooltips; "从未运行" when null.</summary>
        public static string FormatFull(DateTime? time)
            => time?.ToString("yyyy-MM-dd HH:mm:ss", Culture) ?? "从未运行";

        /// <summary>Elapsed duration for a running row: 45s / 1m23s / 1h02m.</summary>
        public static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            var total = (int)elapsed.TotalSeconds;
            if (total < 60) return $"{total}s";
            if (total < 3600) return $"{total / 60}m{total % 60:D2}s";
            return $"{total / 3600}h{(total % 3600) / 60:D2}m";
        }
    }
}