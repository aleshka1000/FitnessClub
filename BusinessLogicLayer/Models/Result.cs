namespace BusinessLogicLayer.Models;

public sealed class Result<T>
{
    private readonly T? _value;
    private readonly BusinessError? _error;

    private Result(T? value, BusinessError? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("The result is a failure.");

    public BusinessError Error => _error ?? throw new InvalidOperationException("The result is a success.");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(BusinessError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }
}
