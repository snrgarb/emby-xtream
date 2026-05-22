# ADR-008: Detecting XMLTV Listings-Provider Shadowing of Gracenote Programs

**Date**: 2026-05-22
**Status**: ADOPTED
**Affects**: `XtreamTunerHost.StampGracenoteLogosOntoChannels` (where the warning emission lives), `XtreamTunerHost.IsLikelyShadowingProvider` (new static helper), `Emby.Xtream.Plugin.Tests/ShadowingProviderDetectionTests.cs` (10 tests)
**Related**: [ADR-005: Detach Listing Providers](005-detach-listing-providers.md), [ADR-007: Stamp Gracenote Logos onto Detached Channels](007-stamp-gracenote-logos.md), [BUG-023](../../BUGS.md)

---

## Context

[ADR-005](005-detach-listing-providers.md) established that the Xtream tuner detaches itself from all of Emby's listings providers so the plugin can fetch Gracenote programs directly for the channels that have a station ID. The reasoning was to prevent Emby's auto-mapper from associating Gracenote stations with the wrong tuner channels.

ADR-005 assumed the detach was sufficient to give the plugin sole authority over what programs each channel shows in the guide. That assumption turns out to be incomplete.

## Problem

A user (ullms1, [GitHub #20](https://github.com/firestaerter3/emby-xtream/issues/20)) reported that Gracenote-mapped channels in his guide were filled with placeholder data — channel call signs as program titles, 13 × 4-hour blocks — even though the plugin was correctly fetching ~18 Gracenote programs per channel and returning them via `GetProgramsInternal`. Log analysis confirmed:

- Plugin had 217 channels in cache, 161 with Gracenote station IDs
- 644 successful Gracenote program fetches, zero failures
- Plugin returned correct `ProgramInfo` records to Emby

The placeholders that appeared in his guide came from a separate **XMLTV listings provider** he had configured in Emby. The XMLTV provider's channels had `Id` values that exactly matched the Gracenote station IDs (his `[gracenote-diag]` block: `XmlTV/: intersection with 160 plugin station IDs — Id:160`). Even though the plugin was detached from this XMLTV provider, Emby's guide-rendering layer was reading XMLTV's programs for those channels — by station ID match — and rendering them in place of (or on top of) what the plugin returned.

Removing the XMLTV listings provider from Emby's settings restored the Gracenote programs immediately. The user found this himself after the v1.4.77 diag block prompted him to check what listings providers were configured.

## Mechanism (Inferred)

Emby's guide does not appear to use only the tuner's `GetProgramsInternal` output. When a listings provider has a channel whose `Id` matches a tuner channel's `ListingsChannelId` (or similar identifier), Emby uses the listings provider's programs for that channel as well — possibly with provider precedence rules we don't currently understand.

This is consistent with what [ADR-005](005-detach-listing-providers.md) documented under "The Circular Gracenote Call": the plugin calls Emby's internal `IListingsProvider.GetProgramsAsync` to fetch Gracenote programs. But Emby's guide can also call other registered `IListingsProvider` instances independently for the same channel ID — the plugin has no visibility or control over that.

## Alternatives Considered

### 1. Do nothing (status quo)

Leave it to users to spot the conflict. Document the workaround (remove the XMLTV provider) in `docs/architecture/gracenote-epg-chain.md` and via the `[gracenote-diag]` block as a passive signal.

**Pros**: No code change, no risk of detaching something the user wanted.
**Cons**: Users won't know to look. The `[gracenote-diag]` block reports the intersection but doesn't flag it as a problem. ullms1 only found it because the maintainer pointed him at the diag and he scrolled through his Emby settings.

### 2. Warn in the diag log when a non-Gracenote listings provider has high-overlap channels

Extend the existing `[gracenote-diag]` block to print an explicit warning when a non-`embygn` listings provider's channels have >N% overlap with plugin station IDs:

```
[gracenote-diag] WARNING: provider 'XmlTV' has 160/176 channels matching plugin
  station IDs by Id. This provider's programs may shadow Gracenote data in the
  guide. If channels are showing placeholder EPG, try disabling this listings
  provider in Emby's settings.
```

**Pros**: Visible to anyone who reads the log. Doesn't modify user config. Low risk.
**Cons**: Requires user to read logs. Doesn't fix the problem, just names it.

### 3. Detach the shadowing provider's channels for the matched station IDs

Programmatically modify Emby's `LiveTvOptions` to add the conflicting listings provider IDs to an "ignore for these station IDs" list. Requires reverse-engineering whether Emby supports this — not all `IListingsProvider` configs expose per-station exclusions.

**Pros**: Active fix.
**Cons**: Likely no public API for this. Would need reflection or per-provider-type special handling. Risk of breaking the user's intended use of the XMLTV provider on other tuners (M3U, HDHomeRun) that legitimately want its programs.

### 4. Active auto-detach of shadowing providers

When the plugin detects a non-`embygn` listings provider with high-overlap (e.g. >50%) station IDs, automatically detach it from all tuners. Mirrors the existing `DetachListingProviders` pattern but extended to cover the shadow case.

**Pros**: Self-healing.
**Cons**: Too aggressive — the user may have the XMLTV provider configured for a separate M3U tuner that genuinely needs it. Removing it system-wide would break that other tuner. Also irreversible from the plugin's perspective once detached.

## Decision

**Adopt Alternative 2 (diag warning)** as the immediate step.

Defer Alternative 4 (active auto-detach) pending evidence that this conflict occurs frequently enough to justify the risk of breaking other tuners. ullms1 is the first reported case; if more users hit it after the warning ships, revisit.

Implementation:

- New constant `GracenoteProviderType = "embygn"` on `XtreamTunerHost`.
- New static helper `IsLikelyShadowingProvider(providerType, allPluginStationIds, providerChannels, out coveredStationIdCount)` returns true when (a) provider type is not `embygn` and (b) the count of *distinct* plugin station IDs covered by ANY of `Id`/`ListingsChannelId`/`TunerChannelId`/`AlternateNames` reaches at least 50% of the plugin's total. Counting distinct station IDs (not field-side hits) avoids false positives where one provider channel matches via multiple fields or the same station ID appears in SD+HD pairs.
- Called from inside the `firstRunDiagnostic` block in `StampGracenoteLogosOntoChannels`, immediately after the existing intersection report line. Warning emitted via `Logger.Warn` so it surfaces above default log filters.
- One warning per shadowing provider per process — the existing `_gracenoteFieldDiagnosticLogged` flag bounds the entire diagnostic block to first invocation.
- Warning format: `[gracenote-diag] WARNING: {providerLabel} (type='{type}') covers {covered}/{total} of your Gracenote station IDs. Its programs may shadow Gracenote data in the Emby guide. If channels are showing placeholder EPG (channel name as program title, fixed-duration blocks), remove this listings provider from Emby's Live TV settings.`
- 10 unit tests in `ShadowingProviderDetectionTests.cs` cover the threshold (0%, 25%, 50%, 100%), the `embygn` exemption (case-insensitive), distinct-counting (same station via multiple fields, same station across multiple channels), the empty inputs, and coverage via `AlternateNames`.

## Consequences

**Positive**:
- Future users with the same setup will see the cause named directly in the log instead of discovering it by accident.
- Maintains the principle that the plugin doesn't silently modify user config beyond `DetachListingProviders` (which only affects the Xtream tuner's own associations).
- Low implementation risk: extends an existing well-tested code path.

**Negative**:
- Still requires the user to read the log to find the warning.
- Doesn't help users who don't read logs and don't reach out — they'll continue to see degraded guide data with no idea why.
- The warning may misfire if a user legitimately wants XMLTV programs to shadow Gracenote (unlikely but possible) — the warning is informational only, not blocking.

## Related

- [ADR-005: Detach Listing Providers](005-detach-listing-providers.md) — the foundational decision this builds on
- [ADR-007: Stamp Gracenote Logos onto Detached Channels](007-stamp-gracenote-logos.md) — added the `StampGracenoteLogosOntoChannels` method and `[gracenote-diag]` block this ADR extends
- [Gracenote EPG Architecture](../architecture/gracenote-epg-chain.md) — the "Circular Gracenote Call" section describes the call pattern that shadowing exploits
- [BUG-023](../../BUGS.md) — the bug this ADR responds to
- [GitHub #20](https://github.com/firestaerter3/emby-xtream/issues/20) — the original report and discovery thread
