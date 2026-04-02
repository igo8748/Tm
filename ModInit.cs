using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models.Module;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot
{
    public class ModInit
    {
        #region Static data
        public static BotConfig Config { get; private set; }
        public static ConcurrentDictionary<long, TgUser> Users { get; private set; } = new();
        public static ConcurrentDictionary<string, List<Subscription>> Subs { get; private set; } = new();
        public static TelegramBotClient Bot { get; private set; }
        public static bool IsRunning { get; private set; }

        static readonly string configPath = "module/TelegramBot/config.json";
        static readonly string subscriptionsPath = "module/TelegramBot/subscriptions.json";
        static readonly string usersPath = "module/TelegramBot/users.json";
        static readonly HttpClient http = new HttpClient();
        static Timer checkTimer;
        #endregion

        #region Models
        public class BotConfig
        {
            public string bot_token { get; set; } = "YOUR_BOT_TOKEN";
            public string tmdb_api_key { get; set; } = "4ef0d7355d9ffb5151e987764708ce96";
            public string trakt_client_id { get; set; } = "";
            public string lampac_host { get; set; } = "http://127.0.0.1:9715";
            public string lampac_token { get; set; } = "";
            public int check_interval_minutes { get; set; } = 60;
            public string tmdb_lang { get; set; } = "ru-RU";
        }

        public class TgUser
        {
            public long chat_id { get; set; }
            public string username { get; set; }
            public string lampac_uid { get; set; }
            public DateTime linked_at { get; set; }
        }

        public class Subscription
        {
            public long chat_id { get; set; }
            public int tmdb_id { get; set; }
            public string media_type { get; set; } = "tv";
            public string title { get; set; }
            public string voice { get; set; }         // Озвучка: "LostFilm", "HDrezka Studio" и т.д. Пусто = любая
            public string voice_source { get; set; }  // "mirage" или "collaps"
            public string mirage_orid { get; set; }   // ID сериала в Mirage
            public int mirage_voice_id { get; set; }  // ID озвучки (параметр t=)
            public string collaps_orid { get; set; }  // ID сериала в Collaps
            public int last_season { get; set; }
            public int last_episode { get; set; }
            public int last_voice_episode { get; set; } // Последняя серия в озвучке
            public DateTime subscribed_at { get; set; }
        }
        #endregion

        public static void loaded(InitspaceModel conf)
        {
            Directory.CreateDirectory("module/TelegramBot");
            LoadConfig(); LoadUsers(); LoadSubscriptions();

            if (Config.bot_token == "YOUR_BOT_TOKEN")
            {
                Console.WriteLine("\n\t[TelegramBot] Установите bot_token в module/TelegramBot/config.json\n");
                SaveConfig();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var cts = new CancellationTokenSource();
                    Bot = new TelegramBotClient(Config.bot_token, cancellationToken: cts.Token);
                    var me = await Bot.GetMe();
                    IsRunning = true;
                    Console.WriteLine($"\n\t[TelegramBot] @{me.Username} запущен!\n");

                    checkTimer = new Timer(async _ =>
                    {
                        try { await CheckAll(); }
                        catch (Exception ex) { Console.WriteLine($"[TelegramBot] Timer error: {ex.Message}"); }
                    }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(Config.check_interval_minutes));

                    Console.WriteLine("[TelegramBot] Starting polling loop...");
                    await HandleUpdates(cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TelegramBot] Fatal: {ex}\n");
                    IsRunning = false;
                }
            });
        }

        #region Telegram polling
        static async Task HandleUpdates(CancellationToken ct)
        {
            int offset = 0;
            Console.WriteLine("[TelegramBot] HandleUpdates started");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var updates = await Bot.GetUpdates(offset, timeout: 30, cancellationToken: ct);
                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;
                        try
                        {
                            if (update.Message?.Text != null)
                            {
                                Console.WriteLine($"[TelegramBot] Msg: {update.Message.Text}");
                                await HandleMessage(update.Message);
                            }
                            else if (update.CallbackQuery != null)
                                await HandleCallback(update.CallbackQuery);
                        }
                        catch (Exception ex) { Console.WriteLine($"[TelegramBot] Handle error: {ex}"); }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TelegramBot] Poll error: {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
        }

        static async Task HandleMessage(Message msg)
        {
            var chatId = msg.Chat.Id;
            var text = msg.Text.Trim();

            if (text.StartsWith("/start"))
            {
                var parts = text.Split(' ');
                if (parts.Length > 1 && parts[1].StartsWith("link_"))
                {
                    var uid = parts[1].Substring(5);
                    Users[chatId] = new TgUser { chat_id = chatId, username = msg.From?.Username ?? "", lampac_uid = uid, linked_at = DateTime.UtcNow };
                    SaveUsers();
                    await Bot.SendMessage(chatId, "✅ *Аккаунт привязан!*\n\n/list — подписки\n/check — проверить\n/help — помощь", parseMode: ParseMode.Markdown);
                    return;
                }
                await Bot.SendMessage(chatId, "🎬 *Lampa Notifications Bot*\n\nУведомления о новых сериях и озвучках.\n\n/list — подписки\n/check — проверить\n/help — помощь", parseMode: ParseMode.Markdown);
            }
            else if (text == "/list") await ShowSubscriptions(chatId);
            else if (text == "/check")
            {
                Console.WriteLine($"[TelegramBot] /check from {chatId}");
                try
                {
                    await Bot.SendMessage(chatId, "🔍 Проверяю...");
                    await CheckAll(chatId);
                }
                catch (Exception ex) { Console.WriteLine($"[TelegramBot] /check error: {ex}"); }
            }
            else if (text == "/help")
            {
                await Bot.SendMessage(chatId,
                    "📖 *Помощь*\n\n" +
                    "• Карточка сериала → 🔔 → выберите озвучку\n" +
                    "• Бот пришлёт уведомление когда появится серия в этой озвучке\n" +
                    "• Или подпишитесь на «Любая озвучка» — уведомление при выходе оригинала\n\n" +
                    "/list — подписки\n/check — проверить\n/unlink — отвязать",
                    parseMode: ParseMode.Markdown);
            }
            else if (text == "/unlink")
            {
                if (Users.TryRemove(chatId, out _))
                {
                    foreach (var kvp in Subs) kvp.Value.RemoveAll(s => s.chat_id == chatId);
                    SaveUsers(); SaveSubscriptions();
                    await Bot.SendMessage(chatId, "🔓 Аккаунт отвязан.");
                }
            }
        }

        static async Task HandleCallback(CallbackQuery cb)
        {
            if (cb.Data.StartsWith("unsub_"))
            {
                var tmdbId = cb.Data.Substring(6);
                if (Subs.TryGetValue(tmdbId, out var list) && list.RemoveAll(s => s.chat_id == cb.Message.Chat.Id) > 0)
                {
                    SaveSubscriptions();
                    await Bot.AnswerCallbackQuery(cb.Id, "Удалено ✅");
                    await ShowSubscriptions(cb.Message.Chat.Id);
                    return;
                }
                await Bot.AnswerCallbackQuery(cb.Id, "Не найдено");
            }
        }

        static async Task ShowSubscriptions(long chatId)
        {
            var userSubs = Subs.SelectMany(kvp => kvp.Value).Where(s => s.chat_id == chatId).OrderByDescending(s => s.subscribed_at).ToList();
            if (userSubs.Count == 0) { await Bot.SendMessage(chatId, "📋 Нет подписок."); return; }

            var text = "📋 *Ваши подписки:*\n\n";
            var buttons = new List<List<InlineKeyboardButton>>();
            foreach (var sub in userSubs)
            {
                var v = string.IsNullOrEmpty(sub.voice) ? "Любая озвучка" : sub.voice;
                text += $"• *{EscapeMd(sub.title)}* — {v}\n  S{sub.last_season:D2}E{sub.last_episode:D2}";
                if (sub.last_voice_episode > 0) text += $" (озв: E{sub.last_voice_episode:D2})";
                text += "\n";
                buttons.Add(new List<InlineKeyboardButton> {
                    InlineKeyboardButton.WithCallbackData($"❌ {sub.title} ({v})", $"unsub_{sub.tmdb_id}")
                });
            }
            await Bot.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: new InlineKeyboardMarkup(buttons));
        }
        #endregion

        #region Проверка — главная
        public static async Task CheckAll(long? onlyChatId = null)
        {
            Console.WriteLine($"[TelegramBot] CheckAll started, onlyChatId={onlyChatId}");
            var allSubs = Subs.SelectMany(kvp => kvp.Value).ToList();
            if (onlyChatId.HasValue) allSubs = allSubs.Where(s => s.chat_id == onlyChatId.Value).ToList();

            int notified = 0;

            foreach (var sub in allSubs.Where(s => s.media_type == "tv"))
            {
                try
                {
                    bool hasVoice = !string.IsNullOrEmpty(sub.voice);
                    bool hasMirage = !string.IsNullOrEmpty(sub.mirage_orid);
                    bool hasCollaps = !string.IsNullOrEmpty(sub.collaps_orid);

                    // 1. Проверить новые серии (Trakt/TMDB)
                    var newEpisodes = await CheckNewEpisodes(sub);
                    foreach (var ep in newEpisodes)
                    {
                        if (!hasVoice)
                        {
                            await SendEpisodeNotification(sub, ep);
                            notified++;
                        }
                        sub.last_season = ep.season;
                        sub.last_episode = ep.episode;
                    }

                    // 2. Проверить озвучки
                    if (hasVoice && (hasMirage || hasCollaps))
                    {
                        int voiceEps;

                        if (sub.voice_source == "collaps" && hasCollaps)
                            voiceEps = await CheckCollapsVoice(sub);
                        else if (hasMirage)
                            voiceEps = await CheckMirageVoice(sub);
                        else
                            voiceEps = await CheckCollapsVoice(sub);

                        if (voiceEps > sub.last_voice_episode)
                        {
                            int fromEp = sub.last_voice_episode + 1;
                            int toEp = voiceEps;

                            for (int e = fromEp; e <= toEp; e++)
                            {
                                var msg = $"🎬 *{EscapeMd(sub.title)}*\n🎙 {EscapeMd(sub.voice)}\n📺 S{sub.last_season:D2}E{e:D2}";
                                string imgUrl = await GetTmdbStill(sub.tmdb_id, sub.last_season, e);

                                try
                                {
                                    if (!string.IsNullOrEmpty(imgUrl))
                                        await Bot.SendPhoto(sub.chat_id, InputFile.FromUri(imgUrl), caption: msg, parseMode: ParseMode.Markdown);
                                    else
                                        await Bot.SendMessage(sub.chat_id, msg, parseMode: ParseMode.Markdown);
                                    notified++;
                                }
                                catch (Exception ex) { Console.WriteLine($"[TelegramBot] Send voice error: {ex.Message}"); }
                            }

                            sub.last_voice_episode = voiceEps;
                            Console.WriteLine($"[TelegramBot] Voice update: {sub.title} {sub.voice} now E{voiceEps}");
                        }
                    }

                    await Task.Delay(500);
                }
                catch (Exception ex) { Console.WriteLine($"[TelegramBot] Check error {sub.tmdb_id}: {ex.Message}"); }
            }

            SaveSubscriptions();
            Console.WriteLine($"[TelegramBot] CheckAll done, notified={notified}");

            if (onlyChatId.HasValue && Bot != null)
                await Bot.SendMessage(onlyChatId.Value, notified > 0 ? $"✅ Уведомлений: {notified}" : "Новых серий/озвучек не найдено.");
        }

        static async Task SendEpisodeNotification(Subscription sub, EpisodeInfo ep)
        {
            var voice = string.IsNullOrEmpty(sub.voice) ? "" : $"\n🎙 {EscapeMd(sub.voice)}";
            var message = $"🎬 *{EscapeMd(ep.show_name)}*\n📺 S{ep.season:D2}E{ep.episode:D2}";
            if (!string.IsNullOrEmpty(ep.title)) message += $" — _{EscapeMd(ep.title)}_";
            message += voice + $"\n📅 {ep.air_date}";

            try
            {
                if (!string.IsNullOrEmpty(ep.image_url))
                    await Bot.SendPhoto(sub.chat_id, InputFile.FromUri(ep.image_url), caption: message, parseMode: ParseMode.Markdown);
                else
                    await Bot.SendMessage(sub.chat_id, message, parseMode: ParseMode.Markdown);
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] Send error: {ex.Message}"); }
        }
        #endregion

        #region Проверка новых эпизодов (Trakt → TMDB)
        class EpisodeInfo
        {
            public string show_name { get; set; }
            public int season { get; set; }
            public int episode { get; set; }
            public string title { get; set; }
            public string air_date { get; set; }
            public string overview { get; set; }
            public string image_url { get; set; }
        }

        static async Task<List<EpisodeInfo>> CheckNewEpisodes(Subscription sub)
        {
            bool useTrakt = !string.IsNullOrEmpty(Config.trakt_client_id);
            try
            {
                return useTrakt ? await CheckViaTrakt(sub) : await CheckViaTmdb(sub);
            }
            catch
            {
                if (useTrakt) try { return await CheckViaTmdb(sub); } catch { }
                return new List<EpisodeInfo>();
            }
        }

        static async Task<List<EpisodeInfo>> CheckViaTrakt(Subscription sub)
        {
            var result = new List<EpisodeInfo>();

            var searchReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/search/tmdb/{sub.tmdb_id}?type=show");
            searchReq.Headers.Add("trakt-api-key", Config.trakt_client_id);
            searchReq.Headers.Add("trakt-api-version", "2");
            var searchResp = await http.SendAsync(searchReq);
            if (!searchResp.IsSuccessStatusCode) return await CheckViaTmdb(sub);

            var searchData = JArray.Parse(await searchResp.Content.ReadAsStringAsync());
            if (searchData.Count == 0) return await CheckViaTmdb(sub);

            var showName = searchData[0]?["show"]?.Value<string>("title") ?? sub.title;
            var traktSlug = searchData[0]?["show"]?["ids"]?.Value<string>("slug");
            var traktId = searchData[0]?["show"]?["ids"]?.Value<string>("trakt");
            var showId = traktSlug ?? traktId;
            if (string.IsNullOrEmpty(showId)) return await CheckViaTmdb(sub);

            var seasonsReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/shows/{showId}/seasons?extended=full");
            seasonsReq.Headers.Add("trakt-api-key", Config.trakt_client_id);
            seasonsReq.Headers.Add("trakt-api-version", "2");
            var seasonsResp = await http.SendAsync(seasonsReq);
            if (!seasonsResp.IsSuccessStatusCode) return await CheckViaTmdb(sub);

            var seasons = JArray.Parse(await seasonsResp.Content.ReadAsStringAsync());
            var seasonsToCheck = seasons
                .Where(s => s.Value<int>("number") >= sub.last_season && s.Value<int>("number") > 0)
                .Where(s => s.Value<int>("aired_episodes") > 0 || s.Value<int>("episode_count") > 0)
                .Select(s => s.Value<int>("number")).OrderBy(n => n).ToList();

            foreach (var seasonNum in seasonsToCheck)
            {
                var epsReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/shows/{showId}/seasons/{seasonNum}?extended=full");
                epsReq.Headers.Add("trakt-api-key", Config.trakt_client_id);
                epsReq.Headers.Add("trakt-api-version", "2");
                var epsResp = await http.SendAsync(epsReq);
                if (!epsResp.IsSuccessStatusCode) continue;

                foreach (JObject ep in JArray.Parse(await epsResp.Content.ReadAsStringAsync()))
                {
                    int sNum = ep.Value<int>("season"), eNum = ep.Value<int>("number");
                    var firstAired = ep.Value<string>("first_aired");
                    if (sNum < sub.last_season || (sNum == sub.last_season && eNum <= sub.last_episode)) continue;
                    if (string.IsNullOrEmpty(firstAired)) continue;
                    if (!DateTime.TryParse(firstAired, null, System.Globalization.DateTimeStyles.RoundtripKind, out var airDt)) continue;
                    if (airDt > DateTime.UtcNow) continue;

                    string imageUrl = await GetTmdbStill(sub.tmdb_id, sNum, eNum);

                    result.Add(new EpisodeInfo
                    {
                        show_name = showName, season = sNum, episode = eNum,
                        title = ep.Value<string>("title") ?? "",
                        air_date = airDt.ToString("yyyy-MM-dd HH:mm UTC"),
                        overview = ep.Value<string>("overview") ?? "",
                        image_url = imageUrl
                    });
                }
                await Task.Delay(300);
            }
            return result;
        }

        static async Task<List<EpisodeInfo>> CheckViaTmdb(Subscription sub)
        {
            var result = new List<EpisodeInfo>();
            var show = JObject.Parse(await http.GetStringAsync($"https://api.themoviedb.org/3/tv/{sub.tmdb_id}?api_key={Config.tmdb_api_key}&language={Config.tmdb_lang}"));
            var lastSeason = show.Value<int>("number_of_seasons");
            var showName = show.Value<string>("name") ?? sub.title;
            var season = JObject.Parse(await http.GetStringAsync($"https://api.themoviedb.org/3/tv/{sub.tmdb_id}/season/{lastSeason}?api_key={Config.tmdb_api_key}&language={Config.tmdb_lang}"));
            var episodes = season["episodes"] as JArray;
            if (episodes == null) return result;

            foreach (JObject ep in episodes)
            {
                int sNum = ep.Value<int>("season_number"), eNum = ep.Value<int>("episode_number");
                var airDate = ep.Value<string>("air_date");
                if (sNum < sub.last_season || (sNum == sub.last_season && eNum <= sub.last_episode)) continue;
                if (string.IsNullOrEmpty(airDate) || !DateTime.TryParse(airDate, out var airDt) || airDt.Date > DateTime.UtcNow.Date) continue;

                var still = ep.Value<string>("still_path");
                result.Add(new EpisodeInfo
                {
                    show_name = showName, season = sNum, episode = eNum,
                    title = ep.Value<string>("name") ?? "", air_date = airDate,
                    image_url = !string.IsNullOrEmpty(still) ? $"https://image.tmdb.org/t/p/w500{still}" : null
                });
            }
            return result;
        }

        static async Task<string> GetTmdbStill(int tmdbId, int season, int episode)
        {
            try
            {
                var data = JObject.Parse(await http.GetStringAsync($"https://api.themoviedb.org/3/tv/{tmdbId}/season/{season}/episode/{episode}?api_key={Config.tmdb_api_key}"));
                var still = data.Value<string>("still_path");
                return !string.IsNullOrEmpty(still) ? $"https://image.tmdb.org/t/p/w500{still}" : null;
            }
            catch { return null; }
        }
        #endregion

        #region Mirage — проверка озвучек
        static async Task<int> CheckMirageVoice(Subscription sub)
        {
            if (string.IsNullOrEmpty(sub.mirage_orid) || string.IsNullOrEmpty(Config.lampac_host))
                return sub.last_voice_episode;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/mirage?rjson=False&s={sub.last_season}&t={sub.mirage_voice_id}&orid={sub.mirage_orid}{token}";

                Console.WriteLine($"[TelegramBot] Mirage check: {sub.title} voice={sub.voice} s={sub.last_season} t={sub.mirage_voice_id}");

                var html = await http.GetStringAsync(url);

                // Парсим HTML — ищем все e="N" в data атрибутах
                var matches = Regex.Matches(html, @"e=""(\d+)""");
                int maxEp = 0;
                foreach (Match m in matches)
                {
                    if (int.TryParse(m.Groups[1].Value, out var ep) && ep > maxEp)
                        maxEp = ep;
                }

                Console.WriteLine($"[TelegramBot] Mirage result: {sub.title} voice={sub.voice} episodes={maxEp} (was {sub.last_voice_episode})");
                return maxEp;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBot] Mirage error: {ex.Message}");
                return sub.last_voice_episode;
            }
        }

        // Получить список озвучек из Mirage для сериала
        public static async Task<List<VoiceInfo>> GetMirageVoices(string orid, int season)
        {
            var result = new List<VoiceInfo>();
            if (string.IsNullOrEmpty(orid) || string.IsNullOrEmpty(Config.lampac_host)) return result;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/mirage?rjson=False&s={season}&orid={orid}{token}";
                var html = await http.GetStringAsync(url);

                // Парсим кнопки озвучек: <div class="videos__button ...">NAME</div> с t=N в url
                var matches = Regex.Matches(html, @"t=(\d+)&[^""]*""[^>]*>([^<]+)</div>");
                foreach (Match m in matches)
                {
                    if (int.TryParse(m.Groups[1].Value, out var tid))
                    {
                        result.Add(new VoiceInfo { id = tid, name = m.Groups[2].Value.Trim() });
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] GetVoices error: {ex.Message}"); }

            return result;
        }

        // Поиск сериала в Mirage по названию
        public static async Task<List<MirageSearchResult>> SearchMirage(string title, int year)
        {
            var result = new List<MirageSearchResult>();
            if (string.IsNullOrEmpty(Config.lampac_host)) return result;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/mirage?title={Uri.EscapeDataString(title)}&year={year}{token}";
                var html = await http.GetStringAsync(url);

                // Парсим orid из ссылок
                var matches = Regex.Matches(html, @"orid=([a-f0-9]+)[^""]*""[^}]*""title"":""([^""]+)""");
                foreach (Match m in matches)
                {
                    result.Add(new MirageSearchResult { orid = m.Groups[1].Value, title = m.Groups[2].Value });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] SearchMirage error: {ex.Message}"); }

            return result;
        }

        public class VoiceInfo { public int id { get; set; } public string name { get; set; } public string source { get; set; } }
        public class MirageSearchResult { public string orid { get; set; } public string title { get; set; } }
        #endregion

        #region Collaps — fallback озвучки
        public static async Task<string> SearchCollaps(string title, int year)
        {
            if (string.IsNullOrEmpty(Config.lampac_host)) return null;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/collaps?title={Uri.EscapeDataString(title)}&year={year}{token}";
                var html = await http.GetStringAsync(url);

                var m = Regex.Match(html, @"orid=(\d+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] SearchCollaps error: {ex.Message}"); return null; }
        }

        public static async Task<List<VoiceInfo>> GetCollapsVoices(string collapsOrid, int season)
        {
            var result = new List<VoiceInfo>();
            if (string.IsNullOrEmpty(collapsOrid) || string.IsNullOrEmpty(Config.lampac_host)) return result;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/collaps?rjson=False&orid={collapsOrid}&s={season}{token}";
                var html = await http.GetStringAsync(url);

                // Парсим details из первого эпизода — там список озвучек
                var m = Regex.Match(html, @"""details"":""([^""]+)""");
                if (m.Success)
                {
                    var detailsRaw = m.Groups[1].Value;
                    // Декодируем Unicode escape: \u0421 -> С
                    detailsRaw = Regex.Replace(detailsRaw, @"\\u([0-9a-fA-F]{4})", match =>
                        ((char)int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).ToString());

                    var voices = detailsRaw.Split(',');
                    int id = 1;
                    foreach (var v in voices)
                    {
                        var name = v.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(name) && name.Length > 1)
                        {
                            result.Add(new VoiceInfo { id = id++, name = name, source = "collaps" });
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] GetCollapsVoices error: {ex.Message}"); }

            return result;
        }

        static async Task<int> CheckCollapsVoice(Subscription sub)
        {
            if (string.IsNullOrEmpty(sub.collaps_orid) || string.IsNullOrEmpty(Config.lampac_host))
                return sub.last_voice_episode;

            try
            {
                var token = !string.IsNullOrEmpty(Config.lampac_token) ? $"&token={Config.lampac_token}" : "";
                var url = $"{Config.lampac_host}/lite/collaps?rjson=False&orid={sub.collaps_orid}&s={sub.last_season}{token}";

                Console.WriteLine($"[TelegramBot] Collaps check: {sub.title} voice={sub.voice} s={sub.last_season}");

                var html = await http.GetStringAsync(url);

                // Парсим каждый эпизод и проверяем details на наличие озвучки
                var epMatches = Regex.Matches(html, @"e=""(\d+)""[^}]*""details"":""([^""]+)""");
                int maxEp = 0;

                foreach (Match em in epMatches)
                {
                    if (int.TryParse(em.Groups[1].Value, out var epNum))
                    {
                        var details = em.Groups[2].Value.Replace("\\u0022", "\"");
                        // Проверяем содержит ли details нашу озвучку
                        if (details.Contains(sub.voice, StringComparison.OrdinalIgnoreCase) && epNum > maxEp)
                            maxEp = epNum;
                    }
                }

                Console.WriteLine($"[TelegramBot] Collaps result: {sub.title} voice={sub.voice} episodes={maxEp} (was {sub.last_voice_episode})");
                return maxEp;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBot] Collaps check error: {ex.Message}");
                return sub.last_voice_episode;
            }
        }
        #endregion

        #region API для контроллера
        public static async Task<object> SubscribeApi(string uid, int tmdbId, string title, string voice,
            int season, int episode, string mirageOrid = null, int mirageVoiceId = 0, int voiceEpisode = 0,
            string collapsOrid = null, string voiceSource = null)
        {
            var user = Users.Values.FirstOrDefault(u => u.lampac_uid == uid);
            if (user == null) return new { success = false, msg = "not_linked" };

            var key = tmdbId.ToString();
            if (!Subs.ContainsKey(key)) Subs[key] = new List<Subscription>();

            // Удалить старую подписку с той же озвучкой
            Subs[key].RemoveAll(s => s.chat_id == user.chat_id && s.voice == (voice ?? ""));

            Subs[key].Add(new Subscription
            {
                chat_id = user.chat_id, tmdb_id = tmdbId, title = title,
                voice = voice ?? "",
                voice_source = voiceSource ?? (string.IsNullOrEmpty(mirageOrid) ? "collaps" : "mirage"),
                mirage_orid = mirageOrid ?? "",
                mirage_voice_id = mirageVoiceId,
                collaps_orid = collapsOrid ?? "",
                last_season = season, last_episode = episode,
                last_voice_episode = voiceEpisode,
                subscribed_at = DateTime.UtcNow
            });
            SaveSubscriptions();

            if (Bot != null)
            {
                var v = string.IsNullOrEmpty(voice) ? "любая озвучка" : voice;
                try { await Bot.SendMessage(user.chat_id, $"🔔 Подписка!\n*{EscapeMd(title)}* — {v}", parseMode: ParseMode.Markdown); } catch { }
            }
            return new { success = true };
        }

        public static object UnsubscribeApi(string uid, int tmdbId, string voice = null)
        {
            var user = Users.Values.FirstOrDefault(u => u.lampac_uid == uid);
            if (user == null) return new { success = false, msg = "not_linked" };

            var key = tmdbId.ToString();
            if (Subs.TryGetValue(key, out var list))
            {
                if (voice != null)
                    list.RemoveAll(s => s.chat_id == user.chat_id && s.voice == voice);
                else
                    list.RemoveAll(s => s.chat_id == user.chat_id);
                SaveSubscriptions();
            }
            return new { success = true };
        }

        public static object StatusApi(string uid, int tmdbId)
        {
            var user = Users.Values.FirstOrDefault(u => u.lampac_uid == uid);
            if (user == null) return new { success = true, subscribed = false, linked = false, voices = new string[0] };

            var userSubs = Subs.TryGetValue(tmdbId.ToString(), out var list)
                ? list.Where(s => s.chat_id == user.chat_id).ToList()
                : new List<Subscription>();

            return new
            {
                success = true, linked = true,
                subscribed = userSubs.Count > 0,
                voices = userSubs.Select(s => s.voice).ToArray()
            };
        }

        public static async Task<object> GetVoicesApi(string title, int year, int season, int tmdbId = 0)
        {
            // Определить актуальный сезон
            int actualSeason = season > 0 ? season : 1;

            if (tmdbId > 0)
            {
                try
                {
                    var detected = await GetCurrentSeason(tmdbId);
                    Console.WriteLine($"[TelegramBot] GetCurrentSeason({tmdbId}) = {detected} (requested: {season})");
                    if (detected > 0) actualSeason = detected;
                }
                catch (Exception ex) { Console.WriteLine($"[TelegramBot] Season detect error: {ex.Message}"); }
            }

            string mirageOrid = "";
            string collapsOrid = "";
            var allVoices = new List<VoiceInfo>();

            // 1. Попробовать Mirage
            var mirageResults = await SearchMirage(title, year);
            if (mirageResults.Count > 0)
            {
                mirageOrid = mirageResults[0].orid;
                var mirageVoices = await GetMirageVoices(mirageOrid, actualSeason);
                foreach (var v in mirageVoices)
                    v.source = "mirage";
                allVoices.AddRange(mirageVoices);
            }

            // 2. Попробовать Collaps (всегда — для fallback и дополнительных озвучек)
            var cOrid = await SearchCollaps(title, year);
            if (!string.IsNullOrEmpty(cOrid))
            {
                collapsOrid = cOrid;
                var collapsVoices = await GetCollapsVoices(cOrid, actualSeason);

                // Добавить только те озвучки которых нет в Mirage
                var existingNames = new HashSet<string>(allVoices.Select(v => v.name), StringComparer.OrdinalIgnoreCase);
                foreach (var v in collapsVoices)
                {
                    if (!existingNames.Contains(v.name))
                        allVoices.Add(v);
                }
            }

            Console.WriteLine($"[TelegramBot] GetVoices: {title} ({year}) s{actualSeason} — mirage:{mirageOrid} collaps:{collapsOrid} voices:{allVoices.Count}");

            return new { success = true, voices = allVoices, orid = mirageOrid, collaps_orid = collapsOrid, season = actualSeason };
        }

        // Определить последний вышедший сезон
        static async Task<int> GetCurrentSeason(int tmdbId)
        {
            // Сначала Trakt
            if (!string.IsNullOrEmpty(Config.trakt_client_id))
            {
                try
                {
                    var searchReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/search/tmdb/{tmdbId}?type=show");
                    searchReq.Headers.Add("trakt-api-key", Config.trakt_client_id);
                    searchReq.Headers.Add("trakt-api-version", "2");
                    var searchResp = await http.SendAsync(searchReq);

                    if (searchResp.IsSuccessStatusCode)
                    {
                        var searchData = JArray.Parse(await searchResp.Content.ReadAsStringAsync());
                        if (searchData.Count > 0)
                        {
                            var slug = searchData[0]?["show"]?["ids"]?.Value<string>("slug");
                            if (!string.IsNullOrEmpty(slug))
                            {
                                var sReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/shows/{slug}/seasons?extended=full");
                                sReq.Headers.Add("trakt-api-key", Config.trakt_client_id);
                                sReq.Headers.Add("trakt-api-version", "2");
                                var sResp = await http.SendAsync(sReq);

                                if (sResp.IsSuccessStatusCode)
                                {
                                    var seasons = JArray.Parse(await sResp.Content.ReadAsStringAsync());
                                    var lastAired = seasons
                                        .Where(s => s.Value<int>("number") > 0 && s.Value<int>("aired_episodes") > 0)
                                        .OrderByDescending(s => s.Value<int>("number"))
                                        .FirstOrDefault();

                                    if (lastAired != null)
                                    {
                                        var result = lastAired.Value<int>("number");
                                        Console.WriteLine($"[TelegramBot] Trakt current season for tmdb {tmdbId}: {result} (total seasons: {seasons.Count})");
                                        return result;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[TelegramBot] Trakt: no aired seasons found for tmdb {tmdbId}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[TelegramBot] GetCurrentSeason Trakt error: {ex.Message}"); }
            }

            // Fallback — TMDB: проверяем каждый сезон на наличие вышедших эпизодов
            try
            {
                Console.WriteLine($"[TelegramBot] GetCurrentSeason TMDB fallback for {tmdbId}");
                var show = JObject.Parse(await http.GetStringAsync(
                    $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={Config.tmdb_api_key}&language={Config.tmdb_lang}"));

                var tmdbSeasons = show["seasons"] as JArray;
                if (tmdbSeasons != null)
                {
                    // Ищем последний сезон с episode_count > 0 и air_date <= сегодня
                    int bestSeason = 1;
                    foreach (var s in tmdbSeasons)
                    {
                        var sNum = s.Value<int>("season_number");
                        var epCount = s.Value<int>("episode_count");
                        var airDate = s.Value<string>("air_date");

                        if (sNum <= 0 || epCount <= 0) continue;
                        if (!string.IsNullOrEmpty(airDate) && DateTime.TryParse(airDate, out var dt) && dt.Date <= DateTime.UtcNow.Date)
                            bestSeason = sNum;
                    }
                    Console.WriteLine($"[TelegramBot] TMDB current season for {tmdbId}: {bestSeason}");
                    return bestSeason;
                }

                return show.Value<int>("number_of_seasons");
            }
            catch { return 1; }
        }

        public static async Task<object> LinkApi(string uid)
        {
            if (Bot == null) return new { success = false, msg = "bot_not_running" };
            var me = await Bot.GetMe();
            return new { success = true, link = $"https://t.me/{me.Username}?start=link_{uid}" };
        }
        #endregion

        #region Storage
        static void LoadConfig()
        {
            try { if (System.IO.File.Exists(configPath)) Config = JsonConvert.DeserializeObject<BotConfig>(System.IO.File.ReadAllText(configPath)); } catch { }
            if (Config == null) { Config = new BotConfig(); SaveConfig(); }
        }
        static void SaveConfig() { try { System.IO.File.WriteAllText(configPath, JsonConvert.SerializeObject(Config, Formatting.Indented)); } catch { } }
        static void LoadUsers()
        {
            try { if (System.IO.File.Exists(usersPath)) { var l = JsonConvert.DeserializeObject<List<TgUser>>(System.IO.File.ReadAllText(usersPath)); if (l != null) foreach (var u in l) Users[u.chat_id] = u; } } catch { }
        }
        public static void SaveUsers() { try { System.IO.File.WriteAllText(usersPath, JsonConvert.SerializeObject(Users.Values.ToList(), Formatting.Indented)); } catch { } }
        static void LoadSubscriptions()
        {
            try { if (System.IO.File.Exists(subscriptionsPath)) { var d = JsonConvert.DeserializeObject<Dictionary<string, List<Subscription>>>(System.IO.File.ReadAllText(subscriptionsPath)); if (d != null) foreach (var kvp in d) Subs[kvp.Key] = kvp.Value; } } catch { }
        }
        public static void SaveSubscriptions()
        {
            try { System.IO.File.WriteAllText(subscriptionsPath, JsonConvert.SerializeObject(Subs.ToDictionary(k => k.Key, k => k.Value), Formatting.Indented)); } catch { }
        }
        #endregion

        static string EscapeMd(string text) =>
            string.IsNullOrEmpty(text) ? "" : text.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }
}
