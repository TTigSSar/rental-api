using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using RentalPlatform.Api.Extensions;
using RentalPlatform.Api.Hubs;
using RentalPlatform.Api.Middleware;
using RentalPlatform.Infrastructure.DependencyInjection;
using RentalPlatform.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// Apply pending EF Core migrations BEFORE serving any traffic and before the dev seed.
// Runs in all environments so the schema is always up to date on boot.
await app.Services.ApplyMigrationsAsync();

if (app.Environment.IsDevelopment())
{
    await app.Services.SeedDevelopmentDataAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Development has seed data (including an admin account); every other environment
    // starts from an empty database and would otherwise have no admin at all. No-op unless
    // Bootstrap:AdminEmail / Bootstrap:AdminPassword are both configured.
    await app.Services.BootstrapAdminAsync(app.Configuration);

    // Same idea for the public catalogue: Development gets its listings from the dev seed,
    // every other environment starts with zero categories/listings. No-op unless
    // Bootstrap:DemoContentEnabled is true and the owner email/password are both configured.
    await app.Services.BootstrapDemoContentAsync(app.Configuration);
}

// Must run before anything that reads the client IP or scheme (rate limiter, CORS, HTTPS
// redirect, auth). No-op unless ForwardedHeaders:Enabled is set — see ForwardedHeadersExtensions.
app.UseForwardedHeaders();

// First: catch any downstream throw and translate to RFC 7807 ProblemDetails.
app.UseMiddleware<GlobalExceptionMiddleware>();

// Second: attach security headers to every response (including short-circuited ones).
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseHttpsRedirection();

// Serve ONLY uploaded listing images, and only files with a whitelisted image
// extension. Anything else under the uploads tree (or wwwroot at large) is not
// reachable — a non-image file that lands here returns 404 and is never executed.
//
// Path is derived from FileStorage:ListingsImagesPath config so it stays in sync
// with LocalFileStorageService — both always read from the same source of truth.
var fileStorageOptions = app.Services.GetRequiredService<IOptions<LocalFileStorageOptions>>().Value;
var uploadsRelPath = fileStorageOptions.ListingsImagesPath.Trim().TrimStart('/', '\\');
var uploadsPhysicalRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot", uploadsRelPath);
var uploadsRequestPath = "/" + uploadsRelPath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');
Directory.CreateDirectory(uploadsPhysicalRoot);
{
    var imageContentTypeProvider = new FileExtensionContentTypeProvider();
    imageContentTypeProvider.Mappings.Clear();
    imageContentTypeProvider.Mappings[".jpg"] = "image/jpeg";
    imageContentTypeProvider.Mappings[".jpeg"] = "image/jpeg";
    imageContentTypeProvider.Mappings[".png"] = "image/png";
    imageContentTypeProvider.Mappings[".webp"] = "image/webp";
    imageContentTypeProvider.Mappings[".gif"] = "image/gif";

    app.UseStaticFiles(new StaticFileOptions
    {
        RequestPath = uploadsRequestPath,
        FileProvider = new PhysicalFileProvider(uploadsPhysicalRoot),
        ContentTypeProvider = imageContentTypeProvider,
        ServeUnknownFileTypes = false,
        OnPrepareResponse = static context =>
        {
            var headers = context.Context.Response.Headers;
            // Filenames are content-derived GUIDs and never change — safe to cache hard.
            headers.CacheControl = "public, max-age=31536000, immutable";
            // Always render inline; never offer an uploaded file as a download attachment.
            headers.ContentDisposition = "inline";
        }
    });
}

// Same treatment for chat image attachments, from FileStorage:ChatAttachmentsPath — without
// this, LocalFileStorageService.SaveChatAttachmentAsync would persist files nothing can ever
// serve over HTTP, the same "write path with no readable output" defect this feature exists
// to close (a message would carry an AttachmentUrl that 404s in the client).
var chatUploadsRelPath = fileStorageOptions.ChatAttachmentsPath.Trim().TrimStart('/', '\\');
var chatUploadsPhysicalRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot", chatUploadsRelPath);
var chatUploadsRequestPath = "/" + chatUploadsRelPath.Replace("\\", "/", StringComparison.Ordinal).Trim('/');
Directory.CreateDirectory(chatUploadsPhysicalRoot);
{
    var imageContentTypeProvider = new FileExtensionContentTypeProvider();
    imageContentTypeProvider.Mappings.Clear();
    imageContentTypeProvider.Mappings[".jpg"] = "image/jpeg";
    imageContentTypeProvider.Mappings[".jpeg"] = "image/jpeg";
    imageContentTypeProvider.Mappings[".png"] = "image/png";
    imageContentTypeProvider.Mappings[".webp"] = "image/webp";
    imageContentTypeProvider.Mappings[".gif"] = "image/gif";

    app.UseStaticFiles(new StaticFileOptions
    {
        RequestPath = chatUploadsRequestPath,
        FileProvider = new PhysicalFileProvider(chatUploadsPhysicalRoot),
        ContentTypeProvider = imageContentTypeProvider,
        ServeUnknownFileTypes = false,
        OnPrepareResponse = static context =>
        {
            var headers = context.Context.Response.Headers;
            headers.CacheControl = "public, max-age=31536000, immutable";
            headers.ContentDisposition = "inline";
        }
    });
}

app.UseCors(RentalPlatform.Api.Extensions.ServiceCollectionExtensions.FrontendCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

// Pure liveness probe for the docker healthcheck — anonymous, no DB access. A DB check
// here would flap the healthcheck during SQL Server warmup / restarts, since the app
// would be reported unhealthy for reasons outside its own control.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
