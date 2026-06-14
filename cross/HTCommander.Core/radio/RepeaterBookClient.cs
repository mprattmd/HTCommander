/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HTCommander;   // RadioChannelInfo, RadioModulationType, RadioBandwidthType

namespace HTCommander.Core.Radio
{
    /// <summary>Which RepeaterBook directory to query.</summary>
    public enum RepeaterBookService { Amateur, Gmrs }

    /// <summary>Search criteria. Empty/null fields are omitted from the request.</summary>
    public sealed class RepeaterBookQuery
    {
        public RepeaterBookService Service = RepeaterBookService.Amateur;
        public string Country;     // e.g. "United States" — also picks NA vs ROW endpoint
        public string State;       // state/province
        public string County;
        public string City;
        public string Callsign;
        public string Frequency;   // MHz, optional exact match
        public string Mode;        // e.g. "analog", "DMR" (amateur only)
    }

    /// <summary>Raised for HTTP-level failures the UI should surface verbatim.</summary>
    public sealed class RepeaterBookException : Exception
    {
        public bool RateLimited { get; }
        public RepeaterBookException(string message, bool rateLimited = false) : base(message) { RateLimited = rateLimited; }
    }

    /// <summary>
    /// One row from a RepeaterBook export. Field names mirror the API JSON (which
    /// returns every value as a string). Only the fields we map are declared.
    /// </summary>
    public sealed class RepeaterBookResult
    {
        [JsonPropertyName("Frequency")] public string Frequency { get; set; }      // output (repeater TX / your RX), MHz
        [JsonPropertyName("Input Freq")] public string InputFreq { get; set; }     // input  (repeater RX / your TX), MHz
        [JsonPropertyName("PL")] public string Pl { get; set; }                    // uplink CTCSS/DCS — what you transmit
        [JsonPropertyName("TSQ")] public string Tsq { get; set; }                  // downlink tone (ignored: RX squelch off)
        [JsonPropertyName("Nearest City")] public string NearestCity { get; set; }
        [JsonPropertyName("Landmark")] public string Landmark { get; set; }
        [JsonPropertyName("County")] public string County { get; set; }
        [JsonPropertyName("State")] public string State { get; set; }
        [JsonPropertyName("Country")] public string Country { get; set; }
        [JsonPropertyName("Callsign")] public string Callsign { get; set; }
        [JsonPropertyName("Lat")] public string Lat { get; set; }
        [JsonPropertyName("Long")] public string Long { get; set; }
        [JsonPropertyName("FM Analog")] public string FmAnalog { get; set; }
        [JsonPropertyName("DMR")] public string Dmr { get; set; }
        [JsonPropertyName("Operational Status")] public string OperationalStatus { get; set; }
    }

    internal sealed class RepeaterBookResponse
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("results")] public List<RepeaterBookResult> Results { get; set; }
    }

    /// <summary>
    /// Fetches repeaters from RepeaterBook's HTTP/JSON API and maps them onto
    /// <see cref="RadioChannelInfo"/>. The fetch is desktop-only; the mapper
    /// (<see cref="ToRadioChannelInfo"/>) is shared so Android can reuse it.
    /// </summary>
    public sealed class RepeaterBookClient
    {
        public const string UserAgent = "HTCommander (+https://github.com/mprattmd/HTCommander; mprattmd@gmail.com)";

        private const string AmateurNorthAmerica = "https://www.repeaterbook.com/api/export.php";
        private const string AmateurRestOfWorld = "https://www.repeaterbook.com/api/exportROW.php";
        private const string Gmrs = "https://www.repeaterbook.com/api/exportgmrs.php";

        private static readonly HashSet<string> NorthAmerica = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "United States", "USA", "US", "Canada", "Mexico",
        };

        private readonly HttpClient _http;

        public RepeaterBookClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>Runs a search. Throws <see cref="RepeaterBookException"/> on HTTP failure (incl. 429).</summary>
        public async Task<RepeaterBookResult[]> SearchAsync(RepeaterBookQuery query, string appToken, CancellationToken ct = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (string.IsNullOrWhiteSpace(appToken))
                throw new RepeaterBookException("No RepeaterBook API token — set one in Settings.");

            string url = BuildUrl(query);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("X-RB-App-Token", appToken);

            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new RepeaterBookException($"Network error contacting RepeaterBook: {ex.Message}"); }

            using (resp)
            {
                if (resp.StatusCode == (HttpStatusCode)429)
                    throw new RepeaterBookException("RepeaterBook is rate-limiting — try again shortly.", rateLimited: true);
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new RepeaterBookException("RepeaterBook rejected the API token — check it in Settings.");
                if (!resp.IsSuccessStatusCode)
                    throw new RepeaterBookException($"RepeaterBook returned HTTP {(int)resp.StatusCode}.");

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(body);
            }
        }

        public static RepeaterBookResult[] Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return Array.Empty<RepeaterBookResult>();
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };
            try
            {
                var parsed = JsonSerializer.Deserialize<RepeaterBookResponse>(body, opts);
                return parsed?.Results?.ToArray() ?? Array.Empty<RepeaterBookResult>();
            }
            catch (JsonException ex)
            {
                throw new RepeaterBookException($"Could not parse RepeaterBook response: {ex.Message}");
            }
        }

        public static string BuildUrl(RepeaterBookQuery q)
        {
            string baseUrl = q.Service == RepeaterBookService.Gmrs
                ? Gmrs
                : (IsNorthAmerica(q.Country) ? AmateurNorthAmerica : AmateurRestOfWorld);

            var sb = new StringBuilder(baseUrl);
            char sep = '?';
            void Add(string key, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
                sep = '&';
            }

            Add("country", q.Country);
            Add("state_id", q.State);
            Add("county", q.County);
            Add("city", q.City);
            Add("callsign", q.Callsign);
            Add("frequency", q.Frequency);
            if (q.Service == RepeaterBookService.Amateur) Add("mode", q.Mode);   // GMRS has no mode param
            return sb.ToString();
        }

        private static bool IsNorthAmerica(string country)
            => string.IsNullOrWhiteSpace(country) || NorthAmerica.Contains(country.Trim());

        /// <summary>
        /// Maps a RepeaterBook row to a radio channel.
        ///   • output freq → RX, input freq → TX (standard repeater convention);
        ///   • uplink PL/DCS → tx_sub_audio (CTCSS Hz×100, or DCS code as int);
        ///   • RX tone squelch left OFF by decision (rx_sub_audio = 0);
        ///   • name = callsign, else nearest city, truncated to the radio's 10 chars.
        /// Returns null when there is no usable frequency.
        /// </summary>
        public static RadioChannelInfo ToRadioChannelInfo(RepeaterBookResult r)
        {
            if (r == null) return null;

            int rxHz = MHzToHz(r.Frequency);   // you receive on the repeater's output
            int txHz = MHzToHz(r.InputFreq);   // you transmit on the repeater's input
            if (rxHz == 0) rxHz = txHz;
            if (txHz == 0) txHz = rxHz;         // simplex / missing input — flag for review
            if (rxHz == 0 && txHz == 0) return null;

            var c = new RadioChannelInfo
            {
                channel_id = 0,
                rx_freq = rxHz,
                tx_freq = txHz,
                rx_sub_audio = 0,                       // RX squelch OFF (decision 2026-06-14)
                tx_sub_audio = ParseTone(r.Pl),          // uplink tone you transmit
                scan = false,
                tx_disable = false,
                mute = false,
                tx_at_max_power = true,
                tx_at_med_power = false,
                talk_around = false,
                pre_de_emph_bypass = false,
            };

            bool dmr = string.Equals(r.Dmr, "Yes", StringComparison.OrdinalIgnoreCase);
            bool fm = string.Equals(r.FmAnalog, "Yes", StringComparison.OrdinalIgnoreCase) || !dmr;
            if (dmr && !fm) { c.rx_mod = RadioModulationType.DMR; c.bandwidth = RadioBandwidthType.NARROW; }
            else { c.rx_mod = RadioModulationType.FM; c.bandwidth = RadioBandwidthType.WIDE; }
            c.tx_mod = c.rx_mod;

            string name = !string.IsNullOrWhiteSpace(r.Callsign) ? r.Callsign
                        : !string.IsNullOrWhiteSpace(r.NearestCity) ? r.NearestCity
                        : !string.IsNullOrWhiteSpace(r.Landmark) ? r.Landmark : "RPT";
            name = name.Trim();
            c.name_str = name.Length > 10 ? name.Substring(0, 10) : name;
            return c;
        }

        private static int MHzToHz(string mhz)
        {
            if (string.IsNullOrWhiteSpace(mhz)) return 0;
            return double.TryParse(mhz.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                ? (int)Math.Round(v * 1_000_000) : 0;
        }

        /// <summary>CTCSS like "100.0" → 10000 (Hz×100). DCS like "D023" / "023" → 23. Empty → 0.</summary>
        private static int ParseTone(string pl)
        {
            if (string.IsNullOrWhiteSpace(pl)) return 0;
            pl = pl.Trim();
            if (pl.StartsWith("D", StringComparison.OrdinalIgnoreCase))   // DCS / digital code
            {
                string digits = new string(Array.FindAll(pl.ToCharArray(), char.IsDigit));
                return int.TryParse(digits, out int code) ? code : 0;
            }
            return double.TryParse(pl, NumberStyles.Any, CultureInfo.InvariantCulture, out double hz)
                ? (int)Math.Round(hz * 100) : 0;
        }
    }
}
