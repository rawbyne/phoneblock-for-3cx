#nullable disable
using CallFlow;
using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using TCX.Configuration;
using TCX.PBXAPI;

//
// ----------------------------------------------------------------------------
//  BlockSpamViaPhoneBlock – 3CX v20 Script
//  Zweck:
//    - Prüft eingehende Anrufe gegen die phoneblock.net API.
//    - Blockiert den Anruf, wenn genug negative Bewertungen vorliegen.
//    - (Optional) Benachrichtigt per Discord/Generic Webhook.
//
//  Hinweise / Datenschutz:
//    - API-Token und Webhook-URLs sind ANONYMISIERT und müssen durch eigene
//      Werte ersetzt werden.
//    - Logging ist bewusst zurückhaltend; passe es bei Bedarf an.
//    - JSON wird minimal via Regex geparst (API-Response ist klein).
//
//  Konfiguration:
//    - BEARER:   Dein phoneblock.net API-Token (Bearer Token)
//    - MIN_VOTES: Anzahl benötigter Stimmen für eine Sperre
//    - NEGATIVE:  Bewertungs-Codes, die als negativ gelten (siehe Liste)
//    - DISCORD_WEBHOOK / GENERIC_WEBHOOK: leer lassen, wenn nicht genutzt
//    - HTTP_TIMEOUT_SEC: HTTP-Timeout für API/Webhooks
//
//  Tested with: 3CX v20 ScriptBase<T>, .NET-kompatible Umgebung
// ----------------------------------------------------------------------------
namespace phoneblock_block
{
    public class BlockSpamViaPhoneBlock : ScriptBase<BlockSpamViaPhoneBlock>
    {
        // Basis-URL der phoneblock.net API
        const string API_BASE = "https://phoneblock.net/phoneblock/api";

        // >>> ANONYMISIERT: HIER DEIN EIGENES TOKEN EINTRAGEN <<<
        // Beispiel: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        const string BEARER   = "<PHONEBLOCK_API_TOKEN>";

        // Schwellwert: ab wie vielen Stimmen (votes) sperren?
        const int MIN_VOTES   = 4;

        // (Optional) Webhooks – leer lassen, wenn keine Benachrichtigungen gewünscht
        const string DISCORD_WEBHOOK = "";      // z.B. "https://discord.com/api/webhooks/…"
        const string GENERIC_WEBHOOK = "";      // z.B. "https://example.com/webhook"
        const int HTTP_TIMEOUT_SEC   = 6;       // konservatives Timeout

        // Bewertungs-Codes, die als "negativ" gewertet werden (phoneblock Kategorien)
        // B_MISSED=verpasste Spam-Versuche, C_PING=Ping-Call, D_POLL=Umfrage, E_ADVERTISING=Werbung, F_GAMBLE=Glücksspiel, G_FRAUD=Betrug
        static readonly HashSet<string> NEGATIVE = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "B_MISSED","C_PING","D_POLL","E_ADVERTISING","F_GAMBLE","G_FRAUD" };

        // Haupteinstieg: wird pro Call aufgerufen
        public override async Task<bool> StartAsync()
        {
            try
            {
                // Original-CLI (Caller ID) und Durchwahl (DID) vom Call-Objekt
                var cli  = MyCall.Caller?.CallerID ?? "";
                var did  = MyCall.Caller?.CalledNumber ?? "";

                // Normalisierung des CallerID-Formats auf E.164 (z.B. +49123456789)
                var e164 = NormalizeToE164(cli);

                // Logging des Startzustands (keine sensiblen Tokens/URLs im Log!)
                MyCall.Info($"PhoneBlock START cli={cli} did={did} e164={e164}");

                // Nur eingehende Anrufe mit verwertbarer Nummer prüfen
                if (!MyCall.IsInbound || string.IsNullOrWhiteSpace(e164))
                {
                    await NotifyAllAsync("no_callerid_or_not_inbound", e164, 0, "", did);
                    return false; // Weiter im Flow (nicht blocken)
                }

                // Abfrage bei phoneblock.net (probiert mehrere Nummernformate)
                var (ok, votes, rating, raw) = await LookupAsync(e164);
                if (!ok)
                {
                    // Bei API-Fehler: Hinweis + optional benachrichtigen
                    MyCall.Info($"PhoneBlock LOOKUP_FAIL {e164} body={raw}");
                    await NotifyAllAsync("lookup_failed", e164, 0, "", did);
                    return false; // Weiter im Flow
                }

                // Entscheidung: ab MIN_VOTES und negativer Bewertung blocken
                bool isBlocklisted = votes >= MIN_VOTES && NEGATIVE.Contains(rating ?? "");
                if (isBlocklisted)
                {
                    MyCall.Info($"PhoneBlock BLOCK {e164} rating={rating} votes={votes} -> Terminate()");
                    await NotifyAllAsync("blocked", e164, votes, rating, did);

                    // Sicher und sofort auflegen (keine Route/Parsing-Probleme)
                    MyCall.Terminate();
                    return true; // Call wurde final behandelt
                }

                // Kein Block: Status protokollieren + benachrichtigen (optional)
                var state = votes > 0 ? "allowed_listed" : "allowed_not_listed";
                MyCall.Info($"PhoneBlock {state.ToUpper()} {e164} rating={rating} votes={votes}");
                await NotifyAllAsync(state, e164, votes, rating, did);

                return false; // Flow unbeeinflusst fortsetzen
            }
            catch (Exception ex)
            {
                // Fehlerfall: sauber protokollieren + optionale Benachrichtigung
                MyCall.Error($"PhoneBlock exception: {ex}");
                await NotifyAllAsync("error", "", 0, "", "");
                return false;
            }
        }

        // ---------------------------
        // API-Lookup bei phoneblock.net
        // - probiert mehrere Kandidaten (E.164 ohne '+' und DE national '0…')
        // ---------------------------
        async Task<(bool ok,int votes,string rating,string raw)> LookupAsync(string e164)
        {
            foreach (var candidate in BuildLookupCandidates(e164))
            {
                var url = API_BASE + "/num/" + Uri.EscapeDataString(candidate) + "?format=json";

                try
                {
                    using var http = new HttpClient(){ Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SEC) };
                    using var req  = new HttpRequestMessage(HttpMethod.Get, url);

                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BEARER);
                    req.Headers.Accept.ParseAdd("application/json");

                    var resp = await http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();

                    MyCall.Info($"PhoneBlock LOOKUP try='{candidate}' status={(int)resp.StatusCode}");

                    if (!resp.IsSuccessStatusCode) continue;

                    // Minimales Parsen via Regex (API liefert klein und flach):
                    //   {"votes":5,"rating":"G_FRAUD", ...}
                    int votes = 0; string rating = "";
                    var mVotes  = Regex.Match(body, "\"votes\"\\s*:\\s*(\\d+)");
                    if (mVotes.Success) int.TryParse(mVotes.Groups[1].Value, out votes);
                    var mRating = Regex.Match(body, "\"rating\"\\s*:\\s*\"([^\"]+)\"");
                    if (mRating.Success) rating = mRating.Groups[1].Value;

                    return (true, votes, rating, body);
                }
                catch (Exception ex)
                {
                    MyCall.Info($"PhoneBlock LOOKUP EXC try='{candidate}': {ex.Message}");
                    // weiter mit nächstem Kandidaten
                }
            }

            // Kein Kandidat erfolgreich
            return (false, 0, "", "");
        }

        // Baut sinnvolle Nummern-Kandidaten aus E.164:
        //  - E.164 ohne '+' (49…)
        //  - DE national 0… (aus 49… -> 0…)
        static IEnumerable<string> BuildLookupCandidates(string e164)
        {
            if (string.IsNullOrWhiteSpace(e164)) yield break;

            var plain = e164.StartsWith("+") ? e164.Substring(1) : e164;
            yield return plain; // 49…

            if (plain.StartsWith("49") && plain.Length > 2)
                yield return "0" + plain.Substring(2); // 0…
        }

        // Benachrichtigt (falls konfiguriert) alle Ziele
        async Task NotifyAllAsync(string state, string number, int votes, string rating, string did)
        {
            await NotifyDiscordAsync(state, number, votes, rating, did);
            await NotifyGenericAsync(state, number, votes, rating, did);
        }

        // ---------------------------
        // Discord Webhook (optional)
        // ---------------------------
        async Task NotifyDiscordAsync(string state, string number, int votes, string rating, string did)
        {
            if (string.IsNullOrWhiteSpace(DISCORD_WEBHOOK)) return;

            try
            {
                using var http = new HttpClient(){ Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SEC) };

                string contentText = $"**PhoneBlock** `{state}`\n" +
                                     $"• Nummer: `{number}`\n" +
                                     $"• Votes: `{votes}`\n" +
                                     $"• Rating: `{rating}`\n" +
                                     $"• DID: `{did}`\n" +
                                     $"• TS: `{DateTime.UtcNow:o}`";

                // Farbcode: Rot bei Block, Grün sonst
                string embedJson =
                    "{"
                    + $"\"title\":\"{JsonEscape(state.ToUpper())}\","
                    + $"\"color\":{(state == "blocked" ? 16711680 : 3066993)},"
                    + "\"fields\":["
                    + $"{{\"name\":\"Nummer\",\"value\":\"`{JsonEscape(number)}`\",\"inline\":true}},"
                    + $"{{\"name\":\"Votes\",\"value\":\"`{votes}`\",\"inline\":true}},"
                    + $"{{\"name\":\"Rating\",\"value\":\"`{JsonEscape(rating)}`\",\"inline\":true}},"
                    + $"{{\"name\":\"DID\",\"value\":\"`{JsonEscape(did)}`\",\"inline\":true}},"
                    + $"{{\"name\":\"TS\",\"value\":\"`{DateTime.UtcNow:o}`\",\"inline\":false}}"
                    + "]"
                    + "}";

                string payload = "{"
                    + $"\"username\":\"3CX PhoneBlock\","
                    + $"\"content\":\"{JsonEscape(contentText)}\","
                    + $"\"embeds\":[{embedJson}]"
                    + "}";

                var resp = await http.PostAsync(DISCORD_WEBHOOK, new StringContent(payload, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                int code = (int)resp.StatusCode;

                if (code == 200 || code == 204)
                {
                    MyCall.Info($"PhoneBlock Discord webhook OK ({code}).");
                    return;
                }

                // Fallback: nur content ohne Embed bei z.B. 400 Bad Request
                MyCall.Info($"PhoneBlock Discord webhook FAIL {code} {resp.StatusCode}: {body}");

                if (code == 400)
                {
                    string fallback = "{\"content\":\"" + JsonEscape(contentText) + "\"}";
                    var resp2 = await http.PostAsync(DISCORD_WEBHOOK, new StringContent(fallback, Encoding.UTF8, "application/json"));
                    var body2 = await resp2.Content.ReadAsStringAsync();
                    int code2 = (int)resp2.StatusCode;
                    if (code2 == 200 || code2 == 204)
                        MyCall.Info($"PhoneBlock Discord webhook FALLBACK OK ({code2}).");
                    else
                        MyCall.Info($"PhoneBlock Discord webhook FALLBACK FAIL {code2} {resp2.StatusCode}: {body2}");
                }
            }
            catch (Exception ex)
            {
                // Exceptions bewusst nur als Info (kein Call-Abbruch)
                MyCall.Info($"PhoneBlock Discord webhook EXC: {ex.Message}");
            }
        }

        // ---------------------------
        // Generic JSON Webhook (optional)
        // ---------------------------
        async Task NotifyGenericAsync(string state, string number, int votes, string rating, string did)
        {
            if (string.IsNullOrWhiteSpace(GENERIC_WEBHOOK)) return;

            try
            {
                using var http = new HttpClient(){ Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SEC) };

                // Kompaktes JSON für generische Hooks
                var json = $"{{\"state\":\"{JsonEscape(state)}\",\"number\":\"{JsonEscape(number)}\",\"votes\":{votes},\"rating\":\"{JsonEscape(rating)}\",\"did\":\"{JsonEscape(did)}\",\"ts\":\"{DateTime.UtcNow:o}\"}}";

                var resp = await http.PostAsync(GENERIC_WEBHOOK, new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    MyCall.Info($"PhoneBlock generic webhook FAIL {(int)resp.StatusCode} {resp.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                MyCall.Info($"PhoneBlock generic webhook EXC: {ex.Message}");
            }
        }

        // Einfache JSON-Escape-Hilfsfunktion für eigene Payloads
        static string JsonEscape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32) sb.Append("\\u" + ((int)ch).ToString("X4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        // Normalisiert Rufnummern grob auf E.164.
        // Regeln:
        //  - bereits mit '+': unverändert
        //  - 00…  -> '+' ersetzen
        //  - 0…   -> als deutsche Nummer interpretieren (+49)
        //  - sonst: führendes '+' ergänzen
        static string NormalizeToE164(string input)
        {
            var cleaned = Regex.Replace(input ?? "", @"[^\d+]", "");
            if (cleaned.Length == 0) return "";
            if (cleaned.StartsWith("+"))  return cleaned;
            if (cleaned.StartsWith("00")) return "+" + cleaned.Substring(2);
            if (cleaned.StartsWith("0"))  return "+49" + cleaned.Substring(1);
            return "+" + cleaned;
        }
    }
}
