# NinjaOne PSA integration

## Goal

Add NinjaOne ticketing as a fourth PSA, alongside HaloPSA, ConnectWise and Autotask. It works the way the others do:

- dotMARC raises a ticket when an alert fires.
- It resolves the ticket when the alert resolves.
- It resolves the alert when a technician resolves or closes the ticket.

Everywhere PSAs are listed, they appear in this order: HaloPSA, NinjaOne, ConnectWise, Autotask.

## What we learned from NinjaOne's API

We checked these against a live EU instance with test ticket 1004, which is now resolved:

**Signing in**
- **Ticketing needs a user.** An app that signs in as itself, with no user, gets 403 `user_context_required` when it creates a ticket. So dotMARC uses the authorization code grant with the `offline_access` scope, then the refresh token grant.
- **Refresh tokens last an hour.** They are valid for one hour, and refreshing returns the same refresh token. NinjaOne support can turn on longer-lived "sliding" refresh tokens for a client ID (support@ninjaone.com, or support.eu@ninjaone.com for EU instances). Access tokens last 3600 seconds.
- **The token endpoint** is `POST https://<instance>/ws/oauth/token`, and the authorize endpoint is `https://<instance>/ws/oauth/authorize`.

**Tickets**
- **Creating a ticket** is `POST /v2/ticketing/ticket` with `clientId`, `ticketFormId`, `subject`, `description {body, public}`, `status` (the status id as a string, such as `"1000"`), `type`, `priority` and `severity`. The response includes the ticket's `id`. NinjaOne sets the requester to the connected user.
- **Statuses** come from `GET /v2/ticketing/statuses` as `{name, displayName, parentId, statusId}`. The defaults are NEW 1000, OPEN 2000, WAITING 3000, PAUSED 4000, RESOLVED 5000, CLOSED 6000, APPROVED 7000 and REJECTED 8000. A custom status points at its built-in status through `parentId`.
- **Updating a ticket** is `PUT /v2/ticketing/ticket/{id}`. It needs the full set of fields: `clientId`, `ticketFormId`, `subject`, `type`, `priority`, `severity`, `requesterUid`, `status` and the current `version`. A shorter body fails with 400 or 404 (`ticket_form_not_found`). Each update raises `version`, and a resolve raised it by 2.
- **An update can't set CLOSED.** That fails with 400 `cannot_close_tickets_directly`. RESOLVED (5000) works, and NinjaOne's own rules close resolved tickets later.
- **Comments** are `POST /v2/ticketing/ticket/{id}/comment` as multipart form data, with a JSON part named `comment` containing `{body, public}`. The response is 204, and the version doesn't change.
- **Reading a ticket** is `GET /v2/ticketing/ticket/{id}`.

**Other endpoints**
- Ticket forms: `GET /v2/ticketing/ticket-form`.
- Organisations: `GET /v2/organizations`.
- A ticket in the web app: `https://<instance>/#/ticketing/ticket/{id}`. This link is not yet confirmed by clicking through.

## Setting it up in NinjaOne

The docs walk through **Administration > Apps > API > Client app IDs > Add client app**:

- **Application platform:** Web.
- **Name:** dotMARC.
- **Redirect URI:** `https://<your dotMARC>/psa/ninjaone/callback`. The settings tab shows this URL with a copy button.
- **Scopes:** Monitoring and Management. Control isn't needed.
- **Allowed grant types:** Authorization code and Refresh token. Client credentials isn't used.

The docs also say:
- Why the integration signs in as a person, and that tickets show that person as the requester. A dedicated NinjaOne technician account for dotMARC is the tidiest choice.
- That refresh tokens last an hour by default, so dotMARC has to stay running to keep the connection alive. To survive longer outages, email NinjaOne support with the client ID and ask for sliding refresh tokens.

## Settings

There's a `NinjaOneSettings` singleton row, seeded by its migration like the other PSAs.

| Field | Notes |
|---|---|
| `Enabled` | |
| `Instance` | One of `eu`, `oc`, `app` (US), `us2`, `ca`, `fed`. Stored as the host (`eu.ninjarmm.com`). |
| `ClientId` | |
| `ClientSecretConfigured` | The secret itself goes in `ISecretStore` under `NinjaOne.ClientSecret`. |
| `RefreshTokenConfigured` | The token itself goes in `ISecretStore` under `NinjaOne.RefreshToken`. |
| `ConnectedBy`, `ConnectedUtc` | The dotMARC user who connected, and when. |
| `LastRefreshUtc` | When the keep-alive last succeeded. |
| `ConnectionLostUtc`, `ConnectionLostReason` | Set when a refresh is rejected. Cleared when someone reconnects. |
| `TicketFormId`, `TicketFormName` | Required. |
| `NewStatusId`, `NewStatusName` | Required. Defaults to NEW. Picked from the statuses list, so custom statuses work. |
| `TicketType` | `PROBLEM`, `QUESTION`, `INCIDENT` or `TASK`. Defaults to `PROBLEM`. |
| `Priority` | `NONE`, `LOW`, `MEDIUM`, `HIGH` or `URGENT`. Defaults to `NONE`. |
| `Severity` | `NONE`, `MINOR`, `MODERATE`, `MAJOR` or `CRITICAL`. Defaults to `NONE`. |

The ticket form and status names are saved alongside their ids for display only, the same as ConnectWise.

Saving goes through `NinjaOneSettingsService.SaveAsync`, which writes a field-level audit entry like ConnectWise's. If the instance, client ID or client secret changes, the saved refresh token no longer matches. So saving deletes the token, clears the connection fields, and the audit entry says the connection was reset.

A PSA counts as ready when all of these hold:
- It's enabled.
- The instance, client ID and secret are set.
- It's connected, meaning `RefreshTokenConfigured` is true and `ConnectionLostUtc` is null.
- A ticket form and a new ticket status are chosen.

If any is missing, readiness lists it, using "connection" for the connection.

## Connecting

There are two endpoints, next to the DNS push ones in `Program.cs`. Both require the admin policy that the PSA settings page uses.

**`GET /psa/ninjaone/connect`**
- It runs only for requests from dotMARC's own pages, using the same check as `DnsPushStartGuard`.
- It builds a state value with ASP.NET Data Protection, holding the user's id and a timestamp. The state lasts 10 minutes.
- It redirects to `https://<instance>/ws/oauth/authorize?response_type=code&client_id=…&redirect_uri=…&scope=monitoring%20management%20offline_access&state=…`.

**`GET /psa/ninjaone/callback`**
- It unprotects the state and checks that it's the same user and still within 10 minutes.
- It exchanges the code with the `authorization_code` grant, then saves the refresh token to the secret store.
- It sets `RefreshTokenConfigured`, `ConnectedBy` and `ConnectedUtc`, and clears `ConnectionLostUtc`.
- It writes an audit entry, "Connected NinjaOne", and resolves any open lost-connection alert.
- It redirects back to the NinjaOne settings tab with `?ninjaone=connected`, or with `?ninjaone=<error>` when the user cancelled, the state is invalid or the exchange failed. The tab shows a matching message.
- The exchange's error body (`error` and `error_description`) is logged. The client secret and tokens are never logged.

The redirect URI is built from the request scheme and host, the same as DNS push.

**Disconnect** deletes the refresh token from the secret store, clears the connection fields, drops the cached access token, and writes an audit entry.

## Tokens and keep-alive

`NinjaOneTokenCache` is a singleton, shaped like `HaloPsaTokenCache`:
- It holds one access token in memory, keyed by instance and client ID, and treats it as expiring 60 seconds early.
- `GetTokenAsync` returns the cached access token, or refreshes using the stored refresh token.
- A semaphore makes sure only one refresh runs at a time.
- If a refresh response includes a different `refresh_token`, it saves the new one to the secret store. That covers sliding tokens if NinjaOne rotates them.
- Two results are distinct:
  - A rejection (400 with `invalid_grant` or `invalid_request`, or a 401) throws `NinjaOneConnectionLostException`.
  - A network error, timeout or 5xx throws an ordinary `HttpRequestException`.

`NinjaOneKeepAliveService` is a `BackgroundService`:
- It runs once at startup, then every 20 minutes. That leaves two tries inside each one-hour refresh token lifetime.
- Each run does nothing unless NinjaOne is enabled and connected.
- It forces a refresh, ignoring any cached access token, so the refresh token is actually used.

What a run does with the result:
- **Success:** sets `LastRefreshUtc`.
- **Connection lost:** sets `ConnectionLostUtc` and `ConnectionLostReason`, and calls `IAlertingService.RaisePsaConnectionLostAsync`. Readiness becomes not ready, so new alerts skip NinjaOne ticketing and don't fail. Ticket polling skips it too.
- **Transient failure:** logs a warning and tries again next run.

Any provider call that hits a connection-lost rejection takes the same lost-connection path.

**The new alert type** is `PsaConnectionLost`, displayed as "PSA connection lost". It doesn't create tickets by default, since the PSA it would ticket into is the one that's down.
- Its subject is "NinjaOne connection", stored where a domain alert stores its domain name, the same as API key alerts.
- Its message: "dotMARC can no longer refresh its NinjaOne sign-in (<reason>). New alerts won't raise NinjaOne tickets until someone reconnects under Settings > PSA > NinjaOne."
- Reconnecting resolves it through `ResolvePsaConnectionLostAsync`.

## The client and provider

`INinjaOneClient` and `NinjaOneClient` use a typed `HttpClient`.
- **Pick-lists:**
  - `ListOrganizationsAsync`
  - `ListTicketFormsAsync`
  - `ListStatusesAsync`
- **Tickets:**
  - `CreateTicketAsync(settings, PsaTicketRequest)`, which returns the id as a string.
  - `GetTicketAsync(settings, id)`, which returns null on 404.
  - `AddCommentAsync`, which sends multipart.
  - `ResolveTicketAsync`.
- A 401 on an API call drops the cached token and retries once. A second 401 counts as a lost connection.

`ResolveTicketAsync` works like this:
1. GET the ticket. If it's already RESOLVED or CLOSED, stop.
2. PUT the full body: `clientId`, `ticketFormId`, `subject`, `type`, `priority`, `severity` and `requesterUid` copied from the ticket, plus `status: "5000"` and `version`.
3. On 409, or a 400 that mentions the version, GET the ticket again and retry once.

`NinjaOneProvider : IPsaProvider` maps the shared PSA calls onto that client:

- **`ListCompaniesAsync`:** organisations, with the id as text.
- **`CreateTicketAsync`:**
  - The subject is the request's `Title`.
  - The description is `Body`, with `public: false`.
  - It uses the saved form, the new ticket status id, and the type, priority and severity.
- **`GetTicketStateAsync`:**
  - Missing on 404, or when `deleted` is true.
  - Closed when the status, or its parent, is RESOLVED (5000) or CLOSED (6000).
  - Open otherwise.
- **`CloseTicketAsync(ticketId, note)`:** adds the note as a private comment, then resolves the ticket. If the comment fails, it still tries the resolve. The failure is logged, but the outcome follows the resolve.
- **`GetTicketUrlTemplateAsync`:** `https://<instance host>/#/ticketing/ticket/{0}`.

## Settings tab

`NinjaOneSettingsTab.razor` follows the same layout as `ConnectWiseSettingsTab`:

1. **Connection.**
   - The fields: Enabled, an Instance select, Client ID and Client secret.
   - The redirect URI to copy.
   - A connection status block:
     - Not connected.
     - Connected by X on date, last refreshed at time.
     - Connection lost on date (reason).
   - **Connect** or **Reconnect** opens `/psa/ninjaone/connect` in the same tab. **Disconnect** asks for confirmation first.
   - Connect is disabled until the settings are saved. If a field is changed but not saved, the tab says to save first.
2. **New tickets.** Pick-lists for the ticket form and the new ticket status, loaded live once connected. Selects for type, priority and severity.
3. **The shared test panel**, `PsaIntegrationTestPanel`, which already works through `IPsaProvider`.

A short note under the connection explains the one-hour refresh token lifetime, with a link to the docs section on sliding tokens.

## Ordering and the other places that list PSAs

- `PsaKind` becomes `{ HaloPsa, NinjaOne, ConnectWise, Autotask }`. It's stored as a string, so existing rows are unaffected.
- Display name "NinjaOne"; company label "NinjaOne organisation".
- `PsaSettings.razor` tabs: HaloPSA, NinjaOne, ConnectWise, Autotask.
- Anything that switches on `PsaKind` gets a NinjaOne case. That's:
  - `PsaImportColumns` and the domain import files (a "NinjaOne organisation" column)
  - `PsaCompanySuggestions`
  - `PsaIntegrationTestService` and `PsaIntegrationTestPanel`
  - `PsaOptionNameCache`
  - `DemoPsaProvider`
  - `AlertTicketPolicy`
  - the PSA company picker, Alerts, ManageDomains and ManageGroups
- `PsaDirectory` already sorts by `Kind`, so company lists follow the new order.
- The demo seeds no NinjaOne data. The demo provider covers NinjaOne the way it covers the other PSAs.

## Docs

- **New page:** `website/docs/psa/ninjaone.mdx`, at sidebar position 2. ConnectWise moves to 3 and Autotask to 4. It covers:
  - Creating the client app, as in the setup section above.
  - The instance.
  - Connecting.
  - Keeping the connection alive, including the one-hour lifetime and sliding tokens from support with the client ID.
  - New ticket settings.
  - How resolving works, including why dotMARC resolves rather than closes.
  - Troubleshooting: lost connections, and `user_context_required` if someone uses a client credentials app.
- **`psa-integration.mdx`:** add NinjaOne, in the new order, wherever the PSAs are listed.

## Testing

- **Unit tests** cover:
  - `NinjaOneClient`, against a fake `HttpMessageHandler`:
    - the create body (status as a string, `public: false`)
    - the multipart comment shape
    - a full-body resolve that copies `requesterUid` and the version
    - a version-conflict retry
    - a 404 returning null
    - a 401 dropping the token and retrying once
  - The token cache:
    - a rejection versus a transient error
    - saving a rotated refresh token
    - one refresh at a time
  - The provider's state mapping: RESOLVED, CLOSED, a custom status whose parent is RESOLVED, deleted, and 404.
  - Readiness: each missing field, and a lost connection.
- **Settings service tests:**
  - Changing the instance, client ID or secret clears the connection.
  - The audit entry is written.
- **Integration tests through `ApiTestHost`** cover the callback:
  - A bad state, the wrong user and a cancel each redirect with an error and save nothing.
  - A successful exchange stores the token, using a stub token endpoint, and resolves the lost-connection alert.
  - A keep-alive rejection raises `PsaConnectionLost` once and marks the connection lost.
- **A `PsaKind` order test** checks that the settings tabs and `PsaDirectory` list HaloPSA, NinjaOne, ConnectWise, Autotask.

## Out of scope

- NinjaOne webhooks to resolve alerts faster. Polling is enough to start with, and webhooks can follow later.
- Picking a custom "resolved" status. dotMARC always uses RESOLVED (5000), because NinjaOne won't let an update set CLOSED.
- Setting a ticket's location or device.
