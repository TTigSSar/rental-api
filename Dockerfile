# Stage 1 — build and publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy solution and all project files first so the restore layer is cached
# independently from source changes.
COPY RentalPlatform.sln .
COPY src/RentalPlatform.Domain/RentalPlatform.Domain.csproj             src/RentalPlatform.Domain/
COPY src/RentalPlatform.Application/RentalPlatform.Application.csproj   src/RentalPlatform.Application/
COPY src/RentalPlatform.Infrastructure/RentalPlatform.Infrastructure.csproj src/RentalPlatform.Infrastructure/
COPY src/RentalPlatform.Api/RentalPlatform.Api.csproj                   src/RentalPlatform.Api/
# Restore only production projects (test projects are excluded from the solution
# glob by copying them below only if needed — keeping the image lean).
RUN dotnet restore src/RentalPlatform.Api/RentalPlatform.Api.csproj

# Copy the full source and publish a self-contained, trimming-safe release build.
COPY src/ src/
RUN dotnet publish src/RentalPlatform.Api/RentalPlatform.Api.csproj \
    --no-restore \
    -c Release \
    -o /app/publish

# Stage 2 — runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create a non-root user so the process has minimal OS privileges.
RUN addgroup --system --gid 1001 appgroup \
 && adduser  --system --uid 1001 --ingroup appgroup --no-create-home appuser

# Copy published output from the build stage.
COPY --from=build /app/publish .

# The app writes uploaded listing images under wwwroot/uploads/listings and
# chat attachments under wwwroot/uploads/chat. Creating both directories here
# ensures they exist and are owned by appuser *before* the corresponding
# volumes are mounted over them — otherwise Docker auto-creates the missing
# mount point as root:root and the appuser process gets Permission denied.
RUN mkdir -p wwwroot/uploads/listings wwwroot/uploads/chat \
 && chown -R appuser:appgroup wwwroot

USER appuser

# ASP.NET Core listens on 8080 by default when ASPNETCORE_HTTP_PORTS is set;
# Kestrel also respects ASPNETCORE_URLS if you prefer explicit binding.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "RentalPlatform.Api.dll"]
