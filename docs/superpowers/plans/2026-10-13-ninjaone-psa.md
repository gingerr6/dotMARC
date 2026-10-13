# NinjaOne PSA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add NinjaOne as a fourth PSA. It raises and resolves tickets for alerts, signs in with the authorization code flow, and keeps its one-hour refresh token alive. PSAs are listed in the order HaloPSA, NinjaOne, ConnectWise, Autotask.

**Architecture:**
- **Shape.** It follows ConnectWise's shape: a settings row and a static settings service, a typed `HttpClient` client, and an `IPsaProvider`.
- **Sign-in.** A connector class handles the connect and callback endpoints. A token cache holds the access token and uses the refresh token from the secret store.
- **Keep-alive.** A `BackgroundService` refreshes every 20 minutes. It is also the only place that raises or resolves the "PSA connection lost" alert. The client only writes the lost state to the settings row, through `INinjaOneConnectionRecorder`. That avoids a dependency loop: AlertingService → PsaTicketService → providers → client.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9, EF Core 10 / Npgsql, xUnit with Testcontainers Postgres, Docusaurus.

**Spec:** `docs/superpowers/specs/2026-10-13-ninjaone-psa-design.md`

## Global Constraints

- **PSA order:** `PsaKind` is `{ HaloPsa, NinjaOne, ConnectWise, Autotask }`. Every PSA list, tab and column follows that order.
- **Scopes:** dotMARC requests exactly `monitoring management offline_access`, and uses only the `authorization_code` and `refresh_token` grants.
- **Secret store keys:** the client secret goes under `NinjaOne.ClientSecret` and the refresh token under `NinjaOne.RefreshToken`. Neither is ever logged or shown.
- **Resolving:** dotMARC resolves tickets with status `"5000"` and never sends `"6000"`. NinjaOne answers `cannot_close_tickets_directly`.
- **Keep-alive:** it runs at startup and then every 20 minutes.
- **The new alert type:** `PsaConnectionLost`, displayed as "PSA connection lost", with `CreatesTicketByDefault: false`. Its subject is "NinjaOne connection".
- **Copy:** no em dashes in any user-facing text, docs or comments. Variable names must mean something; no `$r`/`x`-style names.
- **Audited services:** every public mutating method on `NinjaOneSettingsService` takes `(DotMarcDbContext context, AuditActor actor, ...)`, because `AuditCoverageTests` enforces it.

## Review Focus

These are the most likely ways for this to go wrong that the spec implies but no other task's tests cover:

1. **Saving with no changes while connected must not reset the connection.** If the instance, client ID and secret are all unchanged, the refresh token is kept. Pinned in Task 2.
2. **A ticket deleted in NinjaOne when its alert resolves.** If resolving gets a 404, it does nothing quietly, so the poller and alert resolve aren't stuck retrying forever. Pinned in Task 4.
3. **A lost connection found by a ticket call, not the keep-alive, still raises the alert.** It appears on the next keep-alive run. Pinned in Task 6.
4. **Turning NinjaOne off while its connection is lost resolves the open alert.** The keep-alive doesn't refresh a disabled PSA. Pinned in Task 6.
5. **A callback replayed with another user's state, or after 10 minutes, is refused.** Nothing is saved. Pinned in Task 7.

---

## File map

**Create**
- `src/DotMarc/Psa/NinjaOne/NinjaOneSettings.cs`: the settings row, secret store keys, `IsConnected`
- `src/DotMarc/Psa/NinjaOne/NinjaOneOptions.cs`: instances, types, priorities, severities, status ids, scope
- `src/DotMarc/Psa/NinjaOne/NinjaOneSettingsService.cs`: get, save, connect, disconnect (audited)
- `src/DotMarc/Psa/NinjaOne/NinjaOneOAuth.cs`: the authorize URL, the token request, the token response, the lost-connection exception
- `src/DotMarc/Psa/NinjaOne/NinjaOneTokenCache.cs`: the in-memory access token, refresh, and saving a rotated refresh token
- `src/DotMarc/Psa/NinjaOne/NinjaOneConnectionRecorder.cs`: writes "lost" and "refreshed" to the settings row
- `src/DotMarc/Psa/NinjaOne/INinjaOneClient.cs`, `NinjaOneClient.cs`: the API calls
- `src/DotMarc/Psa/NinjaOne/NinjaOneProvider.cs`: `IPsaProvider`
- `src/DotMarc/Psa/NinjaOne/NinjaOneKeepAliveService.cs`: the background refresh and the alert sync
- `src/DotMarc/Psa/NinjaOne/NinjaOneStateProtector.cs`: the signed OAuth state
- `src/DotMarc/Psa/NinjaOne/NinjaOneConnector.cs`: builds the authorize URL and completes the callback
- `src/DotMarc/Components/Psa/NinjaOneSettingsTab.razor`
- Migration `AddNinjaOneSettings`
- Tests under `test/DotMarc.Tests/Psa/NinjaOne/`, plus `test/DotMarc.Tests/Psa/PsaKindTests.cs` and `test/DotMarc.Tests/Internal/FakeNinjaOneConnectionRecorder.cs`
- `website/docs/psa/ninjaone.mdx`

**Modify**
- `PsaKind.cs`, `ImportTable.cs`, `ImportPlan.cs`, `PsaImportColumns.cs`
- `DotMarcDbContext.cs`, `AuditActions.cs`, `AlertTypes.cs`, `AlertingService.cs` and its three test fakes
- `PsaOptionNameCache.cs`, `PsaIntegrationTestPanel.razor`, `PsaSettings.razor`, `Program.cs`
- `AuditCoverageTests.cs`, `AlertTypesTests.cs`, `ImportTableTests.cs`, `PsaDirectoryTests.cs`
- Docs: `psa-integration.mdx`, `psa/halopsa.mdx`, `psa/connectwise.mdx`, `psa/autotask.mdx`, `alerts.mdx`, `import-domains.mdx`
- The website's `openapi.json`, regenerated

**Test command:** `dotnet test test/DotMarc.Tests --filter "<filter>"`. Docker must be running for the `[Collection("Postgres")]` tests.

---

### Task 1: NinjaOne in the PSA order, names and import column

**Files:**
- Modify: `src/DotMarc/Psa/PsaKind.cs`
- Modify: `src/DotMarc/DomainImport/ImportTable.cs:5` and its header names (around line 52)
- Modify: `src/DotMarc/DomainImport/ImportPlan.cs:11`
- Modify: `src/DotMarc/DomainImport/PsaImportColumns.cs`
- Modify: `website/docs/import-domains.mdx` (the column table, around line 29)
- Test: create `test/DotMarc.Tests/Psa/PsaKindTests.cs`; modify `test/DotMarc.Tests/DomainImport/ImportTableTests.cs` and `test/DotMarc.Tests/Psa/PsaDirectoryTests.cs`

**Interfaces:**
- Produces: `PsaKind.NinjaOne`, which displays as "NinjaOne" with the company label "NinjaOne organisation"; `ImportColumn.NinjaOneOrganisation`; `ImportNameKind.NinjaOneOrganisation`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Psa/PsaKindTests.cs`:

```csharp
using DotMarc.DomainImport;
using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Psa;

public sealed class PsaKindTests
{
    [Fact]
    public void PsasAreListedHaloNinjaConnectWiseAutotask()
    {
        Assert.Equal([PsaKind.HaloPsa, PsaKind.NinjaOne, PsaKind.ConnectWise, PsaKind.Autotask], Enum.GetValues<PsaKind>());
    }

    [Fact]
    public void NinjaOne_HasItsNameAndCompanyLabel()
    {
        Assert.Equal("NinjaOne", PsaKind.NinjaOne.DisplayName());
        Assert.Equal("NinjaOne organisation", PsaKind.NinjaOne.CompanyLabel());
    }

    [Fact]
    public void ImportColumns_FollowThePsaOrder()
    {
        Assert.Equal(Enum.GetValues<PsaKind>(), PsaImportColumns.All.Select(column => column.Psa));
    }
}
```

Add to `ImportTableTests.cs`, after `TheAutotaskCompanyColumn_IsReadByEitherHeaderName`:

```csharp
    [Fact]
    public void WithoutAHeader_TheNinjaOneColumnComesAfterAutotask()
    {
        var row = Table("contoso.com,Client A,primary,Contoso,no,s1,testing,mail.contoso.com,86400,Contoso Ltd,Contoso Limited,Contoso Org").Rows.Single();

        Assert.Equal("Contoso Limited", row.PsaCompanies[PsaKind.Autotask]);
        Assert.Equal("Contoso Org", row.PsaCompanies[PsaKind.NinjaOne]);
    }

    [Theory]
    [InlineData("ninjaone organisation")]
    [InlineData("NinjaOne organization")]
    [InlineData("NinjaOne")]
    [InlineData("ninja")]
    public void TheNinjaOneOrganisationColumn_IsReadByAnyOfItsHeaderNames(string header)
    {
        var row = Table($"domain,{header}\ncontoso.io,Contoso Org").Rows.Single();

        Assert.Equal("Contoso Org", row.PsaCompanies[PsaKind.NinjaOne]);
    }
```

In `PsaDirectoryTests.LoadsEachReadyPsa_ReportsAFailureByName_AndSkipsOnesNotReady`, add a ready NinjaOne provider and check that it sorts second:

```csharp
        var ninjaOne = new FakePsaProvider(PsaKind.NinjaOne) { Companies = { new PsaCompany("3", "Fabrikam") } };

        var lists = await new PsaDirectory([autotask, connectWise, ninjaOne, halo], NullLogger<PsaDirectory>.Instance).LoadAsync(context);

        Assert.Equal([PsaKind.HaloPsa, PsaKind.NinjaOne, PsaKind.ConnectWise], lists.Select(list => list.Psa));
        Assert.Equal("Contoso", Assert.Single(lists[0].Companies!).Name);
        Assert.Null(lists[2].Companies);
        Assert.Equal("ConnectWise companies couldn't be loaded: refused", lists[2].FailureReason);
```

These replace the existing `lists` line and the four asserts after it.

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaKindTests|FullyQualifiedName~ImportTableTests|FullyQualifiedName~PsaDirectoryTests"`
Expected: a build failure, because `PsaKind.NinjaOne` does not exist.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/PsaKind.cs`:

```csharp
namespace DotMarc.Psa;

/// <summary>A PSA dotMARC can raise tickets in. Stored by name, so the order here can change freely. The order is the
/// order PSAs are listed in everywhere.</summary>
public enum PsaKind { HaloPsa, NinjaOne, ConnectWise, Autotask }

public static class PsaKindNames
{
    public static string DisplayName(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "HaloPSA",
        PsaKind.NinjaOne => "NinjaOne",
        PsaKind.ConnectWise => "ConnectWise",
        PsaKind.Autotask => "Autotask",
        _ => psa.ToString()
    };

    /// <summary>What the PSA calls the customer a ticket is raised against, as a column or field label.</summary>
    public static string CompanyLabel(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "Halo client",
        PsaKind.NinjaOne => "NinjaOne organisation",
        PsaKind.ConnectWise => "ConnectWise company",
        PsaKind.Autotask => "Autotask company",
        _ => $"{psa} company"
    };
}
```

In `ImportTable.cs`, add `NinjaOneOrganisation` to the **end** of `ImportColumn`, because headerless input is read in enum order:

```csharp
public enum ImportColumn { Domain, Groups, Tags, HaloClient, Monitored, DkimSelectors, MtaStsMode, MtaStsMxHosts, MtaStsMaxAge, ConnectWiseCompany, AutotaskCompany, NinjaOneOrganisation }
```

Add its header names after the Autotask line:

```csharp
        ["ninjaoneorganisation"] = ImportColumn.NinjaOneOrganisation, ["ninjaoneorganization"] = ImportColumn.NinjaOneOrganisation,
        ["ninjaone"] = ImportColumn.NinjaOneOrganisation, ["ninja"] = ImportColumn.NinjaOneOrganisation,
```

In `ImportPlan.cs`:

```csharp
public enum ImportNameKind { Group, Tag, HaloClient, ConnectWiseCompany, AutotaskCompany, NinjaOneOrganisation }
```

In `PsaImportColumns.All`, insert NinjaOne second:

```csharp
        new(PsaKind.HaloPsa, ImportColumn.HaloClient, ImportNameKind.HaloClient),
        new(PsaKind.NinjaOne, ImportColumn.NinjaOneOrganisation, ImportNameKind.NinjaOneOrganisation),
        new(PsaKind.ConnectWise, ImportColumn.ConnectWiseCompany, ImportNameKind.ConnectWiseCompany),
        new(PsaKind.Autotask, ImportColumn.AutotaskCompany, ImportNameKind.AutotaskCompany),
```

In `website/docs/import-domains.mdx`, add this row after the `autotask company` row:

```markdown
| ninjaone organisation | The NinjaOne organisation's name. Used only when NinjaOne is connected. |
```

If that page lists the headerless column order, add `ninjaone organisation` at the end of the list.

- [ ] **Step 4: Run the tests to check they pass**

Run the same command as Step 2.
Expected: all pass.

- [ ] **Step 5: Run the whole suite**

Run `dotnet test test/DotMarc.Tests > .superpowers/sdd/ninjaone/suite-task1.txt 2>&1; tail -5 .superpowers/sdd/ninjaone/suite-task1.txt`. Fix any switch over `PsaKind` that now throws for `NinjaOne`. The known one is `PsaIntegrationTestPanel.AuditAction`; it's only hit when the panel renders for NinjaOne, which happens in Task 8. Leave it until then.
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Psa/PsaKind.cs src/DotMarc/DomainImport website/docs/import-domains.mdx test/DotMarc.Tests/Psa/PsaKindTests.cs test/DotMarc.Tests/DomainImport/ImportTableTests.cs test/DotMarc.Tests/Psa/PsaDirectoryTests.cs
git commit -m "List PSAs as HaloPSA, NinjaOne, ConnectWise, Autotask and import NinjaOne organisations"
```

---

### Task 2: NinjaOne settings, the migration and the audited settings service

**Files:**
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneSettings.cs`, `NinjaOneOptions.cs`, `NinjaOneSettingsService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs` (the DbSet near line 45, and the seed near line 416)
- Modify: `src/DotMarc/Audit/AuditActions.cs` (the constants near line 42 and the labels near line 105)
- Create: the migration, via `dotnet ef`
- Test: create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneSettingsServiceTests.cs`; modify `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`

**Interfaces:**
- Produces:
  - `NinjaOneSettings`, with the secret store keys `ClientSecretKey`, `RefreshTokenKey` and `ClearedRefreshToken`, and `bool IsConnected`.
  - `NinjaOneOptions`: `Instances`, `TicketTypes`, `Priorities`, `Severities`, `NewStatusId = 1000`, `ResolvedStatusId = 5000`, `ClosedStatusId = 6000`, `Scope`.
  - `NinjaOneSettingsService`:
    - `GetAsync(context, ct)`
    - `SaveAsync(context, actor, secretStore, updated, newClientSecret, ct)`
    - `ConnectAsync(context, actor, secretStore, refreshToken, nowUtc, ct)`
    - `DisconnectAsync(context, actor, secretStore, ct)`
  - `AuditActions`: `NinjaOneSettingsSaved`, `NinjaOneConnected`, `NinjaOneDisconnected`, `NinjaOneConnectionLost`, `NinjaOneIntegrationTested`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneSettingsServiceTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneSettingsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private static NinjaOneSettings Complete(Action<NinjaOneSettings>? change = null)
    {
        var settings = new NinjaOneSettings
        {
            Enabled = true, Instance = "eu.ninjarmm.com", ClientId = "client-1", TicketFormId = 1, TicketFormName = "Default",
            NewStatusId = 1000, NewStatusName = "New", TicketType = "PROBLEM", Priority = "LOW", Severity = "MINOR",
        };
        change?.Invoke(settings);
        return settings;
    }

    private async Task<FakeSecretStore> SaveConnectedAsync()
    {
        var secrets = new FakeSecretStore();
        await using var context = CreateContext();
        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), "secret");
        await NinjaOneSettingsService.ConnectAsync(context, TestActors.Admin, secrets, "refresh-1", Now);
        return secrets;
    }

    [Fact]
    public async Task TheSeededRow_StartsAtNew_WithProblemNoneNone()
    {
        await using var context = CreateContext();
        var settings = await NinjaOneSettingsService.GetAsync(context);

        Assert.Equal((1000, "New", "PROBLEM", "NONE", "NONE", false),
            (settings.NewStatusId, settings.NewStatusName, settings.TicketType, settings.Priority, settings.Severity, settings.IsConnected));
    }

    [Fact]
    public async Task Save_StoresTheClientSecretInTheSecretStore_AndAuditsItAsASecret()
    {
        var secrets = new FakeSecretStore();
        await using var context = CreateContext();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), " secret ");

        Assert.Equal("secret", secrets.Secrets[NinjaOneSettings.ClientSecretKey]);
        await using var verify = CreateContext();
        Assert.True((await NinjaOneSettingsService.GetAsync(verify)).ClientSecretConfigured);
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneSettingsSaved);
        Assert.Contains(entry.Changes!, change => change.Field == "Client secret");
        Assert.DoesNotContain("secret", string.Join(" ", entry.Changes!.Select(change => change.NewValue)));
    }

    [Fact]
    public async Task Connect_StoresTheRefreshToken_AndRecordsWhoConnected()
    {
        var secrets = await SaveConnectedAsync();

        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.True(saved.IsConnected);
        Assert.Equal((TestActors.Admin.Name, Now), (saved.ConnectedBy, saved.ConnectedUtc));
        Assert.True(await verify.AuditEntries.AnyAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneConnected));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("client")]
    [InlineData("secret")]
    public async Task ChangingWhatSignsIn_ResetsTheConnection(string change)
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        var updated = Complete(settings =>
        {
            if (change == "instance") settings.Instance = "app.ninjarmm.com";
            if (change == "client") settings.ClientId = "client-2";
        });
        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, updated, change == "secret" ? "new-secret" : null);

        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.False(saved.RefreshTokenConfigured);
        Assert.Null(saved.ConnectedBy);
        Assert.Equal(NinjaOneSettings.ClearedRefreshToken, secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        Assert.Contains("reset the connection", (await verify.AuditEntries.OrderBy(auditEntry => auditEntry.Id).LastAsync()).Summary);
    }

    [Fact]
    public async Task SavingTicketDefaultsOnly_KeepsTheConnection()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(settings => settings.Priority = "HIGH"), null);

        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.True(saved.IsConnected);
        Assert.Equal("HIGH", saved.Priority);
        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
    }

    [Fact]
    public async Task SavingWithNoChanges_WritesNothing()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();
        var before = await context.AuditEntries.CountAsync();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), null);

        Assert.Equal(before, await context.AuditEntries.CountAsync());
        Assert.True((await NinjaOneSettingsService.GetAsync(context)).IsConnected);
    }

    [Fact]
    public async Task Disconnect_ClearsTheTokenAndTheConnection()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        await NinjaOneSettingsService.DisconnectAsync(context, TestActors.Admin, secrets);

        await using var verify = CreateContext();
        Assert.False((await NinjaOneSettingsService.GetAsync(verify)).RefreshTokenConfigured);
        Assert.Equal(NinjaOneSettings.ClearedRefreshToken, secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        Assert.True(await verify.AuditEntries.AnyAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneDisconnected));
    }

    [Theory]
    [InlineData("instance", "example.com")]
    [InlineData("type", "BUG")]
    [InlineData("priority", "CRITICAL")]
    [InlineData("severity", "URGENT")]
    public async Task Save_RefusesValuesNinjaOneDoesNotHave(string field, string value)
    {
        await using var context = CreateContext();
        var updated = Complete(settings =>
        {
            if (field == "instance") settings.Instance = value;
            if (field == "type") settings.TicketType = value;
            if (field == "priority") settings.Priority = value;
            if (field == "severity") settings.Severity = value;
        });

        await Assert.ThrowsAsync<ArgumentException>(() => NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));
    }
}
```

`AuditEntry.Changes` items have `Field` and `NewValue`. Check the exact property names in `src/DotMarc/Audit/AuditEntry.cs` and `AuditChanges.cs`, and use what's there. If `TestActors.Admin.Name` isn't the actor's display name, use the property `AuditActor` keeps the name in.

In `AuditCoverageTests.AuditedServices`, add `typeof(DotMarc.Psa.NinjaOne.NinjaOneSettingsService)` after the Autotask service.

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneSettingsServiceTests|FullyQualifiedName~AuditCoverageTests"`
Expected: a build failure, because the `DotMarc.Psa.NinjaOne` namespace is missing.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/NinjaOne/NinjaOneSettings.cs`:

```csharp
namespace DotMarc.Psa.NinjaOne;

/// <summary>Singleton settings row for NinjaOne ticketing, seeded by its migration like the other PSAs. NinjaOne only
/// lets a signed-in user raise tickets, so dotMARC connects with the authorization code flow and keeps the refresh token
/// in ISecretStore under <see cref="RefreshTokenKey"/>. The client secret is there too, under
/// <see cref="ClientSecretKey"/>. Neither is ever on this row.</summary>
public sealed class NinjaOneSettings
{
    public const string ClientSecretKey = "NinjaOne.ClientSecret";
    public const string RefreshTokenKey = "NinjaOne.RefreshToken";

    /// <summary>What a disconnected install keeps under <see cref="RefreshTokenKey"/>. The secret store has no delete,
    /// and Key Vault may refuse an empty value, so the old token is overwritten with this.</summary>
    public const string ClearedRefreshToken = "-";

    public int Id { get; set; }
    public bool Enabled { get; set; }

    /// <summary>The instance's host, such as eu.ninjarmm.com. One of <see cref="NinjaOneOptions.Instances"/>.</summary>
    public string? Instance { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
    public bool RefreshTokenConfigured { get; set; }

    /// <summary>The dotMARC user who connected, and when. NinjaOne raises tickets as the NinjaOne user they signed in as.</summary>
    public string? ConnectedBy { get; set; }
    public DateTimeOffset? ConnectedUtc { get; set; }

    /// <summary>When the keep-alive last refreshed the sign-in.</summary>
    public DateTimeOffset? LastRefreshUtc { get; set; }

    /// <summary>Set when NinjaOne refuses the saved sign-in. NinjaOne isn't ready until someone reconnects.</summary>
    public DateTimeOffset? ConnectionLostUtc { get; set; }
    public string? ConnectionLostReason { get; set; }

    // Ticket defaults. The names sit beside the ids for display only, like ConnectWise.
    public int? TicketFormId { get; set; }
    public string? TicketFormName { get; set; }
    public int? NewStatusId { get; set; }
    public string? NewStatusName { get; set; }
    public string TicketType { get; set; } = "PROBLEM";
    public string Priority { get; set; } = "NONE";
    public string Severity { get; set; } = "NONE";

    public bool IsConnected => RefreshTokenConfigured && ConnectionLostUtc is null;
}
```

`src/DotMarc/Psa/NinjaOne/NinjaOneOptions.cs`:

```csharp
namespace DotMarc.Psa.NinjaOne;

public sealed record NinjaOneInstance(string Host, string Label);

/// <summary>The fixed choices NinjaOne's ticketing API takes.</summary>
public static class NinjaOneOptions
{
    /// <summary>What dotMARC asks for when someone connects. offline_access is what makes NinjaOne return a refresh
    /// token.</summary>
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
```

`src/DotMarc/Psa/NinjaOne/NinjaOneSettingsService.cs`:

```csharp
using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Reads and saves the single NinjaOne settings row, and connects and disconnects it. Changing what signs in
/// (instance, client ID or secret) resets the connection, because the saved refresh token belongs to the old
/// app.</summary>
public static class NinjaOneSettingsService
{
    private static readonly AuditTarget Target = AuditTarget.Settings("NinjaOne");

    public static Task<NinjaOneSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.NinjaOneSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, NinjaOneSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        updated.Instance = Trimmed(updated.Instance)?.ToLowerInvariant();
        updated.ClientId = Trimmed(updated.ClientId);
        if (updated.Instance is not null && NinjaOneOptions.Instances.All(instance => instance.Host != updated.Instance))
        {
            throw new ArgumentException($"{updated.Instance} isn't a NinjaOne instance. Choose one from the list.", nameof(updated));
        }

        RequireOneOf(updated.TicketType, NinjaOneOptions.TicketTypes, "ticket type");
        RequireOneOf(updated.Priority, NinjaOneOptions.Priorities, "priority");
        RequireOneOf(updated.Severity, NinjaOneOptions.Severities, "severity");

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NinjaOneSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Instance", saved.Instance, updated.Instance)
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Field("Ticket form", Describe(saved.TicketFormId, saved.TicketFormName), Describe(updated.TicketFormId, updated.TicketFormName))
            .Field("New ticket status", Describe(saved.NewStatusId, saved.NewStatusName), Describe(updated.NewStatusId, updated.NewStatusName))
            .Field("Type", saved.TicketType, updated.TicketType)
            .Field("Priority", saved.Priority, updated.Priority)
            .Field("Severity", saved.Severity, updated.Severity)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var signInChanged = saved.Instance != updated.Instance || saved.ClientId != updated.ClientId || hasNewClientSecret;
        var resetsConnection = signInChanged && saved.RefreshTokenConfigured;

        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.Instance = updated.Instance;
        existing.ClientId = updated.ClientId;
        (existing.TicketFormId, existing.TicketFormName) = (updated.TicketFormId, updated.TicketFormName);
        (existing.NewStatusId, existing.NewStatusName) = (updated.NewStatusId, updated.NewStatusName);
        existing.TicketType = updated.TicketType;
        existing.Priority = updated.Priority;
        existing.Severity = updated.Severity;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(NinjaOneSettings.ClientSecretKey, newClientSecret!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        if (resetsConnection)
        {
            await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, NinjaOneSettings.ClearedRefreshToken, cancellationToken).ConfigureAwait(false);
            ClearConnection(existing);
        }

        var summary = resetsConnection ? "Saved NinjaOne settings and reset the connection" : "Saved NinjaOne settings";
        AuditLog.Record(context, actor, AuditActions.NinjaOneSettingsSaved, Target, summary, changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the refresh token from a completed sign-in and marks NinjaOne connected.</summary>
    public static async Task ConnectAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, string refreshToken, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, refreshToken, cancellationToken).ConfigureAwait(false);
        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.RefreshTokenConfigured = true;
        existing.ConnectedBy = actor.Name;
        existing.ConnectedUtc = nowUtc;
        existing.LastRefreshUtc = nowUtc;
        existing.ConnectionLostUtc = null;
        existing.ConnectionLostReason = null;
        AuditLog.Record(context, actor, AuditActions.NinjaOneConnected, Target, "Connected NinjaOne");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task DisconnectAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, CancellationToken cancellationToken = default)
    {
        await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, NinjaOneSettings.ClearedRefreshToken, cancellationToken).ConfigureAwait(false);
        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        ClearConnection(existing);
        AuditLog.Record(context, actor, AuditActions.NinjaOneDisconnected, Target, "Disconnected NinjaOne");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ClearConnection(NinjaOneSettings settings)
    {
        settings.RefreshTokenConfigured = false;
        settings.ConnectedBy = null;
        settings.ConnectedUtc = null;
        settings.LastRefreshUtc = null;
        settings.ConnectionLostUtc = null;
        settings.ConnectionLostReason = null;
    }

    private static void RequireOneOf(string value, IReadOnlyList<string> allowed, string label)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{value} isn't a NinjaOne {label}. Choose one of {string.Join(", ", allowed)}.");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A chosen option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(CultureInfo.InvariantCulture);
}
```

`DotMarcDbContext.cs`. Add the DbSet after `AutotaskSettings`:

```csharp
    public DbSet<DotMarc.Psa.NinjaOne.NinjaOneSettings> NinjaOneSettings => Set<DotMarc.Psa.NinjaOne.NinjaOneSettings>();
```

Add the seed after the Autotask seed:

```csharp
        modelBuilder.Entity<DotMarc.Psa.NinjaOne.NinjaOneSettings>().HasData(new DotMarc.Psa.NinjaOne.NinjaOneSettings { Id = 1, NewStatusId = DotMarc.Psa.NinjaOne.NinjaOneOptions.NewStatusId, NewStatusName = "New" });
```

`AuditActions.cs`. Add the constants after `AutotaskSettingsSaved`:

```csharp
    public const string NinjaOneSettingsSaved = "settings.ninjaone.saved";
    public const string NinjaOneConnected = "ninjaone.connected";
    public const string NinjaOneDisconnected = "ninjaone.disconnected";
    public const string NinjaOneConnectionLost = "ninjaone.connection_lost";
    public const string NinjaOneIntegrationTested = "ninjaone.integration_tested";
```

Add their labels to the label list, after `(AutotaskSettingsSaved, ...)`:

```csharp
        (NinjaOneSettingsSaved, "NinjaOne settings saved"),
        (NinjaOneConnected, "NinjaOne connected"),
        (NinjaOneDisconnected, "NinjaOne disconnected"),
        (NinjaOneConnectionLost, "NinjaOne connection lost"),
        (NinjaOneIntegrationTested, "NinjaOne integration tested"),
```

Create the migration:

```bash
dotnet tool run dotnet-ef migrations add AddNinjaOneSettings --project src/DotMarc
```

Open the generated migration. Check that it creates `NinjaOneSettings` with the seed row `Id = 1, NewStatusId = 1000, NewStatusName = "New", TicketType = "PROBLEM", Priority = "NONE", Severity = "NONE"`, and touches nothing else.

- [ ] **Step 4: Run the tests to check they pass**

Run the same command as Step 2.
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Psa/NinjaOne src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Audit/AuditActions.cs src/DotMarc/Migrations test/DotMarc.Tests/Psa/NinjaOne test/DotMarc.Tests/Audit/AuditCoverageTests.cs
git commit -m "Add NinjaOne settings, with saving, connecting and disconnecting audited"
```

---

### Task 3: OAuth token requests and the token cache

**Files:**
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneOAuth.cs`, `NinjaOneTokenCache.cs`
- Test: create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneTokenCacheTests.cs`

**Interfaces:**
- Consumes: `NinjaOneSettings`, `NinjaOneOptions.Scope` and the secret store keys (Task 2).
- Produces:
  - `NinjaOneConnectionLostException(string message)`
  - `NinjaOneTokenResponse(string AccessToken, int ExpiresInSeconds, string? RefreshToken)`
  - `NinjaOneOAuth`:
    - `static string AuthorizeUrl(NinjaOneSettings settings, string redirectUri, string state)`
    - `static Task<NinjaOneTokenResponse> RequestTokenAsync(HttpClient httpClient, string instance, IReadOnlyDictionary<string, string> form, CancellationToken ct)`
  - `NinjaOneTokenCache(ISecretStore secretStore, TimeProvider timeProvider)`:
    - `Task<string> GetTokenAsync(HttpClient httpClient, NinjaOneSettings settings, bool forceRefresh, CancellationToken ct)`
    - `Task ClearAsync(CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneTokenCacheTests.cs`:

```csharp
using System.Net;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

public sealed class NinjaOneTokenCacheTests
{
    private const string TokenJson = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1","token_type":"Bearer"}""";

    private static NinjaOneSettings Settings() => new() { Instance = "eu.ninjarmm.com", ClientId = "client-1", ClientSecretConfigured = true, RefreshTokenConfigured = true };

    private static FakeSecretStore Secrets(string refreshToken = "refresh-1") =>
        new() { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret", [NinjaOneSettings.RefreshTokenKey] = refreshToken } };

    [Fact]
    public async Task Refreshes_WithTheRefreshTokenGrant_AtTheInstancesTokenEndpoint()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson };
        var cache = new NinjaOneTokenCache(Secrets(), TimeProvider.System);

        var token = await cache.GetTokenAsync(new HttpClient(handler), Settings(), forceRefresh: false, CancellationToken.None);

        Assert.Equal("access-1", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://eu.ninjarmm.com/ws/oauth/token", request.RequestUri!.ToString());
        var form = handler.RequestBodies.Single();
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("client_id=client-1", form);
        Assert.Contains("client_secret=secret", form);
        Assert.Contains("refresh_token=refresh-1", form);
    }

    [Fact]
    public async Task ReusesTheAccessToken_UntilAMinuteBeforeItExpires_UnlessForced()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson };
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var cache = new NinjaOneTokenCache(Secrets(), clock);
        var httpClient = new HttpClient(handler);

        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(58));
        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        Assert.Single(handler.Requests);

        await cache.GetTokenAsync(httpClient, Settings(), forceRefresh: true, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(59.5));
        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ARotatedRefreshToken_IsSaved()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson.Replace("refresh-1", "refresh-2") };
        var secrets = Secrets();

        await new NinjaOneTokenCache(secrets, TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None);

        Assert.Equal("refresh-2", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", "invalid_grant")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Bad client"}""", "invalid_client - Bad client")]
    public async Task ARefusal_MeansTheConnectionIsLost_AndSaysWhy(HttpStatusCode status, string body, string reason)
    {
        var handler = new FakeHttpMessageHandler { StatusCode = status, ResponseBody = body };

        var exception = await Assert.ThrowsAsync<NinjaOneConnectionLostException>(() =>
            new NinjaOneTokenCache(Secrets(), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));

        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task AServerError_IsTransient_NotALostConnection()
    {
        var handler = new FakeHttpMessageHandler { StatusCode = HttpStatusCode.BadGateway, ResponseBody = "" };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new NinjaOneTokenCache(Secrets(), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));
    }

    [Theory]
    [InlineData(NinjaOneSettings.ClearedRefreshToken)]
    [InlineData("")]
    public async Task WithNoSavedSignIn_TheConnectionIsLost_WithoutCallingNinjaOne(string refreshToken)
    {
        var handler = new FakeHttpMessageHandler();

        await Assert.ThrowsAsync<NinjaOneConnectionLostException>(() =>
            new NinjaOneTokenCache(Secrets(refreshToken), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheAuthorizeUrl_AsksForTheCodeAndOfflineAccess()
    {
        var url = NinjaOneOAuth.AuthorizeUrl(Settings(), "https://dotmarc.example/psa/ninjaone/callback", "state-1");

        Assert.Equal("https://eu.ninjarmm.com/ws/oauth/authorize?response_type=code&client_id=client-1"
            + "&redirect_uri=https%3A%2F%2Fdotmarc.example%2Fpsa%2Fninjaone%2Fcallback&scope=monitoring%20management%20offline_access&state=state-1", url);
    }
}
```

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneTokenCacheTests"`
Expected: a build failure, because `NinjaOneTokenCache` doesn't exist.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/NinjaOne/NinjaOneOAuth.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne refused the saved sign-in (or there isn't one), so someone has to reconnect. A network fault or
/// server error is an ordinary HttpRequestException instead, and is worth retrying.</summary>
public sealed class NinjaOneConnectionLostException(string message) : Exception(message);

public sealed record NinjaOneTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null);

/// <summary>NinjaOne's OAuth endpoints: /ws/oauth/authorize to sign in and /ws/oauth/token for both grants.</summary>
public static class NinjaOneOAuth
{
    public static string AuthorizeUrl(NinjaOneSettings settings, string redirectUri, string state) =>
        $"https://{settings.Instance}/ws/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(settings.ClientId!)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(NinjaOneOptions.Scope)}&state={Uri.EscapeDataString(state)}";

    /// <summary>A 400 or 401 is NinjaOne refusing the grant, so it throws <see cref="NinjaOneConnectionLostException"/>
    /// with NinjaOne's reason. NinjaOne's error bodies never echo the secret or the token.</summary>
    public static async Task<NinjaOneTokenResponse> RequestTokenAsync(HttpClient httpClient, string instance, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{instance}/ws/oauth/token") { Content = new FormUrlEncodedContent(form) };
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new NinjaOneConnectionLostException(await DescribeRefusalAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"NinjaOne's sign-in service returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<NinjaOneTokenResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("NinjaOne's sign-in service returned an empty response.");
    }

    private static async Task<string> DescribeRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var summary = $"NinjaOne refused the sign-in ({(int)response.StatusCode})";
        try
        {
            var refusal = await response.Content.ReadFromJsonAsync<TokenRefusal>(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refusal?.Error))
            {
                return string.IsNullOrWhiteSpace(refusal.Description) ? $"{summary}: {refusal.Error}" : $"{summary}: {refusal.Error} - {refusal.Description}";
            }
        }
        catch (JsonException)
        {
            // Not an OAuth error body, so the status code will have to do.
        }

        return summary;
    }

    private sealed record TokenRefusal(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
```

`src/DotMarc/Psa/NinjaOne/NinjaOneTokenCache.cs`:

```csharp
using DotMarc.Notifications;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Holds NinjaOne's access token in memory and gets a new one with the saved refresh token. Each replica keeps
/// its own: NinjaOne hands back the same refresh token on each use, so replicas refreshing side by side don't trip over
/// each other. If NinjaOne ever returns a different one (sliding tokens may), it is saved.</summary>
public sealed class NinjaOneTokenCache(ISecretStore secretStore, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Key, string Token, DateTimeOffset ExpiresUtc)? _cached;

    public async Task<string> GetTokenAsync(HttpClient httpClient, NinjaOneSettings settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = $"{settings.Instance}|{settings.ClientId}";
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } cached && cached.Key == key && timeProvider.GetUtcNow() < cached.ExpiresUtc)
            {
                return cached.Token;
            }

            var clientSecret = await secretStore.GetSecretAsync(NinjaOneSettings.ClientSecretKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The NinjaOne client secret isn't saved.");
            var refreshToken = await secretStore.GetSecretAsync(NinjaOneSettings.RefreshTokenKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken) || refreshToken == NinjaOneSettings.ClearedRefreshToken)
            {
                throw new NinjaOneConnectionLostException("dotMARC has no saved NinjaOne sign-in.");
            }

            var response = await NinjaOneOAuth.RequestTokenAsync(httpClient, settings.Instance!, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
            }, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.RefreshToken) && response.RefreshToken != refreshToken)
            {
                await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, response.RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            // A minute early, so a call starting just before expiry doesn't race a 401.
            _cached = (key, response.AccessToken, timeProvider.GetUtcNow().AddSeconds(response.ExpiresInSeconds - 60));
            return response.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the access token, after a 401, a reconnect or a disconnect.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

- [ ] **Step 4: Run the tests to check they pass**

Run the same command as Step 2.
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Psa/NinjaOne/NinjaOneOAuth.cs src/DotMarc/Psa/NinjaOne/NinjaOneTokenCache.cs test/DotMarc.Tests/Psa/NinjaOne/NinjaOneTokenCacheTests.cs
git commit -m "Add NinjaOne's OAuth token requests and access token cache"
```

---

### Task 4: The connection recorder and the NinjaOne API client

**Files:**
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneConnectionRecorder.cs`, `INinjaOneClient.cs`, `NinjaOneClient.cs`
- Create: `test/DotMarc.Tests/Internal/FakeNinjaOneConnectionRecorder.cs`
- Test: create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneClientTests.cs` and `NinjaOneConnectionRecorderTests.cs`

**Interfaces:**
- Consumes: `NinjaOneTokenCache`, `NinjaOneConnectionLostException` (Task 3); `NinjaOneSettings`, `NinjaOneOptions`, `AuditActions.NinjaOneConnectionLost` (Task 2).
- Produces:
  - `INinjaOneConnectionRecorder`:
    - `Task ConnectionLostAsync(string reason, CancellationToken ct)`
    - `Task RefreshedAsync(CancellationToken ct)`
  - `NinjaOneConnectionRecorder(IDbContextFactory<DotMarcDbContext>, TimeProvider)`, which implements it.
  - `NinjaOneStatus(int StatusId, string Name, string? DisplayName, int? ParentId)`
  - `NinjaOneTicketStatus(int StatusId, string? Name, int? ParentId)`
  - `NinjaOneTicket(int Id, int Version, int? ClientId, int? TicketFormId, string Subject, string? Type, string? Priority, string? Severity, string? RequesterUid, NinjaOneTicketStatus? Status, bool Deleted)`, with `bool IsResolvedOrClosed`.
  - `INinjaOneClient`:
    - `ListOrganizationsAsync`, `ListTicketFormsAsync`, `ListStatusesAsync`
    - `CreateTicketAsync`, `GetTicketAsync`, `AddCommentAsync`, `ResolveTicketAsync`
    - `RefreshConnectionAsync`
    - The signatures are in Step 3.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Internal/FakeNinjaOneConnectionRecorder.cs`:

```csharp
using DotMarc.Psa.NinjaOne;

namespace DotMarc.Tests.Internal;

internal sealed class FakeNinjaOneConnectionRecorder : INinjaOneConnectionRecorder
{
    public List<string> LostReasons { get; } = [];
    public int Refreshes { get; private set; }

    public Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default)
    {
        LostReasons.Add(reason);
        return Task.CompletedTask;
    }

    public Task RefreshedAsync(CancellationToken cancellationToken = default)
    {
        Refreshes++;
        return Task.CompletedTask;
    }
}
```

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneClientTests.cs`. Each test queues the token response first, because the first call through a fresh cache signs in:

```csharp
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
        handler.ResponseBodies.Enqueue("""{"resultCode":"version_mismatch"}""");
        handler.ResponseBodies.Enqueue(OpenTicketJson.Replace("\"version\":1", "\"version\":2"));
        handler.ResponseBodies.Enqueue("{}");
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.OK, HttpStatusCode.OK })
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
```

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneConnectionRecorderTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneConnectionRecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneConnectionRecorderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        settings.RefreshTokenConfigured = true;
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task ALostConnection_IsSavedAndAuditedOnce()
    {
        var recorder = new NinjaOneConnectionRecorder(new FakeDbContextFactory(_connectionString), new FixedTimeProvider(Now));

        await recorder.ConnectionLostAsync("NinjaOne refused the sign-in (400): invalid_grant");
        await recorder.ConnectionLostAsync("again");

        await using var verify = CreateContext();
        var settings = await verify.NinjaOneSettings.SingleAsync();
        Assert.Equal((Now, "NinjaOne refused the sign-in (400): invalid_grant"), (settings.ConnectionLostUtc, settings.ConnectionLostReason));
        Assert.Equal(1, await verify.AuditEntries.CountAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneConnectionLost));
    }

    [Fact]
    public async Task ARefresh_IsTimestamped()
    {
        var recorder = new NinjaOneConnectionRecorder(new FakeDbContextFactory(_connectionString), new FixedTimeProvider(Now));

        await recorder.RefreshedAsync();

        await using var verify = CreateContext();
        Assert.Equal(Now, (await verify.NinjaOneSettings.SingleAsync()).LastRefreshUtc);
    }
}
```

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneClientTests|FullyQualifiedName~NinjaOneConnectionRecorderTests"`
Expected: a build failure, because `NinjaOneClient` doesn't exist.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/NinjaOne/NinjaOneConnectionRecorder.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Writes the connection's health to the settings row. The client records here, not on the alerting service,
/// because the alerting service already depends on the PSA providers. NinjaOneKeepAliveService turns the saved state
/// into the alert.</summary>
public interface INinjaOneConnectionRecorder
{
    Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default);
    Task RefreshedAsync(CancellationToken cancellationToken = default);
}

public sealed class NinjaOneConnectionRecorder(IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider) : INinjaOneConnectionRecorder
{
    private static readonly AuditActor KeepAliveActor = AuditActor.ForSystem("NinjaOne keep-alive");

    public async Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.RefreshTokenConfigured || settings.ConnectionLostUtc is not null)
        {
            return;
        }

        settings.ConnectionLostUtc = timeProvider.GetUtcNow();
        settings.ConnectionLostReason = reason.Length <= 500 ? reason : reason[..500];
        AuditLog.Record(context, KeepAliveActor, AuditActions.NinjaOneConnectionLost, AuditTarget.Settings("NinjaOne"), $"Lost the NinjaOne connection: {settings.ConnectionLostReason}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        settings.LastRefreshUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

`src/DotMarc/Psa/NinjaOne/INinjaOneClient.cs`:

```csharp
namespace DotMarc.Psa.NinjaOne;

public interface INinjaOneClient
{
    Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when NinjaOne has no such ticket.</summary>
    Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default);
    Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default);

    /// <summary>Moves the ticket to RESOLVED. NinjaOne won't let an update close a ticket, so its own rules close it
    /// later. Does nothing when the ticket is already resolved or closed, or no longer exists.</summary>
    Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default);

    /// <summary>Uses the refresh token now, so it doesn't lapse, and records that it worked.</summary>
    Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
}

public sealed record NinjaOneStatus(int StatusId, string Name, string? DisplayName, int? ParentId);

public sealed record NinjaOneTicketStatus(int StatusId, string? Name, int? ParentId);

public sealed record NinjaOneTicket(
    int Id, int Version, int? ClientId, int? TicketFormId, string Subject, string? Type, string? Priority, string? Severity,
    string? RequesterUid, NinjaOneTicketStatus? Status, bool Deleted)
{
    /// <summary>A custom status counts by the built-in status it sits under.</summary>
    public bool IsResolvedOrClosed => Status is { } status
        && (IsDone(status.StatusId) || status.ParentId is { } parentId && IsDone(parentId));

    private static bool IsDone(int statusId) => statusId is NinjaOneOptions.ResolvedStatusId or NinjaOneOptions.ClosedStatusId;
}
```

`src/DotMarc/Psa/NinjaOne/NinjaOneClient.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne's public API, v2, signed in as the user who connected dotMARC.</summary>
public sealed class NinjaOneClient(HttpClient httpClient, NinjaOneTokenCache tokenCache, INinjaOneConnectionRecorder recorder) : INinjaOneClient
{
    private const int PageSize = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        var organisations = new List<PsaCompany>();
        int? after = null;
        while (true)
        {
            var path = after is { } lastId ? $"organizations?pageSize={PageSize}&after={lastId}" : $"organizations?pageSize={PageSize}";
            var page = await GetJsonAsync<List<IdName>>(settings, path, cancellationToken).ConfigureAwait(false) ?? [];
            organisations.AddRange(page.Select(entry => new PsaCompany(entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name)));

            // A short page is the last one.
            if (page.Count < PageSize)
            {
                break;
            }

            after = page[^1].Id;
        }

        return organisations.OrderBy(organisation => organisation.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        var forms = await GetJsonAsync<List<IdName>>(settings, "ticketing/ticket-form", cancellationToken).ConfigureAwait(false) ?? [];
        return forms.Select(form => new PsaOption(form.Id, form.Name)).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) =>
        await GetJsonAsync<List<NinjaOneStatus>>(settings, "ticketing/statuses", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var body = new CreateTicketBody(
            int.Parse(request.CompanyId, CultureInfo.InvariantCulture),
            settings.TicketFormId!.Value,
            Truncate(request.Title, 200),
            new CommentBody(request.Body, false),
            settings.NewStatusId!.Value.ToString(CultureInfo.InvariantCulture),
            settings.TicketType,
            settings.Priority,
            settings.Severity);
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Post, Url(settings, "ticketing/ticket")) { Content = JsonContent.Create(body, options: JsonOptions) }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "POST ticketing/ticket", cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<IdOnly>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("NinjaOne created a ticket but didn't say its id.");
        return created.Id.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Get, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}")), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "GET ticketing/ticket", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<NinjaOneTicket>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, () =>
        {
            var content = new MultipartFormDataContent
            {
                { new StringContent(JsonSerializer.Serialize(new CommentBody(body, false), JsonOptions), Encoding.UTF8, "application/json"), "comment" }
            };
            return new HttpRequestMessage(HttpMethod.Post, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}/comment")) { Content = content };
        }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "POST ticketing/ticket comment", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ticket = await GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
            if (ticket is null || ticket.Deleted || ticket.IsResolvedOrClosed)
            {
                return;
            }

            // NinjaOne's update replaces the ticket, so everything it requires goes back as it was, with the new status
            // and the version just read.
            var update = new UpdateTicketBody(ticket.ClientId, ticket.TicketFormId, ticket.Subject, ticket.Type, ticket.Priority, ticket.Severity,
                ticket.RequesterUid, NinjaOneOptions.ResolvedStatusId.ToString(CultureInfo.InvariantCulture), ticket.Version);
            using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Put, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}")) { Content = JsonContent.Create(update, options: JsonOptions) }, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var versionConflict = response.StatusCode == HttpStatusCode.Conflict
                || response.StatusCode == HttpStatusCode.BadRequest && text.Contains("version", StringComparison.OrdinalIgnoreCase);
            if (attempt == 1 && versionConflict)
            {
                continue;
            }

            throw Failure(response.StatusCode, "PUT ticketing/ticket", text);
        }
    }

    public async Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        await GetTokenAsync(settings, forceRefresh: true, cancellationToken).ConfigureAwait(false);
        await recorder.RefreshedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(NinjaOneSettings settings, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Get, Url(settings, path)), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"GET {path.Split('?')[0]}", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends with the access token. A 401 drops it and tries once more with a fresh one; a second 401 means
    /// NinjaOne no longer accepts this sign-in.</summary>
    private async Task<HttpResponseMessage> SendAsync(NinjaOneSettings settings, Func<HttpRequestMessage> buildRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await GetTokenAsync(settings, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            using var request = buildRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            await tokenCache.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (attempt == 2)
            {
                const string reason = "NinjaOne refused dotMARC's access token twice in a row.";
                await recorder.ConnectionLostAsync(reason, cancellationToken).ConfigureAwait(false);
                throw new NinjaOneConnectionLostException(reason);
            }
        }
    }

    private async Task<string> GetTokenAsync(NinjaOneSettings settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            return await tokenCache.GetTokenAsync(httpClient, settings, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (NinjaOneConnectionLostException exception)
        {
            await recorder.ConnectionLostAsync(exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, what, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    // NinjaOne's error bodies carry a resultCode and an incident id, never credentials.
    private static HttpRequestException Failure(HttpStatusCode status, string what, string body)
    {
        var advice = status == HttpStatusCode.Forbidden
            ? "NinjaOne refused the request: check the connected user can manage tickets, and the client app has the Monitoring and Management scopes."
            : $"NinjaOne returned {(int)status} for {what}.";
        return new HttpRequestException(string.IsNullOrWhiteSpace(body) ? advice : $"{advice} NinjaOne said: {Truncate(body, 300)}", null, status);
    }

    private static string Url(NinjaOneSettings settings, string path) => $"https://{settings.Instance}/v2/{path}";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed record IdName(int Id, string Name);
    private sealed record IdOnly(int Id);
    private sealed record CommentBody(string Body, bool Public);
    private sealed record CreateTicketBody(int ClientId, int TicketFormId, string Subject, CommentBody Description, string Status, string Type, string Priority, string Severity);
    private sealed record UpdateTicketBody(int? ClientId, int? TicketFormId, string Subject, string? Type, string? Priority, string? Severity, string? RequesterUid, string Status, int Version);
}
```

- [ ] **Step 4: Run the tests to check they pass**

Run the same command as Step 2.
Expected: all pass.

`AddComment_SendsAPrivateCommentAsMultipart` reads the multipart body as a string. If `StringContent` gets a `charset` parameter that `FakeHttpMessageHandler` can't read, relax the assertion to look for `"body":"Alert resolved."` and `"public":false` separately, and record a Ruling.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Psa/NinjaOne test/DotMarc.Tests/Internal/FakeNinjaOneConnectionRecorder.cs test/DotMarc.Tests/Psa/NinjaOne
git commit -m "Add the NinjaOne API client, resolving tickets with a full update"
```

---

### Task 5: The NinjaOne provider and registration

**Files:**
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneProvider.cs`
- Modify: `src/DotMarc/Program.cs` (the PSA registrations, lines 172-190)
- Test: create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneProviderTests.cs`

**Interfaces:**
- Consumes: `INinjaOneClient` and `NinjaOneTicket` (Task 4); `NinjaOneSettingsService.GetAsync` (Task 2).
- Produces: `NinjaOneProvider(INinjaOneClient client, ILogger<NinjaOneProvider> logger) : IPsaProvider` and `static IReadOnlyList<string> MissingSettings(NinjaOneSettings settings)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneProviderTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured, settings.RefreshTokenConfigured, settings.TicketFormId) =
            (true, "eu.ninjarmm.com", "client-1", true, true, 1);
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private sealed class FakeNinjaOneClient : INinjaOneClient
    {
        public NinjaOneTicket? Ticket { get; set; }
        public Exception? CommentFailure { get; set; }
        public List<string> Calls { get; } = [];

        public Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(Ticket);

        public Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add($"comment {ticketId}: {body}");
            return CommentFailure is null ? Task.CompletedTask : Task.FromException(CommentFailure);
        }

        public Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"resolve {ticketId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static NinjaOneTicket Ticket(int statusId, int? parentId = null, bool deleted = false) =>
        new(1004, 1, 1, 1, "SPF broken", "PROBLEM", "NONE", "NONE", "user-1", new NinjaOneTicketStatus(statusId, "X", parentId), deleted);

    [Fact]
    public async Task IsReady_WhenConnectedWithAFormAndStatus()
    {
        await using var context = CreateContext();

        var readiness = await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetReadinessAsync(context);

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void MissingSettings_NamesEachGap_AndALostConnection()
    {
        var settings = new NinjaOneSettings { RefreshTokenConfigured = true, ConnectionLostUtc = DateTimeOffset.UtcNow, NewStatusId = null };

        Assert.Equal(["instance", "client ID", "client secret", "connection", "ticket form", "new ticket status"], NinjaOneProvider.MissingSettings(settings));
    }

    [Theory]
    [InlineData(1000, null, false, PsaTicketState.Open)]
    [InlineData(5000, null, false, PsaTicketState.Closed)]
    [InlineData(6000, null, false, PsaTicketState.Closed)]
    [InlineData(5001, 5000, false, PsaTicketState.Closed)]
    [InlineData(1000, null, true, PsaTicketState.Missing)]
    public async Task TicketState_FollowsTheStatusOrItsParent(int statusId, int? parentId, bool deleted, PsaTicketState expected)
    {
        await using var context = CreateContext();
        var client = new FakeNinjaOneClient { Ticket = Ticket(statusId, parentId, deleted) };

        Assert.Equal(expected, await new NinjaOneProvider(client, NullLogger<NinjaOneProvider>.Instance).GetTicketStateAsync(context, "1004"));
    }

    [Fact]
    public async Task ATicketNinjaOneDoesNotHave_IsMissing()
    {
        await using var context = CreateContext();

        Assert.Equal(PsaTicketState.Missing, await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetTicketStateAsync(context, "1004"));
    }

    [Fact]
    public async Task Close_AddsTheNoteThenResolves_EvenWhenTheNoteFails()
    {
        await using var context = CreateContext();
        var client = new FakeNinjaOneClient { CommentFailure = new HttpRequestException("comment refused") };

        await new NinjaOneProvider(client, NullLogger<NinjaOneProvider>.Instance).CloseTicketAsync(context, "1004", "Alert resolved.");

        Assert.Equal(["comment 1004: Alert resolved.", "resolve 1004"], client.Calls);
    }

    [Fact]
    public async Task TicketLinks_OpenTheTicketOnTheInstance()
    {
        await using var context = CreateContext();

        Assert.Equal("https://eu.ninjarmm.com/#/ticketing/ticket/{0}",
            await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetTicketUrlTemplateAsync(context));
    }
}
```

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneProviderTests"`
Expected: a build failure, because `NinjaOneProvider` doesn't exist.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/NinjaOne/NinjaOneProvider.cs`:

```csharp
using DotMarc.Data;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne ticketing behind the shared PSA contract.</summary>
public sealed class NinjaOneProvider(INinjaOneClient client, ILogger<NinjaOneProvider> logger) : IPsaProvider
{
    public PsaKind Kind => PsaKind.NinjaOne;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(NinjaOneSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Instance)) missing.Add("instance");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        if (!settings.IsConnected) missing.Add("connection");
        if (settings.TicketFormId is null) missing.Add("ticket form");
        if (settings.NewStatusId is null) missing.Add("new ticket status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListOrganizationsAsync(await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var ticket = await client.GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null || ticket.Deleted)
        {
            return PsaTicketState.Missing;
        }

        return ticket.IsResolvedOrClosed ? PsaTicketState.Closed : PsaTicketState.Open;
    }

    /// <summary>The note first, so the ticket says why it was resolved. A refused note doesn't stop the resolve.</summary>
    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.AddCommentAsync(settings, ticketId, note, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Adding the closing note to NinjaOne ticket {TicketId} failed; resolving it anyway", ticketId);
        }

        await client.ResolveTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(settings.Instance) ? null : $"https://{settings.Instance}/#/ticketing/ticket/{{0}}";
    }
}
```

In `Program.cs`, after the ConnectWise `AddHttpClient` line, add:

```csharp
builder.Services.AddSingleton<DotMarc.Psa.NinjaOne.NinjaOneTokenCache>();
builder.Services.AddSingleton<DotMarc.Psa.NinjaOne.INinjaOneConnectionRecorder, DotMarc.Psa.NinjaOne.NinjaOneConnectionRecorder>();
builder.Services.AddHttpClient<DotMarc.Psa.NinjaOne.INinjaOneClient, DotMarc.Psa.NinjaOne.NinjaOneClient>();
```

In the demo branch, register NinjaOne second:

```csharp
    builder.Services.AddSingleton<DotMarc.Psa.IPsaProvider>(services => new DotMarc.Demo.DemoPsaProvider(DotMarc.Psa.PsaKind.HaloPsa, services.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<DotMarc.Psa.IPsaProvider>(services => new DotMarc.Demo.DemoPsaProvider(DotMarc.Psa.PsaKind.NinjaOne, services.GetRequiredService<TimeProvider>()));
```

In the real branch, add NinjaOne second:

```csharp
    builder.Services.AddSingleton<DotMarc.Psa.IPsaProvider, HaloPsaProvider>();
    builder.Services.AddSingleton<DotMarc.Psa.IPsaProvider, DotMarc.Psa.NinjaOne.NinjaOneProvider>();
```

`NinjaOneProvider` is a singleton that holds a typed-client `INinjaOneClient`, the same as ConnectWise. That's acceptable here because the client keeps no state of its own.

- [ ] **Step 4: Run the tests to check they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneProviderTests|FullyQualifiedName~ProgramDiValidationTests|FullyQualifiedName~DemoPsaProviderTests"`
Expected: all pass. If `DemoPsaProviderTests` lists the demo kinds, add `PsaKind.NinjaOne` in second place.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Psa/NinjaOne/NinjaOneProvider.cs src/DotMarc/Program.cs test/DotMarc.Tests/Psa/NinjaOne/NinjaOneProviderTests.cs test/DotMarc.Tests/Demo
git commit -m "Raise, track and resolve NinjaOne tickets through the shared PSA contract"
```

---

### Task 6: The lost-connection alert and the keep-alive service

**Files:**
- Modify: `src/DotMarc/Notifications/AlertTypes.cs`, `src/DotMarc/Notifications/AlertingService.cs`
- Modify: `test/DotMarc.Tests/Internal/FakeAlertingService.cs`, `test/DotMarc.Tests/Internal/RecordingAlerts.cs`, `test/DotMarc.Tests/Ingestion/PollingServiceDiActivationTests.cs` (its inner `IAlertingService` fake)
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneKeepAliveService.cs`
- Modify: `src/DotMarc/Program.cs` (hosted services)
- Regenerate: the website's OpenAPI file, with `node scripts/update-openapi.mjs`
- Test: modify `test/DotMarc.Tests/Notifications/AlertTypesTests.cs`; create `test/DotMarc.Tests/Notifications/PsaConnectionAlertTests.cs` and `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneKeepAliveServiceTests.cs`

**Interfaces:**
- Consumes: `INinjaOneClient.RefreshConnectionAsync` and `NinjaOneConnectionLostException` (Tasks 3 and 4); `NinjaOneSettingsService.GetAsync` (Task 2).
- Produces:
  - `AlertTypes.PsaConnectionLost`.
  - On `IAlertingService`:
    - `Task RaisePsaConnectionLostAsync(PsaKind psa, string reason, CancellationToken ct = default)`
    - `Task ResolvePsaConnectionLostAsync(PsaKind psa, CancellationToken ct = default)`
  - `AlertingService.PsaConnectionAlertSubject(PsaKind psa)`.
  - `NinjaOneKeepAliveService.RunOnceAsync(CancellationToken)` and `NinjaOneKeepAliveService.Interval`.

- [ ] **Step 1: Write the failing tests**

In `AlertTypesTests.All_ListsTheAlertTypesDotMarcRaises`, append `"PsaConnectionLost"` after `"ClientReportFailed"`.

Create `test/DotMarc.Tests/Notifications/PsaConnectionAlertTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class PsaConnectionAlertTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaConnectionAlertTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NotificationSettings.SingleAsync();
        settings.Enabled = true;
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private AlertingService Alerting() =>
        new(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), PsaTestSupport.ForHalo(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    [Fact]
    public async Task ALostConnection_RaisesOneAlert_ThatResolves()
    {
        var alerting = Alerting();

        await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, "NinjaOne refused the sign-in (400): invalid_grant");
        await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, "NinjaOne refused the sign-in (400): invalid_grant");

        await using (var verify = CreateContext())
        {
            var alert = await verify.AlertEvents.SingleAsync();
            Assert.Equal((AlertTypes.PsaConnectionLost, "NinjaOne connection", "PSA connection lost", false),
                (alert.AlertType, alert.DomainName, alert.Title, alert.IsResolved));
            Assert.Contains("invalid_grant", alert.Message);
            Assert.Contains("Settings > PSA > NinjaOne", alert.Message);
        }

        await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne);

        await using var afterResolve = CreateContext();
        Assert.True((await afterResolve.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public void TheAlertType_DoesNotRaiseTicketsByDefault()
    {
        Assert.False(AlertTypes.Find(AlertTypes.PsaConnectionLost)!.CreatesTicketByDefault);
    }
}
```

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneKeepAliveServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneKeepAliveServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneKeepAliveServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private async Task SetUpAsync(bool enabled, bool connected, DateTimeOffset? lostUtc = null)
    {
        await using var context = CreateContext();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured, settings.RefreshTokenConfigured) = (enabled, "eu.ninjarmm.com", "client-1", true, connected);
        (settings.ConnectionLostUtc, settings.ConnectionLostReason) = (lostUtc, lostUtc is null ? null : "invalid_grant");
        await context.SaveChangesAsync();
    }

    /// <summary>Records refresh calls; set Lose to make the refresh fail the way the real client does.</summary>
    private sealed class RefreshingClient(string connectionString) : INinjaOneClient
    {
        public int Refreshes { get; private set; }
        public bool Lose { get; set; }
        public bool Transient { get; set; }

        public async Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            if (Transient)
            {
                throw new HttpRequestException("NinjaOne's sign-in service returned 502.");
            }

            if (Lose)
            {
                await new NinjaOneConnectionRecorder(new FakeDbContextFactory(connectionString), TimeProvider.System).ConnectionLostAsync("invalid_grant", cancellationToken);
                throw new NinjaOneConnectionLostException("invalid_grant");
            }
        }

        public Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Records PSA connection alerts; nothing else is used.</summary>
    private sealed class ConnectionAlerts : IAlertingService
    {
        public List<string> Raised { get; } = [];
        public int Resolves { get; private set; }

        public Task RaisePsaConnectionLostAsync(PsaKind psa, string reason, CancellationToken cancellationToken = default)
        {
            Raised.Add($"{psa}: {reason}");
            return Task.CompletedTask;
        }

        public Task ResolvePsaConnectionLostAsync(PsaKind psa, CancellationToken cancellationToken = default)
        {
            Resolves++;
            return Task.CompletedTask;
        }

        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private NinjaOneKeepAliveService Service(INinjaOneClient client, IAlertingService alerts) =>
        new(new FakeDbContextFactory(_connectionString), client, alerts, NullLogger<NinjaOneKeepAliveService>.Instance);

    [Fact]
    public async Task WhenConnected_ItRefreshes_AndResolvesAnyOldAlert()
    {
        await SetUpAsync(enabled: true, connected: true);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, client.Refreshes);
        Assert.Empty(alerts.Raised);
        Assert.Equal(1, alerts.Resolves);
    }

    [Fact]
    public async Task ARefusedRefresh_RaisesTheAlert()
    {
        await SetUpAsync(enabled: true, connected: true);
        var alerts = new ConnectionAlerts();

        await Service(new RefreshingClient(_connectionString) { Lose = true }, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal("NinjaOne: invalid_grant", Assert.Single(alerts.Raised));
    }

    [Fact]
    public async Task ATransientFailure_RaisesNothing_AndKeepsTheConnection()
    {
        await SetUpAsync(enabled: true, connected: true);
        var alerts = new ConnectionAlerts();

        await Service(new RefreshingClient(_connectionString) { Transient = true }, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Empty(alerts.Raised);
        await using var verify = CreateContext();
        Assert.True((await verify.NinjaOneSettings.SingleAsync()).IsConnected);
    }

    [Fact]
    public async Task ALossFoundByATicketCall_IsRaisedOnTheNextRun_WithoutRefreshing()
    {
        await SetUpAsync(enabled: true, connected: true, lostUtc: DateTimeOffset.UtcNow);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, client.Refreshes);
        Assert.Equal("NinjaOne: invalid_grant", Assert.Single(alerts.Raised));
    }

    [Fact]
    public async Task WhenNinjaOneIsTurnedOff_ItDoesNotRefresh_AndResolvesTheAlert()
    {
        await SetUpAsync(enabled: false, connected: true, lostUtc: DateTimeOffset.UtcNow);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, client.Refreshes);
        Assert.Empty(alerts.Raised);
        Assert.Equal(1, alerts.Resolves);
    }

    [Fact]
    public void ItRefreshesEvery20Minutes_TwiceInsideTheHourARefreshTokenLasts()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), NinjaOneKeepAliveService.Interval);
    }
}
```

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypesTests|FullyQualifiedName~PsaConnectionAlertTests|FullyQualifiedName~NinjaOneKeepAliveServiceTests"`
Expected: a build failure, because `AlertTypes.PsaConnectionLost` and `RaisePsaConnectionLostAsync` don't exist.

- [ ] **Step 3: Implement**

In `AlertTypes.cs`, add the constant after `ClientReportFailed`:

```csharp
    public const string PsaConnectionLost = "PsaConnectionLost";
```

Add it last in `All`:

```csharp
        new(PsaConnectionLost, "PSA connection lost", "dotMARC can no longer sign in to a PSA, so it isn't raising tickets there until someone reconnects.", CreatesTicketByDefault: false),
```

In `AlertingService.cs`, add to `IAlertingService`:

```csharp
    Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default);
    Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default);
```

Add these members to `AlertingService`, after `ResolveClientReportFailedAsync`:

```csharp
    /// <summary>What a PSA's lost-connection alert is about. Stored where a domain alert stores its domain name.</summary>
    public static string PsaConnectionAlertSubject(DotMarc.Psa.PsaKind psa) => $"{DotMarc.Psa.PsaKindNames.DisplayName(psa)} connection";

    public async Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var name = DotMarc.Psa.PsaKindNames.DisplayName(psa);
        await EnsureAlertAsync(db, settings, PsaConnectionAlertSubject(psa), AlertTypes.PsaConnectionLost, "Warning", "PSA connection lost",
            $"dotMARC can no longer refresh its {name} sign-in ({reason}). New alerts won't raise {name} tickets until someone reconnects under Settings > PSA > {name}.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default)
    {
        var subject = PsaConnectionAlertSubject(psa);
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Called every keep-alive run, so a quiet run costs one query.
            if (!await db.AlertEvents.AnyAsync(alert => alert.AlertType == AlertTypes.PsaConnectionLost && alert.DomainName == subject && !alert.IsResolved, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ResolveAllCopiesAsync(subject, AlertTypes.PsaConnectionLost, cancellationToken).ConfigureAwait(false);
    }
```

Add both methods to the three test fakes as no-ops: `FakeAlertingService`, `RecordingAlerts`, and the inner fake in `PollingServiceDiActivationTests`:

```csharp
    public Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default) => Task.CompletedTask;
```

Create `src/DotMarc/Psa/NinjaOne/NinjaOneKeepAliveService.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne's refresh tokens last an hour unless NinjaOne support turns on sliding tokens, so dotMARC uses its
/// token at startup and every 20 minutes even when nothing is ticketing. This is also where the lost-connection alert is
/// raised and resolved. It follows the saved state, so a loss a ticket call found is raised on the next run too.</summary>
public sealed class NinjaOneKeepAliveService(IDbContextFactory<DotMarcDbContext> dbFactory, INinjaOneClient client, IAlertingService alerting, ILogger<NinjaOneKeepAliveService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The NinjaOne keep-alive failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Enabled && settings.IsConnected)
        {
            try
            {
                await client.RefreshConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (NinjaOneConnectionLostException)
            {
                // The client has saved the loss; the alert follows below.
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Refreshing the NinjaOne sign-in failed; trying again in {Interval}", Interval);
            }

            settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.Enabled && settings.RefreshTokenConfigured && settings.ConnectionLostUtc is not null)
        {
            await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, settings.ConnectionLostReason ?? "NinjaOne refused the saved sign-in", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NinjaOneSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
```

In `Program.cs`, after `AddHostedService<DotMarc.Psa.PsaTicketPoller>()`:

```csharp
builder.Services.AddHostedService<DotMarc.Psa.NinjaOne.NinjaOneKeepAliveService>();
```

The alert type list feeds the API's `type` filter, so regenerate the OpenAPI document:

```bash
node scripts/update-openapi.mjs
```

- [ ] **Step 4: Run the tests to check they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypesTests|FullyQualifiedName~PsaConnectionAlertTests|FullyQualifiedName~NinjaOneKeepAliveServiceTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~ProgramDiValidationTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Notifications src/DotMarc/Psa/NinjaOne/NinjaOneKeepAliveService.cs src/DotMarc/Program.cs test/DotMarc.Tests website
git commit -m "Keep the NinjaOne sign-in alive and alert when the connection is lost"
```

---

### Task 7: Connecting through NinjaOne's sign-in

**Files:**
- Create: `src/DotMarc/Psa/NinjaOne/NinjaOneStateProtector.cs`, `NinjaOneConnector.cs`
- Modify: `src/DotMarc/Program.cs` (registrations, plus two endpoints next to the DNS push ones, around line 640)
- Test: create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneStateProtectorTests.cs` and `NinjaOneConnectorTests.cs`

**Interfaces:**
- Consumes: `NinjaOneOAuth` (Task 3), `NinjaOneSettingsService.ConnectAsync` (Task 2), `NinjaOneTokenCache.ClearAsync` (Task 3), `IAlertingService.ResolvePsaConnectionLostAsync` (Task 6).
- Produces:
  - `NinjaOneStateProtector(IDataProtectionProvider)`:
    - `string Protect(string userKey, DateTimeOffset nowUtc)`
    - `bool IsValid(string state, string userKey, DateTimeOffset nowUtc)`
    - `static string UserKey(AuditActor actor)`
  - `NinjaOneConnectOutcome { Connected, Cancelled, InvalidState, NotConfigured, Refused, NoRefreshToken, Failed }`
  - `NinjaOneConnector(HttpClient, IDbContextFactory<DotMarcDbContext>, ISecretStore, NinjaOneStateProtector, NinjaOneTokenCache, IAlertingService, TimeProvider, ILogger<NinjaOneConnector>)`:
    - `Task<string?> BuildAuthorizationUrlAsync(string redirectUri, string state, CancellationToken ct)`
    - `Task<NinjaOneConnectOutcome> CompleteAsync(AuditActor actor, string? state, string? code, string? error, string redirectUri, CancellationToken ct)`
    - `static string RedirectUri(HttpRequest request)`
    - `static string OutcomeKey(NinjaOneConnectOutcome outcome)`

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneStateProtectorTests.cs`:

```csharp
using DotMarc.Psa.NinjaOne;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

public sealed class NinjaOneStateProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly NinjaOneStateProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void TheStateIsValid_ForTheSameUser_WithinTenMinutes()
    {
        var state = _protector.Protect("user-1", Now);

        Assert.True(_protector.IsValid(state, "user-1", Now.AddMinutes(9)));
    }

    [Fact]
    public void AnotherUsersState_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now), "user-2", Now));

    [Fact]
    public void AStateOlderThanTenMinutes_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now), "user-1", Now.AddMinutes(11)));

    [Fact]
    public void ATamperedState_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now) + "x", "user-1", Now));
}
```

Create `test/DotMarc.Tests/Psa/NinjaOne/NinjaOneConnectorTests.cs`:

```csharp
using System.Net;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneConnectorTests : IAsyncLifetime
{
    private const string RedirectUri = "https://dotmarc.example/psa/ninjaone/callback";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private readonly NinjaOneStateProtector _protector = new(new EphemeralDataProtectionProvider());
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneConnectorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured) = (true, "eu.ninjarmm.com", "client-1", true);
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private (NinjaOneConnector Connector, FakeHttpMessageHandler Handler, FakeSecretStore Secrets, RecordingConnectionAlerts Alerts) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret" } };
        var alerts = new RecordingConnectionAlerts();
        var connector = new NinjaOneConnector(new HttpClient(handler), new FakeDbContextFactory(_connectionString), secrets, _protector,
            new NinjaOneTokenCache(secrets, TimeProvider.System), alerts, new FixedTimeProvider(Now), NullLogger<NinjaOneConnector>.Instance);
        return (connector, handler, secrets, alerts);
    }

    private string StateFor(AuditActor actor) => _protector.Protect(NinjaOneStateProtector.UserKey(actor), Now);

    [Fact]
    public async Task BuildsTheAuthorizeUrl_OnceTheSignInIsSaved()
    {
        var (connector, _, _, _) = Create();

        var url = await connector.BuildAuthorizationUrlAsync(RedirectUri, "state-1", CancellationToken.None);

        Assert.StartsWith("https://eu.ninjarmm.com/ws/oauth/authorize?response_type=code&client_id=client-1", url);
    }

    [Fact]
    public async Task ASuccessfulSignIn_StoresTheRefreshToken_AndResolvesTheAlert()
    {
        var (connector, handler, secrets, alerts) = Create();
        handler.ResponseBody = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1"}""";

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.Connected, outcome);
        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        var form = Assert.Single(handler.RequestBodies);
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code=code-1", form);
        Assert.Contains("redirect_uri=https%3A%2F%2Fdotmarc.example%2Fpsa%2Fninjaone%2Fcallback", form);
        Assert.Equal(1, alerts.Resolves);
        await using var verify = CreateContext();
        Assert.True((await verify.NinjaOneSettings.SingleAsync()).IsConnected);
    }

    [Fact]
    public async Task AnotherUsersState_IsRefused_AndNothingIsSaved()
    {
        var (connector, handler, secrets, _) = Create();
        var someoneElse = AuditActor.ForUser("other-object-id", "other@example.com", "Someone Else");

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(someoneElse), "code-1", null, RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.InvalidState, outcome);
        Assert.Empty(handler.Requests);
        Assert.False(secrets.Secrets.ContainsKey(NinjaOneSettings.RefreshTokenKey));
    }

    [Fact]
    public async Task ACancelledSignIn_SavesNothing()
    {
        var (connector, handler, _, _) = Create();

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), null, "access_denied", RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.Cancelled, outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARefusedCode_IsReported()
    {
        var (connector, handler, _, _) = Create();
        handler.StatusCode = HttpStatusCode.BadRequest;
        handler.ResponseBody = """{"error":"invalid_grant"}""";

        Assert.Equal(NinjaOneConnectOutcome.Refused,
            await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None));
    }

    [Fact]
    public async Task NoRefreshToken_MeansTheAppIsMissingTheRefreshTokenGrant()
    {
        var (connector, handler, secrets, _) = Create();
        handler.ResponseBody = """{"access_token":"access-1","expires_in":3600}""";

        Assert.Equal(NinjaOneConnectOutcome.NoRefreshToken,
            await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None));
        Assert.False(secrets.Secrets.ContainsKey(NinjaOneSettings.RefreshTokenKey));
    }

    [Theory]
    [InlineData(NinjaOneConnectOutcome.Connected, "connected")]
    [InlineData(NinjaOneConnectOutcome.InvalidState, "expired")]
    [InlineData(NinjaOneConnectOutcome.NoRefreshToken, "no-refresh-token")]
    public void Outcomes_HaveShortKeysForTheSettingsPage(NinjaOneConnectOutcome outcome, string key) =>
        Assert.Equal(key, NinjaOneConnector.OutcomeKey(outcome));

    /// <summary>Counts lost-connection resolves; nothing else is used.</summary>
    private sealed class RecordingConnectionAlerts : DotMarc.Notifications.IAlertingService
    {
        public int Resolves { get; private set; }

        public Task ResolvePsaConnectionLostAsync(PsaKind psa, CancellationToken cancellationToken = default)
        {
            Resolves++;
            return Task.CompletedTask;
        }

        public Task RaisePsaConnectionLostAsync(PsaKind psa, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, DotMarc.Reporting.ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
```

`TestActors.Admin` is the admin actor used across the tests. If it has no `ObjectId`, `UserKey` falls back to the email or the name. Check `TestActors.cs` and keep the test as written.

- [ ] **Step 2: Run the tests to check they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneStateProtectorTests|FullyQualifiedName~NinjaOneConnectorTests"`
Expected: a build failure, because `NinjaOneConnector` doesn't exist.

- [ ] **Step 3: Implement**

`src/DotMarc/Psa/NinjaOne/NinjaOneStateProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using DotMarc.Audit;
using Microsoft.AspNetCore.DataProtection;

namespace DotMarc.Psa.NinjaOne;

/// <summary>The OAuth state for connecting NinjaOne: signed, bound to the user who started it, and good for 10
/// minutes. That stops a sign-in started by one person, or replayed later, from connecting dotMARC to someone else's
/// NinjaOne account.</summary>
public sealed class NinjaOneStateProtector(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("DotMarc.NinjaOne.ConnectState.v1");

    public static string UserKey(AuditActor actor) => actor.ObjectId ?? actor.Email ?? actor.Name;

    public string Protect(string userKey, DateTimeOffset nowUtc) =>
        _protector.Protect(JsonSerializer.Serialize(new ConnectState(userKey, nowUtc.Add(Lifetime))));

    public bool IsValid(string state, string userKey, DateTimeOffset nowUtc)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<ConnectState>(_protector.Unprotect(state));
            return decoded is not null && decoded.UserKey == userKey && decoded.ExpiresAtUtc > nowUtc;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private sealed record ConnectState(string UserKey, DateTimeOffset ExpiresAtUtc);
}
```

`src/DotMarc/Psa/NinjaOne/NinjaOneConnector.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

public enum NinjaOneConnectOutcome { Connected, Cancelled, InvalidState, NotConfigured, Refused, NoRefreshToken, Failed }

/// <summary>Connects dotMARC to NinjaOne with the authorization code flow, behind /psa/ninjaone/connect and
/// /psa/ninjaone/callback.</summary>
public sealed class NinjaOneConnector(
    HttpClient httpClient, IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, NinjaOneStateProtector stateProtector,
    NinjaOneTokenCache tokenCache, IAlertingService alerting, TimeProvider timeProvider, ILogger<NinjaOneConnector> logger)
{
    public const string CallbackPath = "/psa/ninjaone/callback";

    public static string RedirectUri(HttpRequest request) => $"{request.Scheme}://{request.Host}{CallbackPath}";

    public static string OutcomeKey(NinjaOneConnectOutcome outcome) => outcome switch
    {
        NinjaOneConnectOutcome.Connected => "connected",
        NinjaOneConnectOutcome.Cancelled => "cancelled",
        NinjaOneConnectOutcome.InvalidState => "expired",
        NinjaOneConnectOutcome.NotConfigured => "not-configured",
        NinjaOneConnectOutcome.Refused => "refused",
        NinjaOneConnectOutcome.NoRefreshToken => "no-refresh-token",
        _ => "failed"
    };

    /// <summary>Null until the instance, client ID and client secret are saved.</summary>
    public async Task<string?> BuildAuthorizationUrlAsync(string redirectUri, string state, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return IsConfigured(settings) ? NinjaOneOAuth.AuthorizeUrl(settings, redirectUri, state) : null;
    }

    public async Task<NinjaOneConnectOutcome> CompleteAsync(AuditActor actor, string? state, string? code, string? error, string redirectUri, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        if (state is null || !stateProtector.IsValid(state, NinjaOneStateProtector.UserKey(actor), nowUtc))
        {
            return NinjaOneConnectOutcome.InvalidState;
        }

        if (error is not null || string.IsNullOrEmpty(code))
        {
            return NinjaOneConnectOutcome.Cancelled;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clientSecret = await secretStore.GetSecretAsync(NinjaOneSettings.ClientSecretKey, cancellationToken).ConfigureAwait(false);
        if (!IsConfigured(settings) || string.IsNullOrEmpty(clientSecret))
        {
            return NinjaOneConnectOutcome.NotConfigured;
        }

        NinjaOneTokenResponse token;
        try
        {
            token = await NinjaOneOAuth.RequestTokenAsync(httpClient, settings.Instance!, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (NinjaOneConnectionLostException exception)
        {
            logger.LogWarning("NinjaOne refused the sign-in code: {Reason}", exception.Message);
            return NinjaOneConnectOutcome.Refused;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "Exchanging the NinjaOne sign-in code failed");
            return NinjaOneConnectOutcome.Failed;
        }

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            logger.LogWarning("NinjaOne signed in but returned no refresh token. The client app needs the Refresh token grant type.");
            return NinjaOneConnectOutcome.NoRefreshToken;
        }

        await NinjaOneSettingsService.ConnectAsync(context, actor, secretStore, token.RefreshToken, nowUtc, cancellationToken).ConfigureAwait(false);
        await tokenCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne, cancellationToken).ConfigureAwait(false);
        return NinjaOneConnectOutcome.Connected;
    }

    private static bool IsConfigured(NinjaOneSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Instance) && !string.IsNullOrWhiteSpace(settings.ClientId) && settings.ClientSecretConfigured;
}
```

In `Program.cs`, add the registrations after the NinjaOne client line from Task 5:

```csharp
builder.Services.AddSingleton<DotMarc.Psa.NinjaOne.NinjaOneStateProtector>();
builder.Services.AddHttpClient<DotMarc.Psa.NinjaOne.NinjaOneConnector>();
```

Add the endpoints just before the HaloPSA webhook endpoint, after the DNS push callback:

```csharp
// Connecting NinjaOne: an admin starts here from the NinjaOne tab, signs in at NinjaOne, and comes back to the
// callback. Only from dotMARC's own pages, for the same reason as DNS push (see DnsPushStartGuard).
app.MapGet("/psa/ninjaone/connect", async (HttpContext httpContext, DotMarc.Psa.NinjaOne.NinjaOneConnector connector, DotMarc.Psa.NinjaOne.NinjaOneStateProtector stateProtector, TimeProvider timeProvider) =>
{
    if (!DnsPushStartGuard.IsFromThisSite(httpContext.Request))
    {
        return Results.BadRequest();
    }

    var actor = DotMarc.Audit.AuditActor.FromPrincipal(httpContext.User);
    var state = stateProtector.Protect(DotMarc.Psa.NinjaOne.NinjaOneStateProtector.UserKey(actor), timeProvider.GetUtcNow());
    var url = await connector.BuildAuthorizationUrlAsync(DotMarc.Psa.NinjaOne.NinjaOneConnector.RedirectUri(httpContext.Request), state, httpContext.RequestAborted);
    return Results.Redirect(url ?? "/psa/settings?psa=ninjaone&ninjaone=not-configured");
}).RequireAuthorization(nameof(Permission.AlertsManage));

app.MapGet(DotMarc.Psa.NinjaOne.NinjaOneConnector.CallbackPath, async (string? code, string? state, string? error, HttpContext httpContext, DotMarc.Psa.NinjaOne.NinjaOneConnector connector) =>
{
    var outcome = await connector.CompleteAsync(DotMarc.Audit.AuditActor.FromPrincipal(httpContext.User), state, code, error,
        DotMarc.Psa.NinjaOne.NinjaOneConnector.RedirectUri(httpContext.Request), httpContext.RequestAborted);
    return Results.Redirect($"/psa/settings?psa=ninjaone&ninjaone={DotMarc.Psa.NinjaOne.NinjaOneConnector.OutcomeKey(outcome)}");
}).RequireAuthorization(nameof(Permission.AlertsManage));
```

If `DnsPushStartGuard` or `Permission` need a namespace prefix where these endpoints sit, use the one the DNS push endpoints and `RequireAuthorization(nameof(Permission.DomainsAdd))` already use.

- [ ] **Step 4: Run the tests to check they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NinjaOneStateProtectorTests|FullyQualifiedName~NinjaOneConnectorTests|FullyQualifiedName~ProgramDiValidationTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Psa/NinjaOne src/DotMarc/Program.cs test/DotMarc.Tests/Psa/NinjaOne
git commit -m "Connect NinjaOne through its sign-in, bound to the admin who started it"
```

---

### Task 8: The NinjaOne settings tab

**Files:**
- Create: `src/DotMarc/Components/Psa/NinjaOneSettingsTab.razor`
- Modify: `src/DotMarc/Components/Pages/PsaSettings.razor`, `src/DotMarc/Components/Psa/PsaIntegrationTestPanel.razor:81-87`, `src/DotMarc/Psa/PsaOptionNameCache.cs`
- Test: modify `test/DotMarc.Tests/Psa/PsaOptionNameCacheTests.cs`

**Interfaces:**
- Consumes:
  - `INinjaOneClient.ListTicketFormsAsync` and `ListStatusesAsync` (Task 4).
  - `NinjaOneSettingsService` (Task 2).
  - `NinjaOneOptions` (Task 2).
  - `NinjaOneTokenCache.ClearAsync` (Task 3).
  - `NinjaOneConnector.OutcomeKey` values (Task 7).
- Produces: `PsaOptionNameCache.RememberNinjaOneAsync(DotMarcDbContext context, IEnumerable<PsaOption> forms, IEnumerable<NinjaOneStatus> statuses, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

Add to `PsaOptionNameCacheTests.cs`, following that file's Postgres setup:

```csharp
    [Fact]
    public async Task NinjaOne_RemembersTheSavedFormAndStatusNames()
    {
        await using (var setup = CreateContext())
        {
            var settings = await setup.NinjaOneSettings.SingleAsync();
            (settings.TicketFormId, settings.TicketFormName, settings.NewStatusId, settings.NewStatusName) = (2, null, 2000, null);
            await setup.SaveChangesAsync();
        }

        await using var context = CreateContext();
        await PsaOptionNameCache.RememberNinjaOneAsync(context, [new PsaOption(1, "Default"), new PsaOption(2, "Email security")],
            [new DotMarc.Psa.NinjaOne.NinjaOneStatus(1000, "NEW", "New", null), new DotMarc.Psa.NinjaOne.NinjaOneStatus(2000, "OPEN", "Open", null)]);

        await using var verify = CreateContext();
        var saved = await verify.NinjaOneSettings.SingleAsync();
        Assert.Equal(("Email security", "Open"), (saved.TicketFormName, saved.NewStatusName));
    }
```

- [ ] **Step 2: Run the test to check it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaOptionNameCacheTests"`
Expected: a build failure, because `RememberNinjaOneAsync` doesn't exist.

- [ ] **Step 3: Implement**

In `PsaOptionNameCache.cs`, after `RememberConnectWiseAsync`:

```csharp
    /// <summary>A status shows by its display name, falling back to its name.</summary>
    public static async Task RememberNinjaOneAsync(DotMarcDbContext context, IEnumerable<PsaOption> forms, IEnumerable<DotMarc.Psa.NinjaOne.NinjaOneStatus> statuses, CancellationToken cancellationToken = default)
    {
        var saved = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        saved.TicketFormName = NameOf(forms, saved.TicketFormId) ?? saved.TicketFormName;
        saved.NewStatusName = NameOf(statuses.Select(status => new PsaOption(status.StatusId, status.DisplayName ?? status.Name)), saved.NewStatusId) ?? saved.NewStatusName;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

In `PsaIntegrationTestPanel.razor`'s `AuditAction`, add after the Halo arm:

```csharp
        PsaKind.NinjaOne => AuditActions.NinjaOneIntegrationTested,
```

Create `src/DotMarc/Components/Psa/NinjaOneSettingsTab.razor`:

```razor
@using DotMarc.Data
@using DotMarc.Components.Shared
@using DotMarc.Notifications
@using DotMarc.Psa
@using DotMarc.Psa.NinjaOne
@using Microsoft.EntityFrameworkCore
@inject AuditActorAccessor AuditActorAccessor
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject ISnackbar Snackbar
@inject ILogger<NinjaOneSettingsTab> Logger
@inject INinjaOneClient NinjaOneClient
@inject NinjaOneTokenCache TokenCache
@inject ISecretStore SecretStoreAccessor
@inject NavigationManager Navigation
@inject IDialogService DialogService

@if (_settings is null)
{
    <MudProgressCircular Indeterminate="true" />
}
else
{
    <MudPaper Class="pa-4">
        <div class="d-flex align-center justify-space-between flex-wrap mb-2" style="gap: 0.5rem;">
            <MudText Typo="Typo.h5">NinjaOne</MudText>
            <FieldWithHelp Topic="NinjaOne tickets" Inline="true" Help="Opens a NinjaOne ticket for each alert (as set under 'Which alerts create tickets' on Alert settings) and resolves it when the alert resolves. dotMARC checks open tickets every few minutes, so a ticket resolved or closed in NinjaOne resolves its alert.">
<MudSwitch @bind-Value="_settings.Enabled" Color="Color.Primary" Label="Enable NinjaOne tickets" />
</FieldWithHelp>
        </div>

        <MudText Typo="Typo.subtitle1" Class="mt-2 mb-2">Connection</MudText>
        <MudGrid>
            <MudItem xs="12" md="6">
                <FieldWithHelp Topic="Instance" Help="The NinjaOne region you sign in to, shown in your browser's address bar when you use NinjaOne.">
<MudSelect T="string" Label="Instance" @bind-Value="_settings.Instance" Variant="Variant.Outlined">
                    @foreach (var instance in NinjaOneOptions.Instances)
                    {
                        <MudSelectItem T="string" Value="@instance.Host">@instance.Label</MudSelectItem>
                    }
                </MudSelect>
</FieldWithHelp>
            </MudItem>
            <MudItem xs="12" md="6">
                <FieldWithHelp Topic="Client ID" Help="From the client app you added for dotMARC under Administration, Apps, API, Client app IDs in NinjaOne.">
<MudTextField Label="Client ID" @bind-Value="_settings.ClientId" Variant="Variant.Outlined" />
</FieldWithHelp>
            </MudItem>
            <MudItem xs="12" md="6">
                <FieldWithHelp Topic="Client secret" Help="Shown once, when the client app is created. dotMARC keeps it in its secret store and never shows it again, so leave this blank to keep the saved one.">
<MudTextField Label="Client secret" @bind-Value="_newClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
              HelperText="@(_settings.ClientSecretConfigured ? "A client secret is already saved - leave blank to keep it." : "No client secret saved yet.")" />
</FieldWithHelp>
            </MudItem>
            <MudItem xs="12" md="6">
                <FieldWithHelp Topic="Redirect URI" Help="Add this as a redirect URI on the client app in NinjaOne. NinjaOne sends you back here after you sign in.">
<MudTextField Label="Redirect URI" Value="@RedirectUri" ReadOnly="true" Variant="Variant.Outlined" />
</FieldWithHelp>
            </MudItem>
        </MudGrid>

        <MudAlert Severity="@ConnectionSeverity" Dense="true" Class="mt-4">@ConnectionText</MudAlert>
        <MudText Typo="Typo.caption" Class="mud-text-secondary d-block mt-1">
            NinjaOne's sign-in lasts an hour unless it's used, so dotMARC uses it every 20 minutes. If dotMARC is stopped
            for longer than that, reconnect here. <MudLink Href="https://dotmarc.app/docs/psa/ninjaone#keeping-the-connection-alive" Target="_blank">Longer-lived sign-ins</MudLink>
        </MudText>
        <div class="d-flex flex-wrap mt-2" style="gap: 0.5rem;">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Link" Disabled="@(!CanConnect)" OnClick="Connect">
                @(_settings.RefreshTokenConfigured ? "Reconnect" : "Connect to NinjaOne")
            </MudButton>
            @if (_settings.RefreshTokenConfigured)
            {
                <MudButton Variant="Variant.Outlined" Color="Color.Error" StartIcon="@Icons.Material.Filled.LinkOff" OnClick="DisconnectAsync">Disconnect</MudButton>
            }
        </div>
        @if (!CanConnect)
        {
            <MudText Typo="Typo.caption" Class="mud-text-secondary d-block mt-1">Choose the instance, enter the client ID and secret, and save before connecting.</MudText>
        }

        <div class="d-flex align-center justify-space-between flex-wrap mt-6 mb-1" style="gap: 0.5rem;">
            <MudText Typo="Typo.subtitle1">New tickets</MudText>
            <MudButton Variant="Variant.Outlined" StartIcon="@Icons.Material.Filled.CloudDownload" Disabled="@(_loading || !_settings.IsConnected)" OnClick="LoadOptionsAsync">Load options from NinjaOne</MudButton>
        </div>
        <MudText Typo="Typo.caption" Class="mud-text-secondary d-block mb-2">Loads the ticket forms and statuses to choose from. Connect first.</MudText>
        <MudGrid>
            <MudItem xs="12" md="4">
                <FieldWithHelp Topic="Ticket form" Help="The ticket form alert tickets use.">
<MudSelect T="int?" Label="Ticket form" @bind-Value="_settings.TicketFormId" Variant="Variant.Outlined">
                    @foreach (var form in WithSaved(_forms, _settings.TicketFormId, _settings.TicketFormName))
                    {
                        <MudSelectItem T="int?" Value="@((int?)form.Id)">@form.Name</MudSelectItem>
                    }
                </MudSelect>
</FieldWithHelp>
            </MudItem>
            <MudItem xs="12" md="4">
                <FieldWithHelp Topic="New ticket status" Help="The status alert tickets start in. When the alert resolves, dotMARC sets the ticket to Resolved, and NinjaOne closes it.">
<MudSelect T="int?" Label="New ticket status" @bind-Value="_settings.NewStatusId" Variant="Variant.Outlined">
                    @foreach (var status in WithSaved(_statuses, _settings.NewStatusId, _settings.NewStatusName))
                    {
                        <MudSelectItem T="int?" Value="@((int?)status.Id)">@status.Name</MudSelectItem>
                    }
                </MudSelect>
</FieldWithHelp>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="string" Label="Type" @bind-Value="_settings.TicketType" Variant="Variant.Outlined">
                    @foreach (var type in NinjaOneOptions.TicketTypes)
                    {
                        <MudSelectItem T="string" Value="@type">@Title(type)</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="string" Label="Priority" @bind-Value="_settings.Priority" Variant="Variant.Outlined">
                    @foreach (var priority in NinjaOneOptions.Priorities)
                    {
                        <MudSelectItem T="string" Value="@priority">@Title(priority)</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="string" Label="Severity" @bind-Value="_settings.Severity" Variant="Variant.Outlined">
                    @foreach (var severity in NinjaOneOptions.Severities)
                    {
                        <MudSelectItem T="string" Value="@severity">@Title(severity)</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
        </MudGrid>

        <div class="d-flex justify-end mt-4">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Save" OnClick="SaveAsync">Save NinjaOne settings</MudButton>
        </div>
    </MudPaper>

    <PsaIntegrationTestPanel Psa="PsaKind.NinjaOne" />
}

@code {
    /// <summary>Raised with whether NinjaOne is switched on in the saved settings, after loading and after saving.</summary>
    [Parameter] public EventCallback<bool> EnabledChanged { get; set; }

    /// <summary>How the last sign-in went, from the ?ninjaone= query the callback redirects back with.</summary>
    [Parameter] public string? ConnectResult { get; set; }

    private NinjaOneSettings? _settings;
    private (string? Instance, string? ClientId, bool SecretSaved) _savedSignIn;
    private string? _newClientSecret;
    private bool _loading;
    private List<PsaOption> _forms = [];
    private List<PsaOption> _statuses = [];

    private string RedirectUri => Navigation.BaseUri.TrimEnd('/') + NinjaOneConnector.CallbackPath;

    /// <summary>Connecting signs in with the saved instance, client ID and secret, so unsaved edits to them block it.</summary>
    private bool CanConnect => _settings is not null
        && _savedSignIn.Instance is not null && _savedSignIn.ClientId is not null && _savedSignIn.SecretSaved
        && _settings.Instance == _savedSignIn.Instance && _settings.ClientId == _savedSignIn.ClientId && string.IsNullOrWhiteSpace(_newClientSecret);

    private Severity ConnectionSeverity => _settings switch
    {
        { ConnectionLostUtc: not null } => Severity.Error,
        { RefreshTokenConfigured: true } => Severity.Success,
        _ => Severity.Info
    };

    private string ConnectionText => _settings switch
    {
        { ConnectionLostUtc: { } lostUtc } => $"Connection lost on {lostUtc.ToLocalTime():d MMM yyyy HH:mm} ({_settings.ConnectionLostReason}). Reconnect to raise tickets again.",
        { RefreshTokenConfigured: true } => $"Connected by {_settings.ConnectedBy} on {_settings.ConnectedUtc?.ToLocalTime():d MMM yyyy HH:mm}. Last refreshed {_settings.LastRefreshUtc?.ToLocalTime():d MMM yyyy HH:mm}.",
        _ => "Not connected."
    };

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync();
        ShowConnectResult();
    }

    private async Task ReloadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _settings = await NinjaOneSettingsService.GetAsync(db);
        _savedSignIn = (_settings.Instance, _settings.ClientId, _settings.ClientSecretConfigured);
        await EnabledChanged.InvokeAsync(_settings.Enabled);
    }

    private void ShowConnectResult()
    {
        var (message, severity) = ConnectResult switch
        {
            "connected" => ("Connected to NinjaOne.", Severity.Success),
            "cancelled" => ("The NinjaOne sign-in was cancelled.", Severity.Warning),
            "expired" => ("That sign-in expired or was started by someone else. Try connecting again.", Severity.Error),
            "not-configured" => ("Save the instance, client ID and client secret before connecting.", Severity.Error),
            "refused" => ("NinjaOne refused the sign-in. Check the client ID, secret and redirect URI match the client app.", Severity.Error),
            "no-refresh-token" => ("NinjaOne didn't give dotMARC a refresh token. Tick the Refresh token grant type on the client app, then reconnect.", Severity.Error),
            "failed" => ("dotMARC couldn't reach NinjaOne to finish signing in. Try again.", Severity.Error),
            _ => ((string?)null, Severity.Normal)
        };
        if (message is not null)
        {
            Snackbar.Add(message, severity, config => config.RequireInteraction = severity == Severity.Error);
        }
    }

    // A full page load, so the browser follows the redirect to NinjaOne instead of Blazor trying to route it.
    private void Connect() => Navigation.NavigateTo("/psa/ninjaone/connect", forceLoad: true);

    private async Task DisconnectAsync()
    {
        var confirmed = await DialogService.ShowMessageBoxAsync("Disconnect NinjaOne",
            "dotMARC will stop raising and resolving NinjaOne tickets until someone connects again.", yesText: "Disconnect", cancelText: "Cancel");
        if (confirmed != true)
        {
            return;
        }

        await using var db = await DbFactory.CreateDbContextAsync();
        await NinjaOneSettingsService.DisconnectAsync(db, await AuditActorAccessor.GetAsync(), SecretStoreAccessor);
        await TokenCache.ClearAsync();
        Snackbar.Add("Disconnected NinjaOne.", Severity.Success);
        await ReloadAsync();
    }

    private static IEnumerable<PsaOption> WithSaved(IReadOnlyList<PsaOption> options, int? savedId, string? savedName) =>
        savedId is { } id && options.All(option => option.Id != id)
            ? options.Prepend(new PsaOption(id, savedName ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            : options;

    private static string Title(string value) => value[..1] + value[1..].ToLowerInvariant();

    private async Task LoadOptionsAsync()
    {
        _loading = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var saved = await NinjaOneSettingsService.GetAsync(db);
            _forms = (await NinjaOneClient.ListTicketFormsAsync(saved)).ToList();
            var statuses = await NinjaOneClient.ListStatusesAsync(saved);
            _statuses = statuses.Select(status => new PsaOption(status.StatusId, status.DisplayName ?? status.Name)).ToList();
            try
            {
                await PsaOptionNameCache.RememberNinjaOneAsync(db, _forms, statuses);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Logger.LogWarning(exception, "Couldn't store the NinjaOne option names");
            }

            Snackbar.Add("Loaded ticket forms and statuses from NinjaOne.", Severity.Success);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or NinjaOneConnectionLostException or System.Text.Json.JsonException)
        {
            Logger.LogWarning(exception, "Loading NinjaOne ticket options failed");
            ShowError($"Couldn't load options from NinjaOne. {exception.Message}");
            await ReloadAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            _settings!.TicketFormName = NameOf(_forms, _settings.TicketFormId) ?? _settings.TicketFormName;
            _settings.NewStatusName = NameOf(_statuses, _settings.NewStatusId) ?? _settings.NewStatusName;

            await using var db = await DbFactory.CreateDbContextAsync();
            await NinjaOneSettingsService.SaveAsync(db, await AuditActorAccessor.GetAsync(), SecretStoreAccessor, _settings, string.IsNullOrWhiteSpace(_newClientSecret) ? null : _newClientSecret);
            _newClientSecret = null;
            await TokenCache.ClearAsync();
            Snackbar.Add("NinjaOne settings saved.", Severity.Success);
            await ReloadAsync();
        }
        catch (ArgumentException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Saving NinjaOne settings failed");
            Snackbar.Add("Failed to save NinjaOne settings. Try again.", Severity.Error);
        }
    }

    private static string? NameOf(IReadOnlyList<PsaOption> options, int? id) => options.FirstOrDefault(option => option.Id == id)?.Name;

    // Stays on screen until dismissed: these messages can be long.
    private void ShowError(string message) => Snackbar.Add(message, Severity.Error, config => config.RequireInteraction = true);
}
```

`PsaSettings.razor`:
- Add the NinjaOne tab second.
- Open it when the URL says `?psa=ninjaone`.
- Pass the connect result through.

Replace the `<MudTabs>` block and `@code`:

```razor
<MudTabs @bind-ActivePanelIndex="_activeTab">
    <MudTabPanel Text="HaloPSA" Icon="@TabIcon(_haloEnabled)">
        <div class="pt-4"><HaloPsaSettingsTab EnabledChanged="@(enabled => _haloEnabled = enabled)" /></div>
    </MudTabPanel>
    <MudTabPanel Text="NinjaOne" Icon="@TabIcon(_ninjaOneEnabled)">
        <div class="pt-4"><NinjaOneSettingsTab EnabledChanged="@(enabled => _ninjaOneEnabled = enabled)" ConnectResult="@NinjaOneResult" /></div>
    </MudTabPanel>
    <MudTabPanel Text="ConnectWise" Icon="@TabIcon(_connectWiseEnabled)">
        <div class="pt-4"><ConnectWiseSettingsTab EnabledChanged="@(enabled => _connectWiseEnabled = enabled)" /></div>
    </MudTabPanel>
    <MudTabPanel Text="Autotask" Icon="@TabIcon(_autotaskEnabled)">
        <div class="pt-4"><AutotaskSettingsTab EnabledChanged="@(enabled => _autotaskEnabled = enabled)" /></div>
    </MudTabPanel>
</MudTabs>

@code {
    /// <summary>Which PSA's tab to open, such as "ninjaone" after its sign-in.</summary>
    [SupplyParameterFromQuery(Name = "psa")] public string? Psa { get; set; }

    [SupplyParameterFromQuery(Name = "ninjaone")] public string? NinjaOneResult { get; set; }

    private int _activeTab;
    private bool _haloEnabled;
    private bool _ninjaOneEnabled;
    private bool _connectWiseEnabled;
    private bool _autotaskEnabled;

    protected override void OnInitialized()
    {
        // The tabs are in PsaKind order.
        if (Enum.GetValues<PsaKind>().ToList().FindIndex(psa => string.Equals(psa.ToString(), Psa, StringComparison.OrdinalIgnoreCase)) is var index and >= 0)
        {
            _activeTab = index;
        }
    }

    /// <summary>A tick on the tab of each PSA that is switched on.</summary>
    private static string? TabIcon(bool enabled) => enabled ? Icons.Material.Filled.CheckCircle : null;
}
```

- [ ] **Step 4: Run the tests and build**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaOptionNameCacheTests|FullyQualifiedName~StaticPagesTests|FullyQualifiedName~PsaIntegrationTestServiceTests"`
Expected: all pass, and the Razor compiles with no new warnings.

- [ ] **Step 5: Check it in a browser**

Run the app locally the way the other PSA tabs were checked. Use playwright-edge only, and never install Chrome. Open `/psa/settings?psa=ninjaone` and check these:
- The tabs read HaloPSA, NinjaOne, ConnectWise, Autotask, and the NinjaOne tab is open.
- The redirect URI ends in `/psa/ninjaone/callback`.
- Connect is disabled until the instance, client ID and secret are saved.
- `?ninjaone=no-refresh-token` shows its message.

Stop only the process you started, by its PID.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Components src/DotMarc/Psa/PsaOptionNameCache.cs test/DotMarc.Tests/Psa/PsaOptionNameCacheTests.cs
git commit -m "Add the NinjaOne settings tab, second among the PSAs"
```

---

### Task 9: Docs

**Files:**
- Create: `website/docs/psa/ninjaone.mdx`
- Modify:
  - `website/docs/psa/connectwise.mdx` (`sidebar_position: 3`), `website/docs/psa/autotask.mdx` (`sidebar_position: 4`)
  - `website/docs/psa-integration.mdx`, `website/docs/psa/halopsa.mdx`, `website/docs/alerts.mdx`
  - The "alongside" sentences in `website/docs/psa/connectwise.mdx` and `autotask.mdx`

- [ ] **Step 1: Write the NinjaOne page**

`website/docs/psa/ninjaone.mdx`:

```mdx
---
title: NinjaOne
sidebar_position: 2
description: Raise and resolve NinjaOne tickets from dotMARC alerts, and resolve alerts when a tech resolves the ticket.
---

# NinjaOne

dotMARC can open a NinjaOne ticket when an alert fires, resolve it when the alert resolves, and resolve the alert when a
tech resolves or closes the ticket in NinjaOne. It can do this alongside HaloPSA, ConnectWise and Autotask: see
[PSA integration](../psa-integration.mdx) for how several PSAs work together.

NinjaOne only lets a signed-in person raise tickets, so dotMARC connects as a NinjaOne user, not as an app on its own.
Tickets show that user as the requester. A NinjaOne technician account kept for dotMARC is the tidiest choice.

## 1. Add a client app

In NinjaOne, go to **Administration > Apps > API > Client app IDs** and select **Add client app**:

- **Application platform:** Web (PHP, Java, .Net Core, etc.)
- **Name:** dotMARC
- **Redirect URIs:** your dotMARC address followed by `/psa/ninjaone/callback`, such as
  `https://dotmarc.example.com/psa/ninjaone/callback`. The NinjaOne tab in dotMARC shows the exact address.
- **Scopes:** Monitoring and Management. dotMARC doesn't need Control.
- **Allowed grant types:** Authorization code and Refresh token. Leave Client credentials unticked: NinjaOne refuses
  to raise tickets for an app signed in without a user.

Save it, then copy the client ID and the client secret. NinjaOne shows the secret once.

## 2. Connect dotMARC

1. In dotMARC, go to **Settings > PSA** and open the **NinjaOne** tab.
2. Choose your **Instance**, the region in your NinjaOne address (eu, app for the US, us2, ca, oc or fed).
3. Enter the **Client ID** and **Client secret**, and select **Save NinjaOne settings**.
4. Select **Connect to NinjaOne** and sign in as the user dotMARC should raise tickets as. NinjaOne sends you back to
   dotMARC, which shows who connected and when.

dotMARC asks for the `monitoring`, `management` and `offline_access` scopes. `offline_access` is what lets dotMARC keep
working after you close the page.

Changing the instance, client ID or client secret disconnects dotMARC, because the saved sign-in belongs to the old
app. Connect again afterwards.

## 3. Choose how tickets are raised

Select **Load options from NinjaOne**, then choose:

- **Ticket form:** the form alert tickets use.
- **New ticket status:** the status alert tickets start in, New by default.
- **Type, Priority and Severity:** what alert tickets are created with.

Save, then use **Test the integration** at the bottom of the tab to raise and resolve a test ticket.

## Keeping the connection alive

NinjaOne's refresh tokens last an hour unless they're used. dotMARC uses its token when it starts and every 20 minutes
after, even when no alerts are firing, so the connection stays alive while dotMARC is running.

If dotMARC is stopped for more than an hour, for a long upgrade or an outage, NinjaOne forgets the sign-in. dotMARC then
raises a **PSA connection lost** alert and stops raising NinjaOne tickets until someone selects **Reconnect** on the
NinjaOne tab.

To survive longer gaps, ask NinjaOne for sliding refresh tokens, which last longer and renew as they're used. Email
NinjaOne support at [support@ninjaone.com](mailto:support@ninjaone.com), or
[support.eu@ninjaone.com](mailto:support.eu@ninjaone.com) for EU instances, and give them your client app's client ID.

## How tickets are resolved

When an alert resolves, dotMARC adds a private comment saying why and sets the ticket to **Resolved**. NinjaOne doesn't
let other apps close a ticket directly, so its own rules close resolved tickets in time, the same as when a tech resolves
one.

dotMARC checks open tickets every few minutes. A ticket a tech resolves or closes, or moves to a custom status under
Resolved or Closed, resolves its alert.

## Troubleshooting

- **PSA connection lost:** NinjaOne refused dotMARC's saved sign-in, usually because dotMARC was stopped for over an
  hour, or the client app was changed or deleted. Reconnect on the NinjaOne tab. The alert resolves once you do.
- **"NinjaOne didn't give dotMARC a refresh token":** the client app doesn't allow the Refresh token grant type. Tick
  it, save the app, and connect again.
- **"NinjaOne refused the sign-in":** the client ID, client secret or redirect URI doesn't match the client app.
- **`user_context_required`:** NinjaOne got a request from an app signed in without a user. Make sure the client app
  allows Authorization code, and connect with **Connect to NinjaOne**.
```

- [ ] **Step 2: Update the other docs**

- `connectwise.mdx`: set `sidebar_position: 3`. Change "alongside HaloPSA and Autotask" to "alongside HaloPSA, NinjaOne and Autotask".
- `autotask.mdx`: set `sidebar_position: 4`. Change its "alongside" sentence to name HaloPSA, NinjaOne and ConnectWise.
- `halopsa.mdx`, line 10: change it to "alongside NinjaOne, ConnectWise and Autotask".
- `psa-integration.mdx`:
  - The description becomes "...in HaloPSA, NinjaOne, ConnectWise and Autotask...".
  - Add `- [NinjaOne](./psa/ninjaone.mdx)` second in the PSA list.
  - The company sentence becomes "a client in HaloPSA, an organisation in NinjaOne, a company in ConnectWise or Autotask".
- `alerts.mdx`, line 101: change it to "**Closing its PSA ticket** in HaloPSA, NinjaOne, ConnectWise or Autotask."
- `alerts.mdx`: if it has a table or list of alert types, add **PSA connection lost**: "dotMARC can no longer sign in to a PSA, so it isn't raising tickets there until someone reconnects. Doesn't raise tickets by default."

- [ ] **Step 3: Build the docs**

```bash
cd website && npx docusaurus clean-api-docs all && npx docusaurus gen-api-docs all && npx docusaurus build --out-dir "$TMP/dotmarc-docs-build"
```

Expected: the build succeeds with no broken links. If `yarn install` is needed first, run it through WSL.

- [ ] **Step 4: Check for em dashes**

Run: `grep -rn "—" website/docs/psa/ninjaone.mdx src/DotMarc/Psa/NinjaOne src/DotMarc/Components/Psa/NinjaOneSettingsTab.razor`
Expected: no output.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test test/DotMarc.Tests > .superpowers/sdd/ninjaone/suite-final.txt 2>&1; tail -5 .superpowers/sdd/ninjaone/suite-final.txt`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add website/docs
git commit -m "Document the NinjaOne integration and keeping its connection alive"
```
