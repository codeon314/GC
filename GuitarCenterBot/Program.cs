using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace GuitarCenterBot
{
    // ==========================================
    // MODELS
    // ==========================================
    public class AppConfig
    {
        public string BotToken { get; set; } = "";
        public string ChatId { get; set; } = "";
        public string SearchUrl { get; set; } = "https://www.guitarcenter.com/search?Ntt=schecter%20c1%20classic";
        public string Keyword { get; set; } = "classic";
        public double IntervalHours { get; set; } = 12.0;
    }

    public class CacheItem
    {
        public string ItemId { get; set; } = "";
        public string Url { get; set; } = "";
        public List<int> MessageIds { get; set; } = new();
    }

    // ==========================================
    // REAL-TIME HUB
    // ==========================================
    public class CacheHub : Hub { }

    // ==========================================
    // MEMORY LOGGER (For Web UI)
    // ==========================================
    public static class MemoryLogger
    {
        private static readonly ConcurrentQueue<string> _logs = new();
        private const int MaxLogs = 500;

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(line);
            _logs.Enqueue(line);
            while (_logs.Count > MaxLogs)
            {
                _logs.TryDequeue(out _);
            }
        }

        public static IEnumerable<string> GetLogs() => _logs.ToArray();
    }

    // ==========================================
    // SCRAPER SERVICE
    // ==========================================
    public class ScraperService
    {
        private readonly string _cacheFilePath = "cache.json";
        private readonly string _configFilePath = "config.json";
        private readonly SemaphoreSlim _scrapeLock = new(1, 1);
        private readonly IHubContext<CacheHub> _hubContext;

        // Shared HttpClient to prevent socket exhaustion and save CPU/RAM
        private static readonly HttpClient _httpClient = new HttpClient();

        public ScraperService(IHubContext<CacheHub> hubContext)
        {
            _hubContext = hubContext;
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");
            }
        }

        public AppConfig GetConfig()
        {
            if (!System.IO.File.Exists(_configFilePath))
            {
                var defaultConfig = new AppConfig();
                SaveConfig(defaultConfig);
                return defaultConfig;
            }
            string json = System.IO.File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }

        public void SaveConfig(AppConfig config)
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(_configFilePath, json);
            MemoryLogger.Log("Configuration saved successfully.");
        }

        public Dictionary<string, CacheItem> GetCache()
        {
            if (!System.IO.File.Exists(_cacheFilePath))
            {
                return new Dictionary<string, CacheItem>();
            }
            try
            {
                string json = System.IO.File.ReadAllText(_cacheFilePath);
                return JsonSerializer.Deserialize<Dictionary<string, CacheItem>>(json) ?? new Dictionary<string, CacheItem>();
            }
            catch
            {
                return new Dictionary<string, CacheItem>();
            }
        }

        private async Task SaveCacheAndNotifyAsync(Dictionary<string, CacheItem> cache)
        {
            string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(_cacheFilePath, json);
            await _hubContext.Clients.All.SendAsync("CacheUpdated", cache.Values);
        }

        public async Task UpdateCacheItemAsync(string oldId, CacheItem newItem)
        {
            var cache = GetCache();
            if (cache.ContainsKey(oldId))
            {
                cache.Remove(oldId);
            }
            cache[newItem.ItemId] = newItem;
            await SaveCacheAndNotifyAsync(cache);
            MemoryLogger.Log($"Updated cache item. Old ID: {oldId}, New ID: {newItem.ItemId}");
        }

        public async Task ClearCacheAsync()
        {
            var config = GetConfig();
            var initialCache = GetCache();

            if (!initialCache.Any())
            {
                MemoryLogger.Log("Cache is already empty.");
                return;
            }

            TelegramBotClient? botClient = null;
            if (!string.IsNullOrWhiteSpace(config.BotToken) && !string.IsNullOrWhiteSpace(config.ChatId))
            {
                botClient = new TelegramBotClient(config.BotToken);
            }
            MemoryLogger.Log("Starting to clear cache and delete all messages from Telegram...");

            var itemIdsToDelete = initialCache.Keys.ToList();

            foreach (var itemId in itemIdsToDelete)
            {
                var currentCache = GetCache();
                if (currentCache.TryGetValue(itemId, out var item))
                {
                    if (botClient != null)
                    {
                        foreach (var msgId in item.MessageIds)
                        {
                            try
                            {
                                await botClient.DeleteMessageAsync(config.ChatId, msgId);
                                await Task.Delay(50); // Small delay to prevent rate limiting
                            }
                            catch { /* Ignore errors */ }
                        }
                    }
                    currentCache.Remove(itemId);
                    await SaveCacheAndNotifyAsync(currentCache); // This saves and notifies UI
                    MemoryLogger.Log($"Removed item {itemId} during clear operation.");
                    await Task.Delay(50); // Small delay for UI to feel smoother
                }
            }

            MemoryLogger.Log("Cache clearing process complete.");
        }


        public async Task DeleteCacheItemAsync(string itemId)
        {
            var config = GetConfig();
            var cache = GetCache();

            if (cache.TryGetValue(itemId, out var item))
            {
                if (!string.IsNullOrWhiteSpace(config.BotToken) && !string.IsNullOrWhiteSpace(config.ChatId))
                {
                    var botClient = new TelegramBotClient(config.BotToken);
                    foreach (var msgId in item.MessageIds)
                    {
                        try
                        {
                            await botClient.DeleteMessageAsync(config.ChatId, msgId);
                            await Task.Delay(100);
                        }
                        catch { }
                    }
                }
                cache.Remove(itemId);
                await SaveCacheAndNotifyAsync(cache);
                MemoryLogger.Log($"Manually deleted item {itemId} from cache and Telegram.");
            }
        }

        private async Task ValidateExistingCacheAsync(TelegramBotClient botClient, string chatId, Dictionary<string, CacheItem> cache)
        {
            MemoryLogger.Log("Validating existing cache entries...");
            bool cacheUpdated = false;
            var itemsToList = cache.Values.ToList();

            foreach (var item in itemsToList)
            {
                try
                {
                    var response = await _httpClient.GetAsync(item.Url);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        await RemoveInvalidItemAsync(botClient, chatId, cache, item, "returned 404 Not Found");
                        cacheUpdated = true;
                        continue;
                    }

                    string html = await response.Content.ReadAsStringAsync();

                    if (html.Contains("This highly sought-after gear went quickly!") ||
                        html.Contains("Hey, who turned down the music?"))
                    {
                        await RemoveInvalidItemAsync(botClient, chatId, cache, item, "is marked as sold/removed on the page");
                        cacheUpdated = true;
                    }
                }
                catch (Exception ex)
                {
                    MemoryLogger.Log($"Error validating item {item.ItemId}: {ex.Message}");
                }

                await Task.Delay(500); // Be gentle on the server
            }

            if (cacheUpdated)
            {
                await SaveCacheAndNotifyAsync(cache);
            }
            MemoryLogger.Log("Cache validation complete.");
        }

        private async Task RemoveInvalidItemAsync(TelegramBotClient botClient, string chatId, Dictionary<string, CacheItem> cache, CacheItem item, string reason)
        {
            MemoryLogger.Log($"Item {item.ItemId} {reason}. Removing from Telegram and cache.");
            foreach (var msgId in item.MessageIds)
            {
                try
                {
                    await botClient.DeleteMessageAsync(chatId, msgId);
                    await Task.Delay(100);
                }
                catch { }
            }
            cache.Remove(item.ItemId);
        }

        public async Task RunScrapeAsync()
        {
            if (!_scrapeLock.Wait(0))
            {
                MemoryLogger.Log("A scrape is already in progress. Skipping manual trigger.");
                return;
            }

            try
            {
                var config = GetConfig();

                if (string.IsNullOrWhiteSpace(config.BotToken) || string.IsNullOrWhiteSpace(config.ChatId))
                {
                    MemoryLogger.Log("Bot Token or Chat ID is missing in configuration. Aborting scrape.");
                    return;
                }

                MemoryLogger.Log("Starting Guitar Center Scrape...");

                var cache = GetCache();
                var botClient = new TelegramBotClient(config.BotToken);

                // 1. Validate existing cache to remove sold items
                await ValidateExistingCacheAsync(botClient, config.ChatId, cache);

                // 2. Proceed with scraping
                int currentPage = 1;
                int totalMatchCount = 0;
                bool hasMorePages = true;

                while (hasMorePages)
                {
                    string searchUrl = currentPage == 1 ? config.SearchUrl : $"{config.SearchUrl}&page={currentPage}";

                    MemoryLogger.Log($"Fetching search page {currentPage}...");

                    string htmlContent;
                    try
                    {
                        htmlContent = await _httpClient.GetStringAsync(searchUrl);
                    }
                    catch (Exception ex)
                    {
                        MemoryLogger.Log($"Failed to fetch search page: {ex.Message}");
                        break;
                    }

                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(htmlContent);

                    var productNodes = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'product-item')]");

                    if (productNodes == null || productNodes.Count == 0)
                    {
                        MemoryLogger.Log($"No products found on page {currentPage}. Ending pagination.");
                        break;
                    }

                    MemoryLogger.Log($"Found {productNodes.Count} total product tiles on page {currentPage}. Filtering for used '{config.Keyword}' items...");

                    int pageMatchCount = 0;

                    foreach (var node in productNodes)
                    {
                        var searchLocationNode = node.SelectSingleNode(".//span[contains(@class, 'store-name-text')]");
                        if (searchLocationNode == null) continue;

                        var titleNode = node.SelectSingleNode(".//a[contains(@class, 'product-name')]/h2")
                                     ?? node.SelectSingleNode(".//h2");

                        string title = WebUtility.HtmlDecode(titleNode?.InnerText?.Trim() ?? string.Empty);

                        if (!string.IsNullOrEmpty(config.Keyword) && !title.Contains(config.Keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var priceNode = node.SelectSingleNode(".//span[contains(@class, 'sale-price')]");
                        string price = priceNode?.InnerText?.Trim() ?? "Price not found";

                        var linkNode = node.SelectSingleNode(".//a[contains(@class, 'product-name')]");
                        string link = linkNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                        if (!string.IsNullOrEmpty(link) && link.StartsWith("/"))
                        {
                            link = "https://www.guitarcenter.com" + link;
                        }

                        if (string.IsNullOrEmpty(link)) continue;

                        string itemId = string.Empty;
                        var match = Regex.Match(link, @"-(\d+)\.gc");
                        if (match.Success)
                        {
                            itemId = match.Groups[1].Value.Replace("Item #:", string.Empty);
                        }

                        MemoryLogger.Log($"Deep scraping listing: {link}");
                        string listingHtml = "";
                        try
                        {
                            listingHtml = await _httpClient.GetStringAsync(link);
                        }
                        catch (Exception ex)
                        {
                            MemoryLogger.Log($"Failed to fetch listing page: {ex.Message}");
                            continue;
                        }

                        var listingDoc = new HtmlDocument();
                        listingDoc.LoadHtml(listingHtml);

                        if (string.IsNullOrEmpty(itemId))
                        {
                            var itemIdNode = listingDoc.DocumentNode.SelectSingleNode("//span[contains(., 'Item #:')]/span");
                            itemId = itemIdNode?.InnerText?.Trim() ?? string.Empty;
                        }

                        // Bulletproof way to ensure ONLY numbers are kept in the itemId
                        if (!string.IsNullOrEmpty(itemId))
                        {
                            itemId = Regex.Replace(itemId, @"[^\d]", "");
                        }

                        if (string.IsNullOrEmpty(itemId))
                        {
                            MemoryLogger.Log("Could not find Item ID. Skipping to avoid spam.");
                            continue;
                        }

                        // Re-check cache after getting a definite ID
                        cache = GetCache();
                        if (cache.ContainsKey(itemId))
                        {
                            MemoryLogger.Log($"Item {itemId} is already in cache. Skipping.");
                            continue;
                        }

                        var conditionNode = listingDoc.DocumentNode.SelectSingleNode("//span[contains(@class, 'price-type-condition-value')]");
                        string condition = conditionNode?.InnerText?.Replace("&nbsp;", "")?.Trim() ?? "Used";
                        condition = Regex.Replace(condition, "<.*?>", string.Empty).Trim();

                        var locationNode = listingDoc.DocumentNode.SelectSingleNode("//span[contains(., 'Item Location:')]//a");
                        string location = locationNode?.InnerText?.Trim() ?? searchLocationNode.InnerText.Trim();

                        var imageNodes = listingDoc.DocumentNode.SelectNodes("//img[contains(@class, 'product-gallery-img')]");
                        List<string> imageUrls = new List<string>();

                        if (imageNodes != null)
                        {
                            foreach (var img in imageNodes)
                            {
                                string src = img.GetAttributeValue("src", "");
                                if (!string.IsNullOrEmpty(src) && !imageUrls.Contains(src))
                                {
                                    src = src.Replace("600x600", "2000x2000");
                                    imageUrls.Add(src);
                                }
                            }
                        }

                        if (imageUrls.Count == 0)
                        {
                            var searchImageNode = node.SelectSingleNode(".//div[contains(@class, 'algolia-plp-product-gallery')]//img");
                            string searchImageUrl = searchImageNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;
                            if (!string.IsNullOrEmpty(searchImageUrl))
                            {
                                imageUrls.Add(searchImageUrl);
                            }
                        }

                        pageMatchCount++;
                        totalMatchCount++;

                        MemoryLogger.Log($"Match Found: {title} | ID: {itemId} | Price: {price}");

                        string caption = $"🎸 **{title}**\n\n" +
                                         $"💰 **Price:** {price}\n" +
                                         $"🏷 **Condition:** {condition}\n" +
                                         $"📍 **Location:** {location}\n" +
                                         $"🆔 **Item ID:** {itemId}\n\n" +
                                         $"🔗 [View Listing]({link})";

                        try
                        {
                            List<int> sentMessageIds = new List<int>();

                            if (imageUrls.Count > 0)
                            {
                                var mediaGroup = new List<IAlbumInputMedia>();
                                var limitedUrls = imageUrls.Take(10).ToList();

                                for (int i = 0; i < limitedUrls.Count; i++)
                                {
                                    var media = new InputMediaPhoto(InputFile.FromUri(limitedUrls[i]));
                                    if (i == 0)
                                    {
                                        media.Caption = caption;
                                        media.ParseMode = Telegram.Bot.Types.Enums.ParseMode.Markdown;
                                    }
                                    mediaGroup.Add(media);
                                }

                                var messages = await botClient.SendMediaGroupAsync(
                                    chatId: config.ChatId,
                                    media: mediaGroup
                                );
                                sentMessageIds.AddRange(messages.Select(m => m.MessageId));
                            }
                            else
                            {
                                var message = await botClient.SendTextMessageAsync(
                                    chatId: config.ChatId,
                                    text: caption,
                                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
                                );
                                sentMessageIds.Add(message.MessageId);
                            }

                            MemoryLogger.Log("Successfully sent to Telegram.");

                            cache = GetCache(); // re-fetch cache before adding to it
                            cache[itemId] = new CacheItem
                            {
                                ItemId = itemId,
                                Url = link,
                                MessageIds = sentMessageIds
                            };
                            await SaveCacheAndNotifyAsync(cache);
                        }
                        catch (Exception tgEx)
                        {
                            MemoryLogger.Log($"Failed to send message to Telegram: {tgEx.Message}");
                        }

                        await Task.Delay(2000);
                    }

                    MemoryLogger.Log($"Finished processing page {currentPage}. Found {pageMatchCount} new matches.");

                    var nextButton = htmlDoc.DocumentNode.SelectSingleNode("//li[contains(@class, 'ant-pagination-next')]");

                    if (nextButton == null ||
                        nextButton.GetAttributeValue("class", "").Contains("ant-pagination-disabled") ||
                        nextButton.GetAttributeValue("aria-disabled", "false") == "true")
                    {
                        MemoryLogger.Log("No more pages detected.");
                        hasMorePages = false;
                    }
                    else
                    {
                        currentPage++;
                        await Task.Delay(2000);
                    }
                }

                MemoryLogger.Log($"Scraping complete. Found {totalMatchCount} new matching items.");
            }
            catch (Exception ex)
            {
                MemoryLogger.Log($"An error occurred during execution: {ex.Message}");
            }
            finally
            {
                _scrapeLock.Release();
            }
        }
    }

    // ==========================================
    // BACKGROUND SERVICE (Timer)
    // ==========================================
    public class BackgroundScraper : BackgroundService
    {
        private readonly ScraperService _scraperService;

        public BackgroundScraper(ScraperService scraperService)
        {
            _scraperService = scraperService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            MemoryLogger.Log("Background scraper service started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var config = _scraperService.GetConfig();
                double intervalHours = config.IntervalHours > 0 ? config.IntervalHours : 12.0;

                MemoryLogger.Log($"Running scheduled scrape. Next scrape in {intervalHours} hours.");

                _ = Task.Run(async () =>
                {
                    await _scraperService.RunScrapeAsync();
                }, stoppingToken);

                // Task.Delay yields the thread, consuming 0 CPU while waiting.
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
        }
    }

    // ==========================================
    // MAIN ENTRY POINT & API ENDPOINTS
    // ==========================================
    class Program
    {
        static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<ScraperService>();
            builder.Services.AddHostedService<BackgroundScraper>();

            var app = builder.Build();

            app.UseDefaultFiles();
            app.UseStaticFiles();

            // Config APIs
            app.MapGet("/api/config", (ScraperService scraper) => Results.Ok(scraper.GetConfig()));
            app.MapPost("/api/config", (AppConfig newConfig, ScraperService scraper) =>
            {
                scraper.SaveConfig(newConfig);
                return Results.Ok(new { message = "Configuration saved." });
            });

            // Logs API
            app.MapGet("/api/logs", () => Results.Ok(MemoryLogger.GetLogs()));

            // Scrape API
            app.MapPost("/api/scrape", (ScraperService scraper) =>
            {
                _ = Task.Run(() => scraper.RunScrapeAsync());
                return Results.Ok(new { message = "Scrape started." });
            });

            // Cache APIs
            app.MapGet("/api/cache", (ScraperService scraper) => Results.Ok(scraper.GetCache().Values));

            app.MapPut("/api/cache/{oldId}", async (string oldId, CacheItem updatedItem, ScraperService scraper) =>
            {
                await scraper.UpdateCacheItemAsync(oldId, updatedItem);
                return Results.Ok(new { message = "Item updated." });
            });

            app.MapDelete("/api/cache/{id}", async (string id, ScraperService scraper) =>
            {
                await scraper.DeleteCacheItemAsync(id);
                return Results.Ok(new { message = "Item deleted." });
            });

            app.MapPost("/api/clearcache", async (ScraperService scraper) =>
            {
                // This is now a long-running operation, so we don't await it here.
                // The service will send SignalR updates as it progresses.
                _ = Task.Run(() => scraper.ClearCacheAsync());
                return Results.Ok(new { message = "Cache clearing process started." });
            });

            // Map SignalR Hub
            app.MapHub<CacheHub>("/cacheHub");

            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            app.Urls.Add($"http://0.0.0.0:{port}");

            MemoryLogger.Log("Web server starting...");
            app.Run();
        }
    }
}