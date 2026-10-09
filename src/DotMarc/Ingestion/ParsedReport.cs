using DotMarc.Data;

namespace DotMarc.Ingestion;

public sealed record ParsedReport(
    string Domain,
    string ReportingOrg,
    string ReportId,
    DateTimeOffset DateRangeBeginUtc,
    DateTimeOffset DateRangeEndUtc,
    IReadOnlyList<ParsedReportRecord> Records)
{
    /// <summary>Where the report strayed from the DMARC schema without losing anything dotMARC uses, such as elements
    /// in a different order. Empty for a report that matches it.</summary>
    public IReadOnlyList<string> SchemaProblems { get; init; } = [];
}

public sealed record ParsedReportRecord(
    string SourceIp,
    int MessageCount,
    string Disposition,
    string SpfResult,
    string DkimResult,
    string HeaderFrom,
    IReadOnlyList<ParsedAuthDetail> AuthDetails,
    IReadOnlyList<ParsedPolicyOverrideReason> OverrideReasons);

public sealed record ParsedAuthDetail(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result, string? Selector, string? Scope, string? HumanResult);

public sealed record ParsedPolicyOverrideReason(DmarcPolicyOverrideType Type, string? Comment);
