namespace DotMarc.Psa.NinjaOne;

public sealed record NinjaOneInstance(string Host, string Label);

/// <summary>The fixed choices NinjaOne's ticketing API takes.</summary>
public static class NinjaOneOptions
{
    /// <summary>What dotMARC asks for when someone connects. offline_access is what makes NinjaOne return a refresh token.</summary>
    public const string Scope = "monitoring management offline_access";

    public const int NewStatusId = 1000;
    public const int ResolvedStatusId = 5000;
    public const int ClosedStatusId = 6000;

    public static IReadOnlyList<NinjaOneInstance> Instances { get; } =
    [
        new("eu.ninjarmm.com", "EU (eu.ninjarmm.com)"),
        new("app.ninjarmm.com", "US (app.ninjarmm.com)"),
        new("us2.ninjarmm.com", "US 2 (us2.ninjarmm.com)"),
        new("ca.ninjarmm.com", "Canada (ca.ninjarmm.com)"),
        new("oc.ninjarmm.com", "Oceania (oc.ninjarmm.com)"),
        new("fed.ninjarmm.com", "US federal (fed.ninjarmm.com)"),
    ];

    public static IReadOnlyList<string> TicketTypes { get; } = ["PROBLEM", "QUESTION", "INCIDENT", "TASK"];
    public static IReadOnlyList<string> Priorities { get; } = ["NONE", "LOW", "MEDIUM", "HIGH", "URGENT"];
    public static IReadOnlyList<string> Severities { get; } = ["NONE", "MINOR", "MODERATE", "MAJOR", "CRITICAL"];
}
