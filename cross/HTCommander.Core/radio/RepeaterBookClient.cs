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
            appToken = appToken.Trim();   // paste often carries a trailing newline → auth_invalid;
                                          // otherwise send verbatim — RB issues the token with its own prefix.

            string url = BuildUrl(query);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("X-RB-App-Token", appToken);

            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new RepeaterBookException($"Network error contacting RepeaterBook: {ex.Message}"); }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    // RepeaterBook returns {"ok":false,"error_code":"...","message":"..."} — surface it so the
                    // user can tell auth_invalid (bad token/format) from auth_inactive/auth_revoked (app state).
                    string errBody = "";
                    try { errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
                    (string code, string detail) = ExtractError(errBody);
                    string server = string.IsNullOrEmpty(code) ? "" :
                        $" ({code}{(string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail)})";

                    if (resp.StatusCode == (HttpStatusCode)429)
                        throw new RepeaterBookException($"RepeaterBook is rate-limiting — try again shortly.{server}", rateLimited: true);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new RepeaterBookException($"RepeaterBook rejected the API token{server} — check it in Settings.");
                    throw new RepeaterBookException($"RepeaterBook returned HTTP {(int)resp.StatusCode}{server}.");
                }

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(body);
            }
        }

        /// <summary>Reads a JSON string property that the API may encode as a string, number, or bool.</summary>
        private sealed class FlexStringConverter : JsonConverter<string>
        {
            public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String: return reader.GetString();
                    case JsonTokenType.Number:
                        return reader.TryGetInt64(out long l)
                            ? l.ToString(CultureInfo.InvariantCulture)
                            : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
                    case JsonTokenType.True: return "true";
                    case JsonTokenType.False: return "false";
                    case JsonTokenType.Null: return null;
                    default: reader.Skip(); return null;
                }
            }

            public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
                => writer.WriteStringValue(value);
        }

        /// <summary>Pulls error_code / message out of a RepeaterBook error envelope; nulls if absent/unparseable.</summary>
        private static (string code, string message) ExtractError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string code = root.TryGetProperty("error_code", out var c) ? c.GetString() : null;
                string msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }
            catch { return (null, null); }
        }

        public static RepeaterBookResult[] Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return Array.Empty<RepeaterBookResult>();
            // The API can prepend PHP warnings/HTML (e.g. "<br /><b>Warning</b>…") before the
            // JSON object. Start at the first '{' so that leading noise doesn't break parsing.
            int brace = body.IndexOf('{');
            if (brace > 0) body = body.Substring(brace);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };
            // RepeaterBook is inconsistent: a field that's a quoted string on one endpoint
            // (amateur "Lat":"35.98") comes back as a bare number on another (GMRS "Lat":35.98).
            // This converter lets every string property accept string/number/bool tokens alike.
            opts.Converters.Add(new FlexStringConverter());
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
            // GMRS is now a parameter (stype=gmrs) on the North America export endpoint —
            // the old exportgmrs.php is gone (404). GMRS is US-only, so always use export.php.
            string baseUrl = (q.Service == RepeaterBookService.Gmrs || IsNorthAmerica(q.Country))
                ? AmateurNorthAmerica
                : AmateurRestOfWorld;

            var sb = new StringBuilder(baseUrl);
            char sep = '?';
            void Add(string key, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
                sep = '&';
            }

            if (q.Service == RepeaterBookService.Gmrs) Add("stype", "gmrs");
            Add("country", q.Country);
            Add("state_id", ResolveStateId(q.State));   // RepeaterBook wants the numeric FIPS code, not the name
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
        /// RepeaterBook's <c>state_id</c> is the numeric US-state FIPS code (e.g. Virginia = 51).
        /// Accepts a full state name ("Tennessee") or 2-letter abbreviation ("TN") and returns the
        /// code. A value that is already numeric, or any unrecognized text, is passed through
        /// unchanged so non-US queries and direct IDs still work.
        /// </summary>
        public static string ResolveStateId(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return null;
            string s = state.Trim();
            if (s.Length > 0 && Array.TrueForAll(s.ToCharArray(), char.IsDigit)) return s;  // already an ID
            return UsStateFips.TryGetValue(s, out string code) ? code : s;
        }

        // US state/territory FIPS codes, keyed by full name and 2-letter abbreviation (case-insensitive).
        private static readonly Dictionary<string, string> UsStateFips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alabama"] = "01", ["AL"] = "01", ["Alaska"] = "02", ["AK"] = "02",
            ["Arizona"] = "04", ["AZ"] = "04", ["Arkansas"] = "05", ["AR"] = "05",
            ["California"] = "06", ["CA"] = "06", ["Colorado"] = "08", ["CO"] = "08",
            ["Connecticut"] = "09", ["CT"] = "09", ["Delaware"] = "10", ["DE"] = "10",
            ["District of Columbia"] = "11", ["DC"] = "11", ["Florida"] = "12", ["FL"] = "12",
            ["Georgia"] = "13", ["GA"] = "13", ["Hawaii"] = "15", ["HI"] = "15",
            ["Idaho"] = "16", ["ID"] = "16", ["Illinois"] = "17", ["IL"] = "17",
            ["Indiana"] = "18", ["IN"] = "18", ["Iowa"] = "19", ["IA"] = "19",
            ["Kansas"] = "20", ["KS"] = "20", ["Kentucky"] = "21", ["KY"] = "21",
            ["Louisiana"] = "22", ["LA"] = "22", ["Maine"] = "23", ["ME"] = "23",
            ["Maryland"] = "24", ["MD"] = "24", ["Massachusetts"] = "25", ["MA"] = "25",
            ["Michigan"] = "26", ["MI"] = "26", ["Minnesota"] = "27", ["MN"] = "27",
            ["Mississippi"] = "28", ["MS"] = "28", ["Missouri"] = "29", ["MO"] = "29",
            ["Montana"] = "30", ["MT"] = "30", ["Nebraska"] = "31", ["NE"] = "31",
            ["Nevada"] = "32", ["NV"] = "32", ["New Hampshire"] = "33", ["NH"] = "33",
            ["New Jersey"] = "34", ["NJ"] = "34", ["New Mexico"] = "35", ["NM"] = "35",
            ["New York"] = "36", ["NY"] = "36", ["North Carolina"] = "37", ["NC"] = "37",
            ["North Dakota"] = "38", ["ND"] = "38", ["Ohio"] = "39", ["OH"] = "39",
            ["Oklahoma"] = "40", ["OK"] = "40", ["Oregon"] = "41", ["OR"] = "41",
            ["Pennsylvania"] = "42", ["PA"] = "42", ["Rhode Island"] = "44", ["RI"] = "44",
            ["South Carolina"] = "45", ["SC"] = "45", ["South Dakota"] = "46", ["SD"] = "46",
            ["Tennessee"] = "47", ["TN"] = "47", ["Texas"] = "48", ["TX"] = "48",
            ["Utah"] = "49", ["UT"] = "49", ["Vermont"] = "50", ["VT"] = "50",
            ["Virginia"] = "51", ["VA"] = "51", ["Washington"] = "53", ["WA"] = "53",
            ["West Virginia"] = "54", ["WV"] = "54", ["Wisconsin"] = "55", ["WI"] = "55",
            ["Wyoming"] = "56", ["WY"] = "56",
            ["American Samoa"] = "60", ["AS"] = "60", ["Guam"] = "66", ["GU"] = "66",
            ["Northern Mariana Islands"] = "69", ["MP"] = "69", ["Puerto Rico"] = "72", ["PR"] = "72",
            ["Virgin Islands"] = "78", ["VI"] = "78",
        };

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
