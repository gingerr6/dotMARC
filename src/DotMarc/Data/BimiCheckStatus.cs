namespace DotMarc.Data;

/// <summary>The result of checking a domain's BIMI record (default._bimi.&lt;domain&gt;), its logo and its certificate.</summary>
public enum BimiCheckStatus
{
    NotChecked,

    /// <summary>No BIMI record. The detail says whether DMARC is enforced enough to add one.</summary>
    NoRecord,

    /// <summary>A BIMI record, but DMARC isn't at quarantine or reject for all mail, so no mailbox shows the logo.</summary>
    NotEnforced,

    /// <summary>The record, the logo or the certificate is wrong.</summary>
    Misconfigured,

    /// <summary>A valid logo but no certificate: shown by some mailboxes (Yahoo, Fastmail), not Gmail or Apple Mail.</summary>
    LogoOnly,

    /// <summary>A valid logo and a valid mark certificate (VMC or CMC): shown by Gmail and Apple Mail too.</summary>
    Ok,
}
