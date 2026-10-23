using System;

namespace FolderSync.Core.Filters
{
    /// <summary>
    /// 过滤规则无法可靠评估时抛出。调用方不得把未知结果当作“允许同步”。
    /// </summary>
    public sealed class FilterEvaluationException : Exception
    {
        public FilterEvaluationException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
