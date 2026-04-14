namespace RentalPlatform.Application.Common;

public sealed class ServiceResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public ServiceError? Error { get; }

    private ServiceResult(bool isSuccess, T? value, ServiceError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static ServiceResult<T> Success(T value) => new(true, value, null);

    public static ServiceResult<T> Failure(ServiceError error) => new(false, default, error);
}

public sealed class ServiceError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
