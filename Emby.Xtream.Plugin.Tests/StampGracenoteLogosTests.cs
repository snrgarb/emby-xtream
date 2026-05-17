using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// BUG-023 regression tests for <see cref="XtreamTunerHost.StampLogosFromProviderChannels"/>.
    /// Covers the field-match priority order observed on Emby Guide Data (<c>Id</c> first,
    /// then <c>ListingsChannelId</c>, <c>TunerChannelId</c>, <c>AlternateNames</c>) and
    /// confirms a Dispatcharr-supplied <c>ImageUrl</c> is never overwritten.
    /// </summary>
    public class StampGracenoteLogosTests
    {
        private static ChannelInfo Target(string stationId)
        {
            return new ChannelInfo
            {
                Id = "xtream_" + stationId,
                Name = "Target_" + stationId,
                ListingsChannelId = stationId,
                ImageUrl = null,
            };
        }

        private static Dictionary<string, ChannelInfo> Index(params ChannelInfo[] targets)
        {
            var d = new Dictionary<string, ChannelInfo>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var c in targets)
                d[c.ListingsChannelId] = c;
            return d;
        }

        [Fact]
        public void Stamps_logo_when_provider_id_matches_station_id()
        {
            var target = Target("10309");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = "https://logo/kabc.png" },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(1, stamped);
            Assert.Equal("https://logo/kabc.png", target.ImageUrl);
            Assert.Equal(1, counts["Id"]);
        }

        [Fact]
        public void Stamps_via_listings_channel_id_when_provider_id_is_empty()
        {
            var target = Target("51529");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo
                {
                    Id = string.Empty,
                    ListingsChannelId = "51529",
                    Name = "A&E",
                    ImageUrl = "https://logo/ae.png",
                },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(1, stamped);
            Assert.Equal("https://logo/ae.png", target.ImageUrl);
            Assert.Equal(1, counts["ListingsChannelId"]);
        }

        [Fact]
        public void Stamps_via_alternate_name_when_other_fields_dont_match()
        {
            var target = Target("78808");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo
                {
                    Id = "lineup-internal-id",
                    Name = "AHC",
                    AlternateNames = new[] { "78808" },
                    ImageUrl = "https://logo/ahc.png",
                },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(1, stamped);
            Assert.Equal("https://logo/ahc.png", target.ImageUrl);
            Assert.Equal(1, counts["AlternateNames"]);
        }

        [Fact]
        public void Does_not_overwrite_existing_image_url()
        {
            var target = Target("10309");
            target.ImageUrl = "https://dispatcharr/local-logo.png";
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = "https://gracenote/kabc.png" },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(0, stamped);
            Assert.Equal("https://dispatcharr/local-logo.png", target.ImageUrl);
            Assert.Empty(counts);
        }

        [Fact]
        public void Provider_channel_without_image_url_is_skipped()
        {
            var target = Target("10309");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = null },
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = string.Empty },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(0, stamped);
            Assert.Null(target.ImageUrl);
        }

        [Fact]
        public void No_match_leaves_targets_untouched()
        {
            var target = Target("99999");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = "https://logo/kabc.png" },
                new ChannelInfo { Id = "10367", Name = "KCBS", ImageUrl = "https://logo/kcbs.png" },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(0, stamped);
            Assert.Null(target.ImageUrl);
            Assert.Empty(counts);
        }

        [Fact]
        public void First_field_match_wins_per_provider_channel()
        {
            // A target keyed by 51529 — a provider channel that happens to have 51529 in
            // BOTH Id and AlternateNames. Should match once on Id and not double-stamp.
            var target = Target("51529");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo
                {
                    Id = "51529",
                    AlternateNames = new[] { "51529", "AandE" },
                    Name = "A&E",
                    ImageUrl = "https://logo/ae.png",
                },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(target), providerChannels, counts);

            Assert.Equal(1, stamped);
            Assert.Equal("https://logo/ae.png", target.ImageUrl);
            Assert.Equal(1, counts["Id"]);
            Assert.False(counts.ContainsKey("AlternateNames"));
        }

        [Fact]
        public void Stamps_multiple_targets_across_multiple_provider_channels()
        {
            var kabc = Target("10309");
            var kcbs = Target("10367");
            var ae = Target("51529");
            var providerChannels = new List<ChannelInfo>
            {
                new ChannelInfo { Id = "10309", Name = "KABC", ImageUrl = "https://logo/kabc.png" },
                new ChannelInfo { Id = "10367", Name = "KCBS", ImageUrl = "https://logo/kcbs.png" },
                new ChannelInfo { Id = "51529", Name = "A&E",  ImageUrl = "https://logo/ae.png" },
                new ChannelInfo { Id = "99999", Name = "Unknown", ImageUrl = "https://logo/x.png" },
            };
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);

            var stamped = XtreamTunerHost.StampLogosFromProviderChannels(
                Index(kabc, kcbs, ae), providerChannels, counts);

            Assert.Equal(3, stamped);
            Assert.Equal("https://logo/kabc.png", kabc.ImageUrl);
            Assert.Equal("https://logo/kcbs.png", kcbs.ImageUrl);
            Assert.Equal("https://logo/ae.png", ae.ImageUrl);
            Assert.Equal(3, counts["Id"]);
        }
    }
}
