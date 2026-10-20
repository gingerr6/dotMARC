using System.Net;
using DotMarc.Graph;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Graph;

public sealed class EntraGuestInviterTests
{
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    }

    private static (EntraGuestInviter Inviter, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new EntraGuestInviter(_ => Task.FromResult("token"), new SingleClientFactory(handler), NullLogger<EntraGuestInviter>.Instance), handler);
    }

    [Fact]
    public async Task InviteAsync_PostsAnInvitationThatSendsMicrosoftsEmail()
    {
        var (inviter, handler) = Create();
        handler.StatusCode = HttpStatusCode.Created;

        var result = await inviter.InviteAsync("client@aurora.example", "https://dotmarc.example/", CancellationToken.None);

        Assert.Equal(GuestInviteOutcome.Invited, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/invitations", request.RequestUri!.ToString());
        Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
        Assert.Contains("\"invitedUserEmailAddress\":\"client@aurora.example\"", handler.RequestBodies[0]);
        Assert.Contains("\"inviteRedirectUrl\":\"https://dotmarc.example/\"", handler.RequestBodies[0]);
        Assert.Contains("\"sendInvitationMessage\":true", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task InviteAsync_PassesOnEntrasReason_WhenItRefuses()
    {
        var (inviter, handler) = Create();
        handler.StatusCode = HttpStatusCode.BadRequest;
        handler.ResponseBody = """{"error":{"code":"BadRequest","message":"The invited user already exists in the directory as a member."}}""";

        var result = await inviter.InviteAsync("colleague@contoso.example", "https://dotmarc.example/", CancellationToken.None);

        Assert.Equal(GuestInviteOutcome.Refused, result.Outcome);
        Assert.Equal("The invited user already exists in the directory as a member.", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task InviteAsync_SaysItsNotPermitted_WithoutUserInviteAll(HttpStatusCode statusCode)
    {
        var (inviter, handler) = Create();
        handler.StatusCode = statusCode;
        handler.ResponseBody = """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""";

        var result = await inviter.InviteAsync("client@aurora.example", "https://dotmarc.example/", CancellationToken.None);

        Assert.Equal(GuestInviteOutcome.NotPermitted, result.Outcome);
    }

    [Fact]
    public async Task InviteAsync_Fails_OnAnythingElse()
    {
        var (inviter, handler) = Create();
        handler.StatusCode = HttpStatusCode.ServiceUnavailable;
        handler.ResponseBody = "not json";

        var result = await inviter.InviteAsync("client@aurora.example", "https://dotmarc.example/", CancellationToken.None);

        Assert.Equal(GuestInviteOutcome.Failed, result.Outcome);
        Assert.Null(result.Detail);
    }
}
