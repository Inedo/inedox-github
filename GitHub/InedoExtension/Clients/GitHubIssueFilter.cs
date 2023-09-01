#nullable enable
using System;
using System.Text;
using Inedo.Extensibility.IssueTrackers;

namespace Inedo.Extensions.GitHub.Clients;

internal sealed class GitHubIssueFilter : IssuesQueryFilter
{
    public GitHubIssueFilter(string customFilterQueryString)
    {
        this.CustomFilterQueryString = customFilterQueryString;
    }
    public GitHubIssueFilter(string milestoneTitle, string? labels = null)
    {
        if (milestoneTitle != null && AH.ParseInt(milestoneTitle) == null && milestoneTitle != "*" && !string.Equals("none", milestoneTitle, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("milestone must be an integer, or a string of '*' or 'none'.");

        this.Milestone = milestoneTitle;
        this.Labels = labels; ;
    }
    public string? Milestone { get; }
    public string? Labels { get; }
    public string? CustomFilterQueryString { get; }


    public string ToQueryString()
    {
        if (!string.IsNullOrEmpty(this.CustomFilterQueryString))
            return this.CustomFilterQueryString;

        var buffer = new StringBuilder(128);
        buffer.Append("?state=all");
        if (!string.IsNullOrEmpty(this.Milestone))
            buffer.Append("&milestone=" + Uri.EscapeDataString(this.Milestone));
        if (!string.IsNullOrEmpty(this.Labels))
            buffer.Append("&labels=" + Uri.EscapeDataString(this.Labels));
        buffer.Append("&per_page=100");

        return buffer.ToString();
    }
}
