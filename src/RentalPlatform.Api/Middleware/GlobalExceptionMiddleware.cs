using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace RentalPlatform.Api.Middleware;

// Last-resort fallback for unhandled exceptions. Existing application services use the
// ServiceResult<T> pattern and never throw for business errors, so this middleware
// primarily catches framework-level failures (size limits, deserialization, db, etc.).
// Returns RFC 7807 ProblemDetails JSON and only leaks stack traces in Development.
public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex)
        {
            // Framework throws this for things like multipart body size limits.
            _logger.LogWarning(ex, "Bad HTTP request on {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(
                context,
                ex.StatusCode is >= 400 and < 500 ? ex.StatusCode : StatusCodes.Status400BadRequest,
                "Invalid request.",
                _environment.IsDevelopment() ? ex.Message : null,
                type: "request.invalid");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — nothing to write.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.",
                _environment.IsDevelopment() ? ex.ToString() : null,
                type: "server.unexpected_error");
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string? detail,
        string type)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = type,
            Instance = context.Request.Path,
            Detail = detail
        };

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, JsonOptions);
    }
}
