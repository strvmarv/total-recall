using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Cli.Tests;

/// <summary>A fact that needs a <c>git</c> on the PATH; skipped with a reason when there is none.</summary>
internal sealed class GitAvailableFactAttribute : FactAttribute
{
    public GitAvailableFactAttribute()
    {
        if (GitExecutable.TryFind() is null)
            Skip = "git is not on the PATH";
    }
}
