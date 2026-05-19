namespace RentalPlatform.Api.Middleware;

// Conservative security response headers. CSP is intentionally not included yet —
// it requires per-route asset allowlists and is deferred to a later sprint.
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // OnStarting fires right before the response body is flushed, so the headers
        // are attached even when later middleware short-circuits (401, 429, 404, ...).
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var headers = ctx.Response.Headers;

            if (!headers.ContainsKey("X-Content-Type-Options"))
            {
                headers["X-Content-Type-Options"] = "nosniff";
            }

            if (!headers.ContainsKey("X-Frame-Options"))
            {
                headers["X-Frame-Options"] = "DENY";
            }

            if (!headers.ContainsKey("Referrer-Policy"))
            {
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            }

            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
