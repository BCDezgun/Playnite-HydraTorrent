using System;
using System.Text.RegularExpressions;

namespace HydraTorrent.Models
{
    public static class MagnetLinkValidator
    {
        private static readonly Regex HexHashRegex = new Regex(
            @"urn:btih:([a-fA-F0-9]{40})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Base32HashRegex = new Regex(
            @"urn:btih:([A-Z2-7]{32})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool Validate(string magnet, out string hash)
        {
            hash = null;

            if (string.IsNullOrEmpty(magnet))
                return false;

            if (!magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return false;

            var hexMatch = HexHashRegex.Match(magnet);
            if (hexMatch.Success)
            {
                hash = hexMatch.Groups[1].Value.ToLowerInvariant();
                return true;
            }

            var base32Match = Base32HashRegex.Match(magnet);
            if (base32Match.Success)
            {
                hash = base32Match.Groups[1].Value.ToLowerInvariant();
                return true;
            }

            return false;
        }
    }
}
