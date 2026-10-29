using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

public sealed class AiSearchSettings : IAiSearchSettings
{
    private readonly AiSearchOptions _options;

    public AiSearchSettings(IOptions<AiSearchOptions> options) => _options = options.Value;

    public bool IsConfigured => _options.HasApiKey;
    public decimal DailyBudgetUsd => _options.DailyBudgetUsd;
    public double NearMeRadiusKm => _options.NearMeRadiusKm;
}
