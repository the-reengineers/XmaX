namespace XmaX.WidgetFramework;

/// <summary>
/// Outcome of an <see cref="IWidgetApi"/> operation that returns no value.
/// Use <see cref="Ok"/> for success and <see cref="Fail"/> for a categorized error.
/// </summary>
public readonly record struct ApiResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Human-readable error message. Null when <see cref="IsSuccess"/> is true.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Error category. <see cref="ApiErrorCode.None"/> on success.</summary>
    public ApiErrorCode ErrorCode { get; init; }

    /// <summary>Construct a successful result.</summary>
    public static ApiResult Ok() => new() { IsSuccess = true, ErrorCode = ApiErrorCode.None };

    /// <summary>Construct a failed result with a message and error code.</summary>
    public static ApiResult Fail(string message, ApiErrorCode code = ApiErrorCode.BackendError)
        => new() { IsSuccess = false, ErrorMessage = message, ErrorCode = code };

    /// <summary>Construct a failed result from an exception.</summary>
    public static ApiResult FromException(Exception ex, ApiErrorCode code = ApiErrorCode.BackendError)
        => new() { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = code };
}

/// <summary>
/// Outcome of an <see cref="IWidgetApi"/> query that returns a value on success.
/// </summary>
/// <typeparam name="T">The type of the returned value.</typeparam>
public readonly record struct ApiResult<T>
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>The returned value. Default when <see cref="IsSuccess"/> is false.</summary>
    public T? Value { get; init; }

    /// <summary>Human-readable error message. Null when <see cref="IsSuccess"/> is true.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Error category. <see cref="ApiErrorCode.None"/> on success.</summary>
    public ApiErrorCode ErrorCode { get; init; }

    /// <summary>Construct a successful result carrying a value.</summary>
    public static ApiResult<T> Ok(T value) => new()
    {
        IsSuccess = true,
        Value = value,
        ErrorCode = ApiErrorCode.None,
    };

    /// <summary>Construct a failed result with a message and error code.</summary>
    public static ApiResult<T> Fail(string message, ApiErrorCode code = ApiErrorCode.BackendError)
        => new() { IsSuccess = false, ErrorMessage = message, ErrorCode = code };

    /// <summary>Construct a failed result from an exception.</summary>
    public static ApiResult<T> FromException(Exception ex, ApiErrorCode code = ApiErrorCode.BackendError)
        => new() { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = code };
}
