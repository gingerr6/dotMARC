using DotMarc.Data;
using DotMarc.Senders;
using Xunit;

namespace DotMarc.Tests.Senders;

public sealed class ForwardingDetectorTests
{
    private static ForwardingEvidence Mail(int count, string? envelope, params (string Domain, DmarcMechanismResult Result)[] dkim) =>
        new(count, "contoso.com", AuthResult.Fail, envelope is null ? [] : [envelope], dkim, []);

    [Fact]
    public void A_broken_signature_of_the_domains_is_forwarding()
    {
        var reason = ForwardingDetector.Detect(1, [Mail(1, "contoso.com", ("contoso.com", DmarcMechanismResult.Fail))]);

        Assert.StartsWith("Its mail carries the domain's own DKIM signature, broken on the way", reason);
    }

    [Fact]
    public void An_intact_signature_with_the_domains_envelope_is_forwarding()
    {
        var reason = ForwardingDetector.Detect(5, [Mail(5, "contoso.com", ("contoso.com", DmarcMechanismResult.Pass))]);

        Assert.StartsWith("Its mail carries the domain's own DKIM signature and envelope sender", reason);
    }

    [Fact]
    public void An_intact_signature_with_a_services_envelope_is_a_sender_not_forwarding()
    {
        // A service sending for the domain directly: its own bounce address, the domain's DKIM.
        Assert.Null(ForwardingDetector.Detect(5, [Mail(5, "bounces.mailer.example", ("contoso.com", DmarcMechanismResult.Pass))]));
    }

    [Fact]
    public void Mail_with_no_signature_of_the_domains_is_not_forwarding()
    {
        Assert.Null(ForwardingDetector.Detect(3, [Mail(3, "contoso.com", ("attacker.example", DmarcMechanismResult.Pass)), Mail(0, "contoso.com")]));
        Assert.Null(ForwardingDetector.Detect(3, [Mail(3, "contoso.com", ("contoso.com", DmarcMechanismResult.None))]));
    }

    [Fact]
    public void Receivers_saying_so_is_forwarding()
    {
        var reported = new ForwardingEvidence(2, "contoso.com", AuthResult.Fail, [], [], [DmarcPolicyOverrideType.MailingList]);

        Assert.Equal("Receivers reported this mail as forwarded.", ForwardingDetector.Detect(2, [reported]));
    }

    [Fact]
    public void It_takes_most_of_the_sources_mail()
    {
        var forwarded = Mail(4, "contoso.com", ("contoso.com", DmarcMechanismResult.Fail));

        Assert.Null(ForwardingDetector.Detect(10, [forwarded, Mail(6, "contoso.com")]));
        Assert.NotNull(ForwardingDetector.Detect(8, [forwarded, Mail(4, "contoso.com")]));
    }

    [Fact]
    public void A_signature_from_a_subdomain_counts_as_the_domains()
    {
        Assert.NotNull(ForwardingDetector.Detect(1, [Mail(1, null, ("mail.contoso.com", DmarcMechanismResult.Fail))]));
    }

    [Fact]
    public void A_recognised_service_is_never_taken_for_a_detected_forwarder()
    {
        var identity = SenderIdentity.For("40.92.90.30", "mail-vi1eur05.outbound.protection.outlook.com", "Microsoft Corporation", "Receivers reported this mail as forwarded.");

        Assert.False(identity.IsForwarder);
        Assert.Null(identity.ForwardingReason);
        Assert.True(SenderIdentity.For("203.0.113.9", null, null, "Receivers reported this mail as forwarded.").IsForwarder);
    }
}
