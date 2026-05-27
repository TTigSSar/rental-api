using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using RentalPlatform.Api.Extensions;
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

app.UseCors(RentalPlatform.Api.Extensions.ServiceCollectionExtensions.FrontendCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
