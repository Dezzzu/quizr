namespace Quizr.Domain;

// Factory methods live here rather than as statics on Result<T> — CA1000
// forbids static members on generic types. Construction normally goes through
// the implicit operators below instead; these are for the rare spot that
// needs an explicit call.
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(BusinessError error) => new(default, error);
}

// Business failures are values, not exceptions — see STYLE.md. Success payloads
// carry information too: landing in the reserve at position 19 is still success.
public readonly struct Result<T>
{
    private readonly BusinessError? _error;

    internal Result(T? value, BusinessError? error)
    {
        Value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public T Value =>
        IsSuccess ? field! : throw new InvalidOperationException("Result has no value; check IsSuccess first.");

    public BusinessError Error =>
        _error ?? throw new InvalidOperationException("Result has no error; check IsSuccess first.");

    public static implicit operator Result<T>(T value) => Result.Success(value);

    public static implicit operator Result<T>(BusinessError error) => Result.Failure<T>(error);

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<BusinessError, TResult> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);
}
