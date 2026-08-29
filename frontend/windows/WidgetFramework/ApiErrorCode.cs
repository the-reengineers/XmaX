namespace XmaX.WidgetFramework;

/// <summary>
/// Categorises failures returned by <see cref="IWidgetApi"/> operations.
/// Widgets can use this to decide how to present errors (e.g. toast vs inline
/// message vs silent revert).
/// </summary>
public enum ApiErrorCode
{
    /// <summary>Operation succeeded (no error).</summary>
    None = 0,

    /// <summary>Backend rejected the command or returned an error.</summary>
    BackendError,

    /// <summary>Input failed client-side validation.</summary>
    InvalidInput,

    /// <summary>User cancelled the operation (e.g. confirmation dialog).</summary>
    Cancelled,

    /// <summary>Backend pipe is not connected.</summary>
    Disconnected,

    /// <summary>Operation did not complete in the expected time.</summary>
    Timeout,

    /// <summary>Backend does not support this operation.</summary>
    NotSupported,
}
