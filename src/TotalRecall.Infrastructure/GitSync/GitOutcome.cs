namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The success value of a git operation that returns nothing (not named <c>Unit</c>, which is an F# type).</summary>
public readonly struct NoValue;

/// <summary>
/// The result of a typed git operation: a value, or the failed <see cref="GitResult"/> (its <c>Failure</c> says why).
/// A <c>default</c> outcome is a failure with no result and refuses to give a value or a failure. Outcomes have no
/// value equality (a failed result carries a byte array); compare the parts.
/// </summary>
public readonly struct GitOutcome<T>
{
    private readonly bool _ok;
    private readonly T? _value;
    private readonly GitResult? _failure;

    private GitOutcome(bool ok, T? value, GitResult? failure)
    {
        _ok = ok;
        _value = value;
        _failure = failure;
    }

    public bool IsOk => _ok;

    /// <summary>The value. Only valid when <see cref="IsOk"/>.</summary>
    public T Value => _ok ? _value! : throw new InvalidOperationException("the operation failed: there is no value");

    /// <summary>The failed call. Only valid when not <see cref="IsOk"/>.</summary>
    public GitResult Failure =>
        !_ok && _failure is not null ? _failure : throw new InvalidOperationException("the operation succeeded or has no result");

    public override string ToString() =>
        _ok ? $"Ok({_value})" : _failure is null ? "default" : $"Failed(exit {_failure.ExitCode?.ToString() ?? "none"}, {_failure.Failure})";

    public static GitOutcome<T> Ok(T value) => new(true, value, null);

    public static GitOutcome<T> Fail(GitResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(false, default, failure);
    }
}
