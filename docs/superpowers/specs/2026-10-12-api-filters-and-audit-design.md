# API filters and the audit log API

**Release:** dotMARC 0.9.2
**Status:** approved in brainstorming, awaiting spec review

## Purpose

API users have to fetch whole lists and filter them themselves: alerts filter only by open or all, domains by one Group, one tag and monitoring. This adds granular filters across the list endpoints, so a script fetches only what it needs, and an audit log endpoint, so who did what can be pulled into a SIEM or PSA.

This is the first of two pieces of work. The second adds new read endpoints (sending sources, individual DMARC reports, TLS reports, a dashboard summary, client reports, parse failures and alert tickets) on the conventions set here.

## Decisions made in brainstorming

- **Filter conventions** apply to every filter, now and in the second piece (section 1).
- **The audit log** is readable with `AuditView`. A key limited to Groups sees the entries about its own domains, Groups and their alerts; everything else is left out.
- **Page views** are left out of the audit log unless asked for.
- **Every current call keeps working** and returns the same results; filters only narrow.

## Scope

**In:** the shared filter conventions and parsing; `GET /audit`; new filters on `GET /alerts`, `GET /domains`, `GET /domains/{domain}/reports/summary`, `GET /groups` and `GET /tags`; OpenAPI documentation of every filter with its allowed values; the "Filtering" section of the API docs; tests.

**Out:** the new read endpoints (the second piece); write endpoints for deleting domains or managing Groups and tags; sorting options; choosing which fields come back; access grants, API keys, settings and server logs, which stay out of the API on purpose.

## 1. Filter conventions

1. **Several values:** a filter marked "one or more" accepts `type=A,B` and `type=A&type=B`, or both together. Values within one filter combine as "any of"; different filters combine as "all of".
2. **Matching:** values are case-insensitive. Domains are given by id or name, as on the `/domains/{domain}` routes (`ApiDomainKey`). Groups and tags are given by id.
3. **Unknown values:** a 400 validation problem naming the filter and listing the values it accepts, for example `severity: Use Info or Warning.` Blank values in a list (`type=A,,B`) are ignored.
4. **Dates:** ISO 8601, either a date (`2026-10-01`, meaning 00:00 UTC that day) or a date and time with an offset or `Z`. `...After` includes the moment given; `...Before` excludes it. A date that doesn't parse is a 400 naming the filter. A range whose `...After` is not earlier than its `...Before` is a 400.
5. **Numbers:** `passRate...` filters take 0 to 1; anything else is a 400.
6. **Documentation:** every filter is described in the OpenAPI document, with its allowed values listed as an enum where there is a fixed set. Multi-value filters are documented as arrays (`style: form`, `explode: true`), and the description says commas work too.

## 2. `GET /audit`

### Access

- Needs the `AuditView` permission.
- A key not limited to Groups sees every entry.
- A key limited to Groups sees only entries whose target is:
  - a domain it can see: target type `Domain` with that domain's id, or, for entries that record only a name (`AuditTarget.DomainNamed`), that domain's name;
  - a Group in its scope: target type `Group` with that Group's id;
  - an alert on a domain it can see: target type `Alert`, whose target name is the domain name.
- Every other entry (sign-ins, page views, settings, roles, access, API keys, tags, and entries with no target) is left out for a limited key. Entries about a domain the key can no longer see (for example because it was deleted) are left out too.

### Response

`ApiPage<ApiAuditEntry>`, newest first (`occurredUtc` descending, then `id` descending), 50 a page by default and at most 200.

`ApiAuditEntry`:

| Field | Notes |
| --- | --- |
| `id` | The entry's id. |
| `occurredUtc` | When it happened. |
| `kind` | `change`, `signIn` or `pageView`. |
| `actor` | `{ kind, name, email }`; `kind` is `user`, `system` or `apiKey`; `email` is null for dotMARC itself and API keys. |
| `action` | The stable code from `AuditActions`, for example `DomainAdded`. |
| `target` | `{ type, id, name }`, or null when the entry has no target. |
| `summary` | The entry's summary line. |
| `changes` | `[{ field, old, new, secret }]`; a secret field has `secret: true` and null `old` and `new`, as stored. |

### Filters

| Filter | Values |
| --- | --- |
| `action` | One or more codes from `AuditActions`, listed as an enum. |
| `kind` | One or more of `change`, `signIn`, `pageView`, or `all`. Default `change,signIn`. |
| `actorKind` | One or more of `user`, `system`, `apiKey`. |
| `actor` | Text matched, case-insensitively, against the actor's name or email. |
| `targetType` | One or more of `Domain`, `Group`, `Tag`, `Alert`, `Role`, `UserAccess`, `ApiKey`, `Settings`. |
| `targetId` | One or more target ids (with `targetType` to be unambiguous). |
| `domain` | One or more domains (id or name): entries targeting the domain, and its alerts. A domain the key can't see is a 404, as on the domain routes. |
| `occurredAfter`, `occurredBefore` | Dates (section 1). |
| `search` | Text matched, case-insensitively, against the summary and the target name. |

## 3. Filters on existing endpoints

### `GET /alerts`

| Filter | Values |
| --- | --- |
| `status` | `open` (default), `resolved` or `all`. `resolved` is new. |
| `type` | One or more alert type codes from `AlertTypes`, listed as an enum. |
| `severity` | One or more of the severities dotMARC raises (`Info`, `Warning`). |
| `domain` | One or more domains (id or name). A domain the key can't see is a 404. |
| `group`, `tag` | One or more ids: alerts for domains in any of them. A Group outside the key's scope matches nothing. |
| `acknowledgeable` | `true` or `false`, as `ApiAlert.Acknowledgeable` defines it. |
| `raisedAfter`, `raisedBefore` | Dates, against `CreatedUtc`. |
| `resolvedAfter`, `resolvedBefore` | Dates, against `ResolvedUtc`; open alerts never match. |

### `GET /domains`

| Filter | Values |
| --- | --- |
| `search` | Part of the domain name, case-insensitive. |
| `group`, `tag` | One or more ids (was one). A Group outside the key's scope matches nothing. |
| `monitored` | As now. |
| `dmarc` | One or more `DmarcCheckStatus` values. |
| `spf` | One or more `SpfCheckStatus` values. |
| `dkim` | One or more `DkimCheckStatus` values. |
| `mx` | One or more `MxCheckStatus` values. |
| `tlsrpt` | One or more `TlsrptCheckStatus` values. |
| `mtaSts` | One or more `MtaStsStatus` values. |
| `dmarcAuthorization` | One or more `DmarcAuthorizationCheckStatus` values. |
| `status` | One or more `PortalHealth` values (`NeedsAttention`, `NoReportsYet`, `MonitoringOnly`, `Protected`), worked out with `PortalStatus.For` exactly as the portal and client reports do. |
| `policy` | One or more `DmarcPolicyLevel` values, against the published policy. |
| `dnsProvider` | One or more `DetectedDnsProvider` values. |
| `lastReportAfter`, `lastReportBefore` | Dates, against `LastReportReceivedUtc`; domains with no report never match. |
| `noReports` | `true`: domains with no report ever; `false`: domains with at least one. |
| `passRateBelow`, `passRateAtLeast` | 0 to 1, against the 30-day pass rate the list returns; domains with no pass rate never match. |

`status`, `passRateBelow` and `passRateAtLeast` depend on values calculated outside the database query. When any is given, the endpoint filters everything else in the query, calculates those values for the remaining domains, filters, and then pages, so `totalCount` and paging stay correct.

### `GET /domains/{domain}/reports/summary`

| Filter | Values |
| --- | --- |
| `days` | As now (1 to 30, default 30). |
| `from`, `to` | Dates, an alternative to `days`; `from` included, `to` excluded. At most 366 days apart. Giving `days` with either is a 400. |
| `top` | 1 to 200 sending IPs (default 20). |

The response's `days` is the length of the range used.

### `GET /groups` and `GET /tags`

| Filter | Values |
| --- | --- |
| `search` | Part of the name, case-insensitive. |

## 4. Docs

The "Using the API" page gets a "Filtering" section: the conventions in section 1, the audit log's access rule, and examples, including domains whose SPF is broken in one Group, open SPF alerts raised this week, and the audit entries for one domain.

## 5. Testing

- **Conventions:** comma and repeated values, mixed; case-insensitivity; an unknown value's 400 listing the accepted values; blank entries ignored; date-only and date-time values; `...After` inclusive and `...Before` exclusive; an inverted range refused.
- **Audit:** every filter; the default kinds leaving page views out; newest-first paging; secret changes without values; a limited key seeing its domains', Groups' and alerts' entries, an entry recorded by domain name only, and not seeing sign-ins, settings, other Groups' domains or a deleted domain; `AuditView` required.
- **Alerts:** each filter, alone and combined; `resolved` status; a Group outside scope matching nothing; an unseen domain giving 404.
- **Domains:** each health filter; `status` matching `PortalStatus`; pass-rate filters with paging and `totalCount` correct; several Groups and tags; `search`.
- **Report summary:** `from`/`to` against `days`; `days` with dates refused; `top`.
- **Groups and tags:** `search`.
- **Compatibility:** each existing endpoint test still passes unchanged.
- **OpenAPI:** the committed document lists each filter, with enums for fixed sets, and remains valid OpenAPI 3.
