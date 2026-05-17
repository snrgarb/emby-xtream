# ADR-007: Stamp Gracenote Logos onto Detached Channels

**Date**: 2026-05-17
**Status**: ADOPTED (v1.4.77)
**Affects**: `XtreamTunerHost.StampGracenoteLogosOntoChannels()`, `GetChannelsInternal()`
**Related**: [ADR-005: Detach Listing Providers](005-detach-listing-providers.md)

---

## Context

ADR-005 detaches the Xtream tuner from all listing providers when `DeferEpgToGuideData` is enabled, then fetches Gracenote programs directly via `IListingsProvider.GetProgramsAsync()`. This solves the wrong-channel auto-mapping problem (BUG-018) but leaves a side-effect: Emby's normal tuner-to-listings cross-match, which also assigns station logos, no longer runs.

Reported as [GitHub issue #20](https://github.com/firestaerter3/emby-xtream/issues/20) (BUG-023, reporter ullms1, affected versions v1.4.61 onward): channels mapped to a Gracenote station ID in Dispatcharr show no logo unless one is set directly on the channel in Dispatcharr. Dispatcharr-supplied logos work as expected; the gap is for channels that rely on Gracenote artwork.

## Problem

`ChannelInfo.ImageUrl` is the only source of channel logos Emby honours for a tuner-host-provided channel. The plugin populates it from `LiveStreamInfo.StreamIcon` (Dispatcharr's `stream_icon` field). For channels where Dispatcharr has no `stream_icon` set, the field is null and the channel renders without a logo in the Emby guide.

Before the detach, Emby would cross-match each tuner channel against the attached listing providers (by `Id`, name, call sign, etc.) and use the listings-provider channel's `ImageUrl` to fill in. After the detach, no cross-match happens, so no logo is applied.

## Investigation

Decompiled `MediaBrowser.Controller.dll` and `Emby.LiveTV.dll` (Emby 4.9.3.0) via `ikdasm`. Findings:

1. `IListingsProvider.GetChannels(info, ct)` is callable from the plugin regardless of tuner attachment state, and returns `List<ChannelInfo>` populated with `ImageUrl`, `LightLogoImageUrl`, `LightColorLogoImageUrl`, `Id`, `ListingsChannelId`, `CallSign`, and `AlternateNames`.
2. `Emby.LiveTV.EmbyTV+EpgChannelData` (constructor at `livetv.il:222773`) indexes the returned channels by `ChannelInfo.Id` only — `ChannelsByName`, `ChannelsByCallsign`, and `ChannelsByNumber` are secondary indexes for fallback matching.
3. `Emby.LiveTV.EmbyTV.GetEpgChannelFromTunerChannel` (matching algorithm at `livetv.il:235178`) consults `ChannelOptions.ListingsProviderId` + `ListingsChannelId` first (manual mapping), then falls back to `tunerChannel.Id`, `TunerChannelId`, name/call sign, and number.
4. A local diagnostic run against the Emby Guide Data Hulu Los Angeles lineup confirmed `ChannelInfo.Id` carries the Gracenote station ID:
   ```
   sample: Id='10309' Number='001' Name='KABC' CallSign='KABC'
           ListingsChannelId='' ListingsId='' TunerChannelId='' AlternateNames=[] HasImageUrl=True
   ```
   `ListingsChannelId` is empty on this provider; the station ID is in `Id`. Cross-validated against the log format `Found epg channel in Emby Guide Data USA-DISH658-X LOGOHD 96971` in ullms' v1.4.73 capture — same pattern.

## Alternatives Considered

### 1. Per-channel manual mapping via `LiveTvOptions.ChannelOptions[]`

Emby exposes `ChannelOptions` (in `MediaBrowser.Model.LiveTv`) — a persistent per-channel override with `Id`, `Disabled`, `ListingsProviderId`, `ListingsChannelId`, and `SortIndexNumber`. `EmbyTV.GetChannelOptions(ChannelInfo, ChannelOptions[])` resolves the right options entry for a tuner channel via its `ManagementId` (which is `TunerHostId + "_" + ChannelInfo.Id`), and `EmbyTV.GetEpgChannelFromTunerChannel` treats a populated `ListingsProviderId` as a hard override that disables auto-mapping for that channel.

In theory the plugin could re-attach the tuner to listings providers and write `ChannelOptions` entries for each Xtream channel — restoring logos AND programs through Emby's native code path, with no auto-mapping side-effects.

**Rejected** for two reasons:

1. `ChannelOptions.ListingsChannelId` must match the value `EpgChannelData.GetChannelById` will find — which is the listing provider's `ChannelInfo.Id`, not the Gracenote station ID directly. For Emby Guide Data, those happen to be the same value (`10309` = KABC's station ID = `ch.Id`), but the plugin would need to call `GetChannels` and build a station-ID → lineup-Id map anyway. Same amount of work as option 2.
2. For Xtream channels that have NO Gracenote station ID, the plugin would need to write a `ChannelOptions` entry blocking auto-mapping against every attached provider. `ChannelOptions` is keyed per channel, not per (channel, provider), so this requires either keeping the tuner attached to a single provider only or some other architectural complication. Re-introduces BUG-018 risk.

### 2. Plugin pulls station logos and stamps `ChannelInfo.ImageUrl` directly

After channel cache is built, walk each configured `IListingsProvider`, call `GetChannels(info, ct)`, and for each provider-returned channel with an `ImageUrl`, check whether any candidate station-ID field (`Id`, `ListingsChannelId`, `TunerChannelId`, `AlternateNames`) matches a plugin channel that has a station ID but no `ImageUrl`. Stamp the logo in place.

**Adopted.** Requires no architectural change to ADR-005, no re-attachment, no `ChannelOptions` mutation. Dispatcharr-supplied logos continue to take precedence.

## Decision

`XtreamTunerHost.StampGracenoteLogosOntoChannels(List<ChannelInfo>)` is invoked from `GetChannelsInternal()` (via `Task.Run`) on every channel-cache rebuild when `DeferEpgToGuideData` is on AND at least one channel has a Gracenote station ID.

The method:

1. Resolves `ILiveTvManager` and `IConfigurationManager` via DI. Returns early if no listings providers are configured.
2. Builds a `channelsByStationId` dictionary from plugin channels where `ListingsChannelId != null && string.IsNullOrEmpty(ImageUrl)`.
3. For each `(IListingsProvider, ListingsProviderInfo)` pair, calls `provider.GetChannels(info, CancellationToken.None)` synchronously (the result is from Emby's on-disk cache, so it's fast).
4. Walks the provider channels through `StampLogosFromProviderChannels` — a pure static method extracted for unit-testing. For each provider channel with an `ImageUrl`, tries `Id`, `ListingsChannelId`, `TunerChannelId`, then each `AlternateNames` entry in that order. First match wins and short-circuits further fields on the same provider channel (so a single provider channel never stamps twice).
5. Aggregates per-field stamp counts across all providers and logs a single summary line.

On first invocation per process lifetime, a `[gracenote-diag]` block also logs sample provider channels (Id, Number, Name, CallSign, ListingsChannelId, ListingsId, TunerChannelId, AlternateNames, HasImageUrl) plus the full intersection count between all plugin station IDs and each provider's channel list (per field). This makes future user setups where field-mapping diverges self-diagnosing.

## Consequences

**Positive**:

- Channels with a Gracenote station ID but no Dispatcharr logo now get a logo from any configured listings provider.
- Dispatcharr logos still take precedence (channel-level customization is preserved).
- No architectural change to ADR-005's detach behaviour.
- No `ChannelOptions` mutation — no global Emby config writes beyond what ADR-005 already does.
- Unit-tested decision matrix (`Emby.Xtream.Plugin.Tests/StampGracenoteLogosTests.cs`).

**Negative**:

- Stamping runs asynchronously after `GetChannelsInternal` returns. The first refresh of the guide returns channels without logos; the next refresh (within Emby's own caching window) picks them up. Users typically perform "Refresh Guide" twice when troubleshooting anyway, so the asymmetry is acceptable.
- The diagnostic-only branch makes the first cache rebuild after restart slightly slower (one `IListingsProvider.GetChannels` call per attached provider, plus an `O(n*m)` intersection scan where n = plugin station IDs, m = provider channels). For ullms' setup (127 plugin IDs × ~140 provider channels × 13 providers ≈ 230K comparisons) this completes in <100ms.
- Logo refresh is not invalidated separately — if a listings provider updates a station logo, the change is picked up on the next plugin channel-cache rebuild (5-minute TTL).
- The `_gracenoteFieldDiagnosticLogged` static flag means the verbose diagnostic only fires on the first cache rebuild after Emby process start. Restart Emby (or invalidate via `RefreshCache`) to re-arm it.

## What This Doesn't Fix

The `Error LiveTvManager: Error getting channels` log lines visible in ullms' setup are independent of BUG-023 — they originate when `IListingsProvider.GetChannels` throws on one or more lineups, which is a pre-existing condition of having providers with no enabled tuners after detach. Filed as a separate follow-up if reported again.

## Related

- [ADR-005: Detach Listing Providers](005-detach-listing-providers.md) — the upstream decision this fix extends
- [Gracenote EPG Architecture](../architecture/gracenote-epg-chain.md) (if present)
- GitHub issue #20 (BUG-023)
