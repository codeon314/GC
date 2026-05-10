using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    // CONFIGURATION MODEL
    // ==========================================
    public class AppConfig
    {
        public string BotToken { get; set; } = "";
        public string ChatId { get; set; } = "";
        public string SearchUrl { get; set; } = "https://www.guitarcenter.com/search?Ntt=schecter%20c1%20classic";
        public string Keyword { get; set; } = "classic";
        public double IntervalHours { get; set; } = 12.0;
    }

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
        private readonly string _cacheFilePath = "cache.txt";
        private readonly string _configFilePath = "config.json";
        private readonly SemaphoreSlim _scrapeLock = new(1, 1);

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

        public void ClearCache()
        {
            if (System.IO.File.Exists(_cacheFilePath))
            {
                System.IO.File.Delete(_cacheFilePath);
            }
            MemoryLogger.Log("Cache cleared successfully.");
        }

        public async Task RunScrapeAsync()
        {
            // Prevent multiple scrapes from running at the exact same time
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

                HashSet<string> cache = new HashSet<string>();
                if (System.IO.File.Exists(_cacheFilePath))
                {
                    cache = new HashSet<string>(System.IO.File.ReadAllLines(_cacheFilePath));
                    MemoryLogger.Log($"Loaded {cache.Count} items from cache.");
                }

                var botClient = new TelegramBotClient(config.BotToken);

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");

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
                        htmlContent = await httpClient.GetStringAsync(searchUrl);
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
                            itemId = match.Groups[1].Value;
                        }

                        if (!string.IsNullOrEmpty(itemId) && cache.Contains(itemId))
                        {
                            MemoryLogger.Log($"Item {itemId} is already in cache. Skipping.");
                            continue;
                        }

                        MemoryLogger.Log($"Deep scraping listing: {link}");
                        string listingHtml = "";
                        try
                        {
                            listingHtml = await httpClient.GetStringAsync(link);
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

                        if (string.IsNullOrEmpty(itemId))
                        {
                            MemoryLogger.Log("Could not find Item ID. Skipping to avoid spam.");
                            continue;
                        }

                        if (cache.Contains(itemId))
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

                                await botClient.SendMediaGroupAsync(
                                    chatId: config.ChatId,
                                    media: mediaGroup
                                );
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: config.ChatId,
                                    text: caption,
                                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
                                );
                            }

                            MemoryLogger.Log("Successfully sent to Telegram.");

                            cache.Add(itemId);
                            System.IO.File.AppendAllText(_cacheFilePath, itemId + Environment.NewLine);
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

                // Run the scrape without awaiting it blocking the timer, but catch exceptions
                _ = Task.Run(async () =>
                {
                    await _scraperService.RunScrapeAsync();
                }, stoppingToken);

                // Wait for the configured interval
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

            // Register services
            builder.Services.AddSingleton<ScraperService>();
            builder.Services.AddHostedService<BackgroundScraper>();

            var app = builder.Build();

            // Serve static files from wwwroot (index.html)
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // API: Get Config
            app.MapGet("/api/config", (ScraperService scraper) =>
            {
                return Results.Ok(scraper.GetConfig());
            });

            // API: Save Config
            app.MapPost("/api/config", (AppConfig newConfig, ScraperService scraper) =>
            {
                scraper.SaveConfig(newConfig);
                return Results.Ok(new { message = "Configuration saved." });
            });

            // API: Get Logs
            app.MapGet("/api/logs", () =>
            {
                return Results.Ok(MemoryLogger.GetLogs());
            });

            // API: Trigger Manual Scrape
            app.MapPost("/api/scrape", (ScraperService scraper) =>
            {
                // Fire and forget so the HTTP request doesn't hang
                _ = Task.Run(() => scraper.RunScrapeAsync());
                return Results.Ok(new { message = "Scrape started." });
            });

            // API: Clear Cache
            app.MapPost("/api/clearcache", (ScraperService scraper) =>
            {
                scraper.ClearCache();
                return Results.Ok(new { message = "Cache cleared." });
            });

            // Bind to port provided by Railway or default to 8080
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            app.Urls.Add($"http://0.0.0.0:{port}");

            MemoryLogger.Log("Web server starting...");
            app.Run();
        }
    }
}