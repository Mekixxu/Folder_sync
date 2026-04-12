using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderSync.Core.Filters
{
    public record FilterConflict(string Type, string Message);

    /// <summary>
    /// 检测白/黑名单之间可能同时命中同一目标的冲突。
    /// </summary>
    public static class FilterConflictDetector
    {
        public static IReadOnlyList<FilterConflict> Detect(DualListFilterConfiguration configuration)
        {
            var conflicts = new List<FilterConflict>();
            if (configuration == null)
            {
                return conflicts;
            }

            DetectExtensionConflict(configuration, conflicts);
            DetectRegexConflict(configuration, conflicts);
            DetectSizeConflict(configuration, conflicts);
            DetectTimeConflict(configuration, conflicts);

            return conflicts;
        }

        private static void DetectExtensionConflict(DualListFilterConfiguration configuration, List<FilterConflict> conflicts)
        {
            var white = configuration.Whitelist.ParseExtensions();
            var black = configuration.Blacklist.ParseExtensions();
            if (white.Count == 0 || black.Count == 0)
            {
                return;
            }

            var overlap = white.Intersect(black, StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count > 0)
            {
                conflicts.Add(new FilterConflict(
                    "扩展名冲突",
                    $"白名单和黑名单同时包含扩展名：{string.Join(", ", overlap.Take(5))}"
                ));
            }
        }

        private static void DetectRegexConflict(DualListFilterConfiguration configuration, List<FilterConflict> conflicts)
        {
            var whiteRegex = configuration.Whitelist.RegexPattern?.Trim();
            var blackRegex = configuration.Blacklist.RegexPattern?.Trim();
            if (string.IsNullOrWhiteSpace(whiteRegex) || string.IsNullOrWhiteSpace(blackRegex))
            {
                return;
            }

            if (string.Equals(whiteRegex, blackRegex, StringComparison.Ordinal))
            {
                conflicts.Add(new FilterConflict(
                    "正则冲突",
                    $"白名单与黑名单使用了相同正则：{whiteRegex}"
                ));
            }
        }

        private static void DetectSizeConflict(DualListFilterConfiguration configuration, List<FilterConflict> conflicts)
        {
            var whiteMin = configuration.Whitelist.ParseMinSizeBytes();
            var whiteMax = configuration.Whitelist.ParseMaxSizeBytes();
            var blackMin = configuration.Blacklist.ParseMinSizeBytes();
            var blackMax = configuration.Blacklist.ParseMaxSizeBytes();

            if (!HasAny(whiteMin, whiteMax) || !HasAny(blackMin, blackMax))
            {
                return;
            }

            var overlapMin = Max(whiteMin, blackMin);
            var overlapMax = Min(whiteMax, blackMax);
            if (!RangeOverlaps(overlapMin, overlapMax))
            {
                return;
            }

            var desc = $"白名单大小区间({FormatSizeRange(whiteMin, whiteMax)})与黑名单大小区间({FormatSizeRange(blackMin, blackMax)})存在重叠。";
            conflicts.Add(new FilterConflict("大小区间冲突", desc));
        }

        private static void DetectTimeConflict(DualListFilterConfiguration configuration, List<FilterConflict> conflicts)
        {
            var whiteHours = configuration.Whitelist.ParseNewerThanHours();
            var blackHours = configuration.Blacklist.ParseNewerThanHours();
            if (!whiteHours.HasValue || !blackHours.HasValue)
            {
                return;
            }

            var overlapHours = Math.Max(whiteHours.Value, blackHours.Value);
            conflicts.Add(new FilterConflict(
                "时间冲突",
                $"白名单“新于 {whiteHours} 小时”和黑名单“新于 {blackHours} 小时”存在重叠，至少新于 {overlapHours} 小时的目标会同时命中。"
            ));
        }

        private static bool HasAny(long? min, long? max) => min.HasValue || max.HasValue;
        private static long? Max(long? a, long? b) => !a.HasValue ? b : (!b.HasValue ? a : Math.Max(a.Value, b.Value));
        private static long? Min(long? a, long? b) => !a.HasValue ? b : (!b.HasValue ? a : Math.Min(a.Value, b.Value));

        private static bool RangeOverlaps(long? min, long? max)
        {
            if (min.HasValue && max.HasValue)
            {
                return min.Value <= max.Value;
            }

            return true;
        }

        private static string FormatSizeRange(long? min, long? max)
        {
            static string ToMb(long bytes) => $"{bytes / 1024d / 1024d:0.##} MB";
            var minText = min.HasValue ? ToMb(min.Value) : "-∞";
            var maxText = max.HasValue ? ToMb(max.Value) : "+∞";
            return $"{minText} ~ {maxText}";
        }
    }
}
