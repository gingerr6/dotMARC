# dotMARC – Handover

_Last updated: Sunday 15 November 2026 (London time)_

## 1. What it is
- **dotMARC** is a self-hosted .NET (Blazor/MudBlazor) app that analyses DMARC aggregate reports. Upstream: [homotechsual/dotMARC](https://github.com/homotechsual/dotMARC).
- James runs his own fork, **[gingerr6/dotMARC](https://github.com/gingerr6/dotMARC)**, which has changes that upstream doesn't.
- The live instance is **dotmarc.mspcyber.uk**.
- Domains under DMARC management include bcdr.co.uk and budgetwaste.co.uk (budgetwaste sends through SMTP.com), plus client domains.

## 2. How a change gets live
1. A pull request is opened against `main` on the fork.
2. CI runs (the Build and test jobs; Deploy is skipped on PRs).
3. The PR is squash-merged. **Standing rule: "Always merge on passes".** Every PR (fixes and features) is merged as soon as CI passes, without asking James (confirmed 15 Nov 2026).
4. On `main`, CI passes, then the **Publish** workflow builds the container image.
5. The server checks for a new image every **15 minutes** and redeploys itself.
6. **From merge to live usually takes about 30 minutes.**

## 3. Recent work (merged)
| PR | What it did | Merge commit |
|---|---|---|
| [#45](https://github.com/gingerr6/dotMARC/pull/45) | Sender classification work. Google's unverified-forwarding pool gets its own forwarder entry, Groups.io is recognised as a mailing list, and likely-spoof sources are flagged. Also adds the first version of AI source suggestions. | 0a78d99 |
| [#46](https://github.com/gingerr6/dotMARC/pull/46) | Adds a role permission for AI suggestions and stops cutting off the reason text (it's now capped at 1,000 characters and wraps). The prompt now explains other domains it mentions, such as the SPF bounce domain. | 5d61b3fa |
| [#47](https://github.com/gingerr6/dotMARC/pull/47) | Fixes the dashboard group filter being lost when you navigate back to it. The filter is now kept in the page address. | fb146a34 |
| [#48](https://github.com/gingerr6/dotMARC/pull/48) | Removes the personal AI switch from the Sources tab and removes the role permission. Suggest now shows for every signed-in user once a provider is set up. | 54493fb |
| [#49](https://github.com/gingerr6/dotMARC/pull/49) | Fixes the group filter regression from #47. Clearing the Group crashed the Blazor circuit (the dropdown was removed mid-click), and MudBlazor's clear left an invisible open list that swallowed the next click. The dropdowns now stay on screen while loading, `SearchableSelect` closes its list after a clear, and the address is updated with `history.replaceState` instead of a second navigation. | c605f5b |
| [#50](https://github.com/gingerr6/dotMARC/pull/50) | Adds **Suggest fix** on the Alerts page: asks the connected AI provider how to fix one open alert and shows a summary and steps under the row. | (see PR) |

## 4. Open / in progress
- Nothing in progress. The group filter regression from #47 is fixed in #49.

## 5. How the main features work
### Sender classification
- Known senders are matched by hostname, with the longest matching suffix winning. Where there's no hostname, CIDR IP ranges are used instead.
- A source is flagged as a **likely spoof** when all of these are true:
  - it isn't a known sender;
  - SPF and DKIM both failed;
  - it sent 50 messages or fewer;
  - it has no reverse DNS name, or the name looks like a home or dynamic connection.
- A saved decision (**Approved** or **Not ours**) always overrides the automatic classification.
- There is **no** manual "Forwarder" decision. Forwarders are only recognised from the built-in list.

### AI source suggestions
- An admin sets up an OpenAI-compatible provider at `/ai/settings` (under Manage, then AI suggestions). The API key is stored encrypted.
- For Claude, the endpoint is `https://api.anthropic.com/v1/` and the model is something like `claude-haiku-4-5` or `claude-sonnet-4-5`.
- Each request goes to `{endpoint}/chat/completions` with temperature 0.
- Only facts about the source are sent: IP, hostname, owner, country, volume, SPF, DKIM, disposition, current classification, auth results, and the domain being viewed. No email content is sent.
- The answer is one of four verdicts (legitimate service, forwarder, likely spoof, unknown) plus a reason.
- The verdict is **not saved** and **never** changes a source's decision. Every request is written to the audit log.
- Since #48, every signed-in app user can use it. **API keys and client portal users can't.**
- **Suggest fix on alerts (#50)** uses the same provider, key and access rules. It sends one alert (type, severity, title, message) plus the latest DNS check results for its domain, and shows a one-line summary and up to 8 steps under the row. Nothing is changed or saved; each request is audited as `alert.fix_suggestion_requested`. The demo hides it.
- The AI integration hasn't been tested end to end yet, because no Anthropic API key has been supplied.

## 6. Code map (fork)
- `src/DotMarc/Senders/` holds the classification and suggestion code: `SourceSuggestionService`, `SourceSuggestionSettingsService`, `OpenAiCompatibleSuggestionClient`, `SourceSuggestionFacts`, `UserPreferenceService` and the known-senders list.
- `src/DotMarc/Notifications/AlertFixSuggestion.cs` holds the alert fix suggestion (facts, prompt, reply parsing, service). `OpenAiCompatibleSuggestionClient` serves both kinds of suggestion.
- `src/DotMarc/Components/Pages/AiSuggestionSettings.razor` is the AI settings page at `/ai/settings`.
- `src/DotMarc/Components/Pages/DomainDetail.razor` is the domain page, including the Sources tab and the Suggest button.
- The dashboard's group filter is kept in the page address's query string (since #47). It is written with `history.replaceState` (`dotmarcReplaceAddress` in `App.razor`), not a Blazor navigation (since #49). `DashboardFilterSession` in `src/DotMarc/Reporting/DashboardFilter.cs` tracks which filter is shown and which load is current.
- Don't hide a MudBlazor input while its own event is still running (for example, replacing it with a spinner during a reload): its follow-up JS focus call throws and ends the circuit.

## 7. Working notes
- James usually dictates, so expect misheard words. "Deakin" means DKIM, and "Box Bush" is a client domain.
- Fix problems directly through PRs on the fork rather than handing James code.
- There is no bot login to the live instance, so changes are checked through CI and James's feedback.
