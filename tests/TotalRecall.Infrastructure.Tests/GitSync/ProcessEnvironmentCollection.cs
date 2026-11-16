using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Tests that set process environment variables run alone, so they cannot leak into another test.</summary>
[CollectionDefinition("process-environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}
