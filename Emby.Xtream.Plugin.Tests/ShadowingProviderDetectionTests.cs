using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// ADR-008 / BUG-023 regression tests for
    /// <see cref="XtreamTunerHost.IsLikelyShadowingProvider"/>. The detection fires when a
    /// non-Gracenote listings provider's lineup covers >=50% of the plugin's station IDs,
    /// since Emby's guide rendering layer will overlay that provider's programs on top of
    /// the plugin's returned Gracenote data.
    /// </summary>
    public class ShadowingProviderDetectionTests
    {
        private static HashSet<string> Stations(params string[] ids)
        {
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }

        private static ChannelInfo ProviderChan(string id = null, string listingsChannelId = null,
            string tunerChannelId = null, string[] alts = null)
        {
            return new ChannelInfo
            {
                Id = id,
                ListingsChannelId = listingsChannelId,
                TunerChannelId = tunerChannelId,
                AlternateNames = alts,
            };
        }

        [Fact]
        public void Warns_when_xmltv_provider_covers_all_station_ids_via_id()
        {
            var pluginStations = Stations("10309", "51529", "44895");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
                ProviderChan(id: "51529"),
                ProviderChan(id: "44895"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.True(warn);
            Assert.Equal(3, covered);
        }

        [Fact]
        public void Warns_at_exactly_fifty_percent_coverage()
        {
            // 2 of 4 station IDs covered → 50% → warn.
            var pluginStations = Stations("10309", "51529", "44895", "78808");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
                ProviderChan(id: "51529"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.True(warn);
            Assert.Equal(2, covered);
        }

        [Fact]
        public void Does_not_warn_below_fifty_percent_coverage()
        {
            // 1 of 4 → 25% → no warn.
            var pluginStations = Stations("10309", "51529", "44895", "78808");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
                ProviderChan(id: "99999"), // not in plugin stations
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.False(warn);
            Assert.Equal(1, covered);
        }

        [Fact]
        public void Never_warns_for_embygn_provider_even_at_full_coverage()
        {
            // Gracenote (embygn) IS supposed to cover the plugin's station IDs — that's
            // the design. Never warn for it regardless of coverage.
            var pluginStations = Stations("10309", "51529", "44895");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
                ProviderChan(id: "51529"),
                ProviderChan(id: "44895"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "embygn", pluginStations, providerChannels, out covered);

            Assert.False(warn);
            Assert.Equal(3, covered);
        }

        [Fact]
        public void Type_check_is_case_insensitive()
        {
            var pluginStations = Stations("10309", "51529");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
                ProviderChan(id: "51529"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "EmbyGN", pluginStations, providerChannels, out covered);

            Assert.False(warn);
        }

        [Fact]
        public void Same_station_id_in_multiple_fields_counts_once()
        {
            // A single provider channel with the station ID in both Id AND AlternateNames
            // must only contribute 1 to coverage. Without distinct-counting we'd say 2,
            // which would falsely cross the threshold on small lineups.
            var pluginStations = Stations("10309", "51529", "44895", "78808");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309", alts: new[] { "10309" }),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.False(warn);
            Assert.Equal(1, covered);
        }

        [Fact]
        public void Same_station_id_across_multiple_channels_counts_once()
        {
            // Two provider channels both keyed by 10309 (e.g. SD/HD pair). Should count
            // 1 toward coverage, not 2.
            var pluginStations = Stations("10309", "51529", "44895", "78808");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309", listingsChannelId: "KABC-SD"),
                ProviderChan(id: "10309", listingsChannelId: "KABC-HD"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.False(warn);
            Assert.Equal(1, covered);
        }

        [Fact]
        public void Empty_plugin_stations_never_warns()
        {
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "10309"),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", Stations(), providerChannels, out covered);

            Assert.False(warn);
            Assert.Equal(0, covered);
        }

        [Fact]
        public void Empty_provider_channels_never_warns()
        {
            var pluginStations = Stations("10309", "51529");

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, new List<ChannelInfo>(), out covered);

            Assert.False(warn);
            Assert.Equal(0, covered);
        }

        [Fact]
        public void Coverage_via_alternate_names_when_id_does_not_match()
        {
            // Some XMLTV providers put the station ID in AlternateNames rather than Id.
            // Coverage should still count.
            var pluginStations = Stations("10309", "51529");
            var providerChannels = new List<ChannelInfo>
            {
                ProviderChan(id: "lineup-internal-1", alts: new[] { "10309" }),
                ProviderChan(id: "lineup-internal-2", alts: new[] { "51529" }),
            };

            int covered;
            var warn = XtreamTunerHost.IsLikelyShadowingProvider(
                "xmltv", pluginStations, providerChannels, out covered);

            Assert.True(warn);
            Assert.Equal(2, covered);
        }
    }
}
