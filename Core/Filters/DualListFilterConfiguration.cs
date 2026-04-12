using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 双名单过滤配置：白名单（包含）+ 黑名单（排除）。
    /// </summary>
    public class DualListFilterConfiguration
    {
        public FilterRuleSet Whitelist { get; init; } = new();
        public FilterRuleSet Blacklist { get; init; } = new();

        public bool IsAllowAll => !Whitelist.HasAnyRule && !Blacklist.HasAnyRule;

        /// <summary>
        /// 兼容历史单名单配置：
        /// - "全部允许" 或未指定模式 => 迁移为白/黑均为空
        /// - 白名单模式 => 历史规则迁移到白名单
        /// - 黑名单模式 => 历史规则迁移到黑名单
        /// </summary>
        public static DualListFilterConfiguration FromLegacy(LegacyFilterConfiguration legacy)
        {
            if (legacy == null)
            {
                return new DualListFilterConfiguration();
            }

            if (legacy.FilterTypeNone || (!legacy.FilterTypeWhitelist && !legacy.FilterTypeBlacklist))
            {
                return new DualListFilterConfiguration();
            }

            var migratedRules = new FilterRuleSet
            {
                ExtensionFilterText = legacy.ExtensionFilterText,
                MinSizeMB = legacy.MinSizeMB,
                MaxSizeMB = legacy.MaxSizeMB,
                NewerThanHours = legacy.NewerThanHours,
                RegexPattern = legacy.RegexPattern
            };

            return new DualListFilterConfiguration
            {
                Whitelist = legacy.FilterTypeWhitelist ? migratedRules : new FilterRuleSet(),
                Blacklist = legacy.FilterTypeBlacklist ? migratedRules : new FilterRuleSet()
            };
        }
    }

    /// <summary>
    /// 历史过滤配置（用于迁移到双名单结构）。
    /// </summary>
    public class LegacyFilterConfiguration
    {
        public bool FilterTypeNone { get; init; }
        public bool FilterTypeWhitelist { get; init; }
        public bool FilterTypeBlacklist { get; init; }
        public string? ExtensionFilterText { get; init; }
        public string? MinSizeMB { get; init; }
        public string? MaxSizeMB { get; init; }
        public string? NewerThanHours { get; init; }
        public string? RegexPattern { get; init; }
    }

    /// <summary>
    /// 二级规则集合：扩展名、大小、时间（小时）、正则。
    /// </summary>
    public class FilterRuleSet
    {
        public string? ExtensionFilterText { get; init; }
        public string? MinSizeMB { get; init; }
        public string? MaxSizeMB { get; init; }
        public string? NewerThanHours { get; init; }
        public string? RegexPattern { get; init; }

        public bool HasAnyRule =>
            ParseExtensions().Any() ||
            ParseMinSizeBytes().HasValue ||
            ParseMaxSizeBytes().HasValue ||
            ParseNewerThanHours().HasValue ||
            !string.IsNullOrWhiteSpace(RegexPattern);

        public IReadOnlyList<string> ParseExtensions()
        {
            if (string.IsNullOrWhiteSpace(ExtensionFilterText))
            {
                return Array.Empty<string>();
            }

            return ExtensionFilterText
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public long? ParseMinSizeBytes() => ParseSizeMbToBytes(MinSizeMB);
        public long? ParseMaxSizeBytes() => ParseSizeMbToBytes(MaxSizeMB);
        public int? ParseNewerThanHours() => ParsePositiveInt(NewerThanHours);

        private static long? ParseSizeMbToBytes(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) &&
                !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out mb))
            {
                return null;
            }

            if (mb < 0)
            {
                return null;
            }

            return (long)(mb * 1024 * 1024);
        }

        private static int? ParsePositiveInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) &&
                !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out result))
            {
                return null;
            }

            return result >= 0 ? result : null;
        }
    }
}
