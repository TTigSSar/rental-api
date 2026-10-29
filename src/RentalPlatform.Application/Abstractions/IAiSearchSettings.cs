namespace RentalPlatform.Application.Abstractions;

// What the AI search service needs from configuration (AiSearch options live in Infrastructure).
public interface IAiSearchSettings
{
    bool IsConfigured { get; }
    decimal DailyBudgetUsd { get; }
    double NearMeRadiusKm { get; }
    int PerUserPerDay { get; }
}
