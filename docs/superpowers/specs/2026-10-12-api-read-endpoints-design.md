# API read endpoints: sources, reports, TLS reports, summary, client reports, parse failures and alert tickets

**Release:** dotMARC 0.9.2
**Status:** approved in brainstorming, awaiting spec review

## Purpose

The second piece of the API work. The first added filter conventions, filters on the list endpoints and the audit log. This adds the data a script still can't reach: every sending source, the individual DMARC and TLS reports (with their original files), the dashboard's headline figures, client reports (schedule, history, PDF and sending), parse failures, and each alert's PSA tickets.

## Decisions made in brainstorming

- **Raw reports on request.** Lists and single reports return parsed JSON; `/raw` returns the original XML (DMARC) or JSON (TLS) file.
- **Client reports: read, download and send now.** Schedules stay managed in the app.
- **Sources use cached IP details only.** No live RDAP lookups through the API; organisation and country are null until dotMARC has looked the IP up.
- **Permissions reuse the app's.** Domains view for sources, reports, TLS reports and the summary; Reports manage for client reports; Domains edit for parse failures; Alerts view for tickets. Group-limited keys see only their own data.
- **Everything follows the first piece's conventions** (`ApiFilterReader`, `ApiFilterDocs`, typed OpenAPI filters, one 400 naming every problem, a domain the key can't see is a 404).

## Scope

**In:** the endpoints in sections 1 to 4; `tickets` on alerts and the `hasOpenTicket` filter; OpenAPI for every operation, including the `application/xml`, `application/json` and `application/pdf` responses; a docs section; tests.

**Out:** managing report schedules; live IP lookups; reprocessing or deleting parse failures; ticket actions (closing, reopening).

## 1. Sources and reports (Domains view)

### `GET /domains/{domain}/sources`

Every sending IP for the domain over a range, totalled in the database, busiest first (`volume` descending, then `ip`), paged (50 by default, at most 200).

| Field | Notes |
| --- | --- |
| `ip` | The source IP. |
| `volume`, `passed`, `failed` | Message counts; passed means SPF or DKIM passed (`DomainStatistics.IsPassing`). |
| `passRate` | `passed / volume`. |
| `spf`, `dkim` | `Pass` if it passed in any record in the range, else `Fail`. |
| `disposition` | The most severe seen: `Reject`, else `Quarantine`, else `None`. |
| `organization`, `country` | From `IpInfoService`'s cache (exact IP, then cached ranges); null when not yet looked up. |

| Filter | Values |
| --- | --- |
| `days`, `from`, `to` | As on the report summary: last 1 to 30 days (default 30), or a range of up to 366 days; not both. |
| `result` | `passing` (no failed messages) or `failing` (any failed messages). |
| `spf`, `dkim` | `Pass` or `Fail`, against the combined value. |
| `disposition` | One or more of `None`, `Quarantine`, `Reject`. |
| `minVolume` | A whole number of 1 or more. |
| `ip` | Part of the address. |
| `organization` | Part of the cached organisation name, case-insensitive; IPs with no cached organisation never match. |

`organization` is matched after the database totals, against the cached names; paging and `totalCount` reflect the filtered list.

### `GET /domains/{domain}/reports`

DMARC aggregate reports, newest received first, paged.

| Field | Notes |
| --- | --- |
| `id` | dotMARC's id for the report. |
| `reportingOrg`, `reportId` | As the receiver sent them. |
| `dateRangeBeginUtc`, `dateRangeEndUtc`, `receivedUtc` | |
| `messageCount`, `passRate` | Over the report's records; `passRate` null with no messages. |
| `records` | Each: `sourceIp`, `count`, `disposition`, `spf`, `dkim`, `headerFrom`, `overrideReasons` (`type`, `comment`), `authDetails` (`mechanism`, `domain`, `result`, `selector`, `scope`). |

| Filter | Values |
| --- | --- |
| `reportingOrg` | One or more; each matches part of the name, case-insensitive. |
| `receivedAfter`, `receivedBefore` | Dates. |
| `failingOnly` | `true`: reports with at least one failing record. |

`GET /domains/{domain}/reports/{id}` returns one report in the same shape; an id belonging to another domain, or none, is a 404. `GET /domains/{domain}/reports/{id}/raw` returns the stored XML as `application/xml`.

### `GET /domains/{domain}/tls-reports`

TLS-RPT reports, newest received first, paged.

| Field | Notes |
| --- | --- |
| `id`, `reportingOrg`, `reportId`, `dateRangeBeginUtc`, `dateRangeEndUtc`, `receivedUtc` | |
| `successfulSessions`, `failedSessions` | Totals over the report's policies. |
| `policies` | Each: `type`, `domain`, `successfulSessions`, `failedSessions`, `failures` (`resultType`, `sessions`, `receivingMx`, `reasonCode`, `additionalInformation`). |

| Filter | Values |
| --- | --- |
| `reportingOrg` | As above. |
| `receivedAfter`, `receivedBefore` | Dates. |
| `failuresOnly` | `true`: reports with any failed sessions. |
| `resultType` | One or more result types (free text, case-insensitive exact match): reports with a failure of that type. |

`GET .../tls-reports/{id}` returns one; `.../tls-reports/{id}/raw` returns the stored JSON as `application/json`.

### `GET /summary`

The dashboard's headline figures over the domains the key can see, from `DashboardSummary.Build` and `PortalStatus.For`.

| Field | Notes |
| --- | --- |
| `domainCount`, `overallPassRate`, `warningCount`, `missingCount`, `sourceCount` | As the dashboard shows them. |
| `reasonBreakdown` | As on the report summary. |
| `parseFailureCount` | Only for keys with Domains edit and not limited to groups; otherwise null. |
| `statusCounts` | `{ protected, monitoringOnly, needsAttention, noReportsYet }`. |

It takes no filters.

## 2. Client reports (Reports manage)

The Group in the route is an id. A key limited to groups reaches only its own; any other Group, or none, is a 404.

### `GET /groups/{group}/report-schedule`

`{ frequency, recipients, startedUtc, nextPeriod: { start, end, kind }, nextDueUtc }`. `frequency` is `Off`, `Weekly`, `Monthly` or `Quarterly`. With no schedule, frequency is `Off`, recipients empty and the next-run fields null. The next run comes from `ClientReportService.GetNextScheduledAsync` with the report settings' time zone and send hour.

### `GET /groups/{group}/report-deliveries`

Newest first, paged.

| Field | Notes |
| --- | --- |
| `id`, `periodStart`, `periodEnd`, `kind` (`Scheduled`, `Manual`), `status` (`Pending`, `Sent`, `Failed`, `Skipped`), `recipients`, `attempts`, `firstAttemptUtc`, `lastAttemptUtc`, `sentUtc`, `error`, `requestedBy` | As stored. |

| Filter | Values |
| --- | --- |
| `status` | One or more statuses. |
| `kind` | One or more kinds. |
| `sentAfter`, `sentBefore` | Dates, against `SentUtc`; unsent deliveries never match. |

### `GET /groups/{group}/report`

The PDF for one period, from `ClientReportRunner.RenderAsync`, as `application/pdf` with the dialog's filename (`ClientReportDocument.FileName`) in `Content-Disposition`.

| Filter | Values |
| --- | --- |
| `period` | `week`, `month` or `quarter`, with `of`. |
| `of` | A date inside the period (default: today in the report time zone, so the period in progress). |
| `start`, `end` | A custom range instead of `period`, both dates, `end` included, as the dialog takes them. |

Giving both `period` and `start`/`end`, neither, or only one of `start`/`end` is a 400. Periods are built with `ReportPeriods`, as the dialog builds them, in the report time zone.

### `POST /groups/{group}/report/send`

Body `{ period?, of?, start?, end?, recipients? }`, with the same period rules as the download. `recipients` defaults to the Group's schedule recipients; each must be a valid address. It calls `ClientReportRunner.SendNowAsync` with the key as the actor, so the delivery, retries and audit entry are as when sent from the app, and returns the delivery (as in the history) with 200 whether it was sent or failed. No recipients is a 400; email not set up is a 409.

## 3. Parse failures (Domains edit)

### `GET /parse-failures`

Newest last attempt first, paged. `{ id, messageId, reason, attempts, lastAttemptUtc }`. A key limited to groups gets a 403 naming why, since failures belong to no domain.

| Filter | Values |
| --- | --- |
| `search` | Part of the reason, case-insensitive. |
| `minAttempts` | A whole number of 1 or more. |
| `lastAttemptAfter`, `lastAttemptBefore` | Dates. |

## 4. Alert tickets (Alerts view)

`ApiAlert` gains `tickets`: `[{ psa, ticketId, open, createdUtc, lastCheckedUtc }]`, `psa` being `HaloPsa`, `ConnectWise` or `Autotask`. They load in one query for the page. `/alerts` gains `hasOpenTicket` (`true`/`false`).

## 5. Docs

The "Using the API" page gains "Reports and downloads": sources versus the summary, raw files, downloading and sending client reports (with a curl example saving the PDF), and the summary.

## 6. Testing

- **Sources:** totals, combined SPF/DKIM/disposition and pass rate match `DomainStatistics.GetSourceAggregates` over the same records; each filter; organisation from cache and null when uncached; ranges as on the summary.
- **Reports:** list shape and paging; filters; one report; another domain's id is a 404; raw XML byte-for-byte with its content type.
- **TLS reports:** as for reports, plus `failuresOnly` and `resultType`; raw JSON.
- **Summary:** figures match `DashboardSummary.Build` and `PortalStatus.For` over the visible domains; a limited key's figures cover only its domains; `parseFailureCount` null without Domains edit or for a limited key.
- **Client reports:** schedule with and without one; deliveries and filters; the PDF's content type, filename and that it is a PDF; period validation; send now recording a delivery and an audit entry with the key as actor, default and given recipients, no recipients 400, email not set up 409; a limited key's other Group 404.
- **Parse failures:** list, filters, 403 for a limited key.
- **Alert tickets:** tickets on alerts; `hasOpenTicket`.
- **OpenAPI:** every new operation has a summary, permission, section and documented errors (extend `EveryOperation_DescribesEachErrorItCanReturn_AsAProblem`); file responses carry their content types; the committed document is valid.
