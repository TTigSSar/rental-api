using RentalPlatform.Api.Extensions;
using RentalPlatform.Api.Middleware;
using RentalPlatform.Infrastructure.DependencyInjection;

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
if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseStaticFiles();
}
app.UseCors(RentalPlatform.Api.Extensions.ServiceCollectionExtensions.FrontendCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();
