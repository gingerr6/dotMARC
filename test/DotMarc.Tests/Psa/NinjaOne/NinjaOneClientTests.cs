using System.Net;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

public sealed class NinjaOneClientTests
{
    private const string TokenJson = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1"}""";
    private const string OpenTicketJson = """
        {"id":1004,"version":1,"clientId":1,"ticketFormId":1,"subject":"SPF broken","type":"PROBLEM","priority":"NONE","severity":"NONE",
         "requesterUid":"user-1","status":{"name":"NEW","displayName":"New","parentId":null,"statusId":1000},"deleted":false}
        """;

    private static NinjaOneSettings Settings() => new()
    {
        Enabled = true, Instance = "eu.ninjarmm.com", ClientId = "client-1", ClientSecretConfigured = true, RefreshTokenConfigured = true,
        TicketFormId = 1, NewStatusId = 1000, TicketType = "PROBLEM", Priority = "LOW", Severity = "MINOR",
    };

    private static (NinjaOneClient Client, FakeHttpMessageHandler Handler, FakeNinjaOneConnectionRecorder Recorder) Create()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue(TokenJson);
        var secrets = new FakeSecretStore { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret", [NinjaOneSettings.RefreshTokenKey] = "refresh-1" } };
        var recorder = new FakeNinjaOneConnectionRecorder();
        return (new NinjaOneClient(new HttpClient(handler), new NinjaOneTokenCache(secrets, TimeProvider.System), recorder), handler, recorder);
    }

    [Fact]
    public async Task Calls_UseTheBearerToken_OnTheInstancesV2Api()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue("""[{"id":2,"name":"Contoso"},{"id":1,"name":"aardvark"}]""");

        var organisations = await client.ListOrganizationsAsync(Settings());

        var request = handler.Requests[1];
        Assert.StartsWith("https://eu.ninjarmm.com/v2/organizations", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "access-1"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal([new PsaCompany("1", "aardvark"), new PsaCompany("2", "Contoso")], organisations);
    }

    [Fact]
    public async Task Organisations_ArePagedFromTheLastId_UntilAShortPage()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue("[" + string.Join(",", Enumerable.Range(1, 1000).Select(number => $"{{\"id\":{number},\"name\":\"Org {number:0000}\"}}")) + "]");
        handler.ResponseBodies.Enqueue("""[{"id":1001,"name":"Aardvark"}]""");

        var organisations = await client.ListOrganizationsAsync(Settings());

        Assert.Equal(1001, organisations.Count);
        Assert.EndsWith("organizations?pageSize=1000&after=1000", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateTicket_SendsTheStatusAsAString_AndAPrivateDescription()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(OpenTicketJson);

        var id = await client.CreateTicketAsync(Settings(), new PsaTicketRequest("7", "contoso.com", "SpfRecordBroken", "SPF broken", "The SPF record is missing."));

        Assert.Equal("1004", id);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/v2/ticketing/ticket", handler.Requests[1].RequestUri!.ToString());
        var body = handler.RequestBodies[1];
        Assert.Contains("\"clientId\":7", body);
        Assert.Contains("\"ticketFormId\":1", body);
        Assert.Contains("\"subject\":\"SPF broken\"", body);
        Assert.Contains("\"status\":\"1000\"", body);
        Assert.Contains("\"public\":false", body);
        Assert.Contains("\"type\":\"PROBLEM\"", body);
        Assert.Contains("\"priority\":\"LOW\"", body);
        Assert.Contains("\"severity\":\"MINOR\"", body);
    }

    [Fact]
    public async Task AddComment_SendsAPrivateCommentAsMultipart()
    {
        var (client, handler, _) = Create();
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NoContent);

        await client.AddCommentAsync(Settings(), "1004", "Alert resolved.");

        Assert.EndsWith("/v2/ticketing/ticket/1004/comment", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("multipart/form-data", handler.Requests[1].Content!.Headers.ContentType!.MediaType);
        Assert.Contains("name=comment", handler.RequestBodies[1]);
        Assert.Contains("{\"body\":\"Alert resolved.\",\"public\":false}", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task ResolveTicket_SendsTheWholeTicketBack_WithResolvedAndItsVersion()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(OpenTicketJson);
        handler.ResponseBodies.Enqueue("{}");

        await client.ResolveTicketAsync(Settings(), "1004");

        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        var body = handler.RequestBodies[2];
        Assert.Contains("\"requesterUid\":\"user-1\"", body);
        Assert.Contains("\"subject\":\"SPF broken\"", body);
        Assert.Contains("\"ticketFormId\":1", body);
        Assert.Contains("\"status\":\"5000\"", body);
        Assert.Contains("\"version\":1", body);
        Assert.DoesNotContain("6000", body);
    }

    [Fact]
    public async Task ResolveTicket_OnAVersionConflict_ReadsTheTicketAgainAndRetriesOnce()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(OpenTicketJson);
        handler.ResponseBodies.Enqueue("""{"resultCode":"ticket_updated_by_another_user","errorMessage":"Ticket 1004 has been updated previously"}""");
        handler.ResponseBodies.Enqueue(OpenTicketJson.Replace("\"version\":1", "\"version\":2"));
        handler.ResponseBodies.Enqueue("{}");
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK })
        {
            handler.StatusCodes.Enqueue(status);
        }

        await client.ResolveTicketAsync(Settings(), "1004");

        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("\"version\":2", handler.RequestBodies[4]);
    }

    [Fact]
    public async Task ResolveTicket_LeavesAResolvedTicketAlone()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(OpenTicketJson.Replace("\"statusId\":1000", "\"statusId\":5000"));

        await client.ResolveTicketAsync(Settings(), "1004");

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveTicket_ForATicketNinjaOneNoLongerHas_DoesNothing()
    {
        var (client, handler, _) = Create();
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NotFound);

        await client.ResolveTicketAsync(Settings(), "1004");

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTicket_ReturnsNullWhenNinjaOneHasNoSuchTicket()
    {
        var (client, handler, _) = Create();
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NotFound);

        Assert.Null(await client.GetTicketAsync(Settings(), "1004"));
    }

    [Fact]
    public async Task A401_SignsInAgainAndRetriesOnce()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue(TokenJson);
        handler.ResponseBodies.Enqueue("[]");
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.OK })
        {
            handler.StatusCodes.Enqueue(status);
        }

        await client.ListTicketFormsAsync(Settings());

        Assert.Equal(4, handler.Requests.Count);
        Assert.EndsWith("/ws/oauth/token", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task ARefusedRefresh_IsRecordedAsALostConnection_AndThrown()
    {
        var handler = new FakeHttpMessageHandler { StatusCode = HttpStatusCode.BadRequest, ResponseBody = """{"error":"invalid_grant"}""" };
        var secrets = new FakeSecretStore { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret", [NinjaOneSettings.RefreshTokenKey] = "refresh-1" } };
        var recorder = new FakeNinjaOneConnectionRecorder();
        var client = new NinjaOneClient(new HttpClient(handler), new NinjaOneTokenCache(secrets, TimeProvider.System), recorder);

        await Assert.ThrowsAsync<NinjaOneConnectionLostException>(() => client.ListTicketFormsAsync(Settings()));

        Assert.Contains("invalid_grant", Assert.Single(recorder.LostReasons));
    }

    [Fact]
    public async Task RefreshConnection_ForcesARefresh_AndRecordsIt()
    {
        var (client, handler, recorder) = Create();
        handler.ResponseBodies.Enqueue(TokenJson);

        await client.RefreshConnectionAsync(Settings());
        await client.RefreshConnectionAsync(Settings());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, recorder.Refreshes);
    }

    [Theory]
    [InlineData(5000, null, true)]
    [InlineData(6000, null, true)]
    [InlineData(5001, 5000, true)]
    [InlineData(2000, null, false)]
    [InlineData(2001, 2000, false)]
    public void ATicketIsDone_WhenItOrItsParentStatusIsResolvedOrClosed(int statusId, int? parentId, bool done)
    {
        var ticket = new NinjaOneTicket(1, 1, 1, 1, "Subject", null, null, null, null, new NinjaOneTicketStatus(statusId, "X", parentId), false);

        Assert.Equal(done, ticket.IsResolvedOrClosed);
    }
}
