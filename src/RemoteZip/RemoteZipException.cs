namespace RemoteZip;

/// <summary>
/// Thrown when a remote archive is malformed, uses an unsupported feature, or exceeds a
/// configured limit. Transport failures surface as <see cref="HttpRequestException" />
/// instead.
/// </summary>
public class RemoteZipException(string message) : Exception(message);
