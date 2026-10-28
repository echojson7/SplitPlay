// SPDX-License-Identifier: MIT
using SplitPlay.Core.Api;

namespace SplitPlay.Service.Api;

/// <summary>An error that maps directly to an HTTP status and a Web API error code.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, message);

    public static ApiException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);

    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, message);

    public static ApiException Validation(string message) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, message);

    public static ApiException NotImplemented(string message) => new(StatusCodes.Status501NotImplemented, ApiErrorCodes.NotImplemented, message);
}
