### Explanation of Changes
I have created a complete, ready-to-run C# Console Application that scrapes the Guitar Center search page for the specified term. It uses `HtmlAgilityPack` to parse the HTML and `Telegram.Bot` to send the extracted data to a Telegram channel. 

The program specifically looks for the `store-name-text` class to identify used items and skips any promoted or new items that lack this location data. It also filters the results to ensure the word "classic" is in the title. 

Additionally, I have created a `README.md` file that documents the current Phase 1 implementation, provides instructions on how to schedule the program to run daily at 9 AM using OS-level task schedulers, and outlines the architecture and requirements for Phase 2 (caching and deep-scraping individual listing pages).

### How to Test
1. Create a new C# Console Application (.NET 6 or later recommended).
2. Install the required NuGet packages by running the following commands in your terminal or Package Manager Console:
   * `dotnet add package HtmlAgilityPack`
   * `dotnet add package Telegram.Bot`
3. Replace the contents of your `Program.cs` with the provided C# code.
4. Update the `botToken` and `chatId` variables in the code with your actual Telegram Bot Token and the Target Channel/Chat ID.
5. Run the application. You should see console output indicating the scraping progress, and your Telegram channel should receive messages with the image, title, price, and link for each valid used listing found.

***

# Guitar Center Scraper

A C# Console Application designed to scrape Guitar Center's search results for specific used gear (e.g., "schecter c1 classic") and post the findings to a Telegram channel.

## Phase 1: Search Page Scraping (Current Implementation)

### Features
* **Targeted Scraping**: Fetches the search results page for the query "schecter c1 classic".
* **Smart Filtering**: 
  * Only processes items that contain the word "classic" in the title.
  * Automatically skips promoted, new, or financed items by verifying the presence of the `store-name-text` HTML class (which is exclusive to used items).
* **Data Extraction**: Grabs the listing Title, Price, Thumbnail Image URL, and the direct Link to the product.
* **Telegram Integration**: Formats the extracted data into a clean Markdown message and sends it to a specified Telegram channel along with the thumbnail image.

### Setup & Configuration
1. Open `Program.cs`.
2. Replace `YOUR_TELEGRAM_BOT_TOKEN_HERE` with your bot token from BotFather.
3. Replace `YOUR_TELEGRAM_CHAT_ID_HERE` with your target channel or chat ID (e.g., `@my_channel_name` or `-100123456789`).
4. Build the project using `dotnet build`.

### Scheduling the Program (Daily at 9 AM)
Because this is a standard C# Console Application, the most robust and resource-efficient way to run it daily at 9 AM is to use your operating system's built-in task scheduler.

#### Windows (Task Scheduler)
1. Open **Task Scheduler** and click **Create Basic Task...**
2. Name it "Guitar Center Scraper".
3. Set the Trigger to **Daily** and set the start time to **9:00 AM**.
4. Set the Action to **Start a program**.
5. Browse to the compiled `.exe` file of this application (found in your `bin/Release/netX.X/` folder).
6. Finish and save. The OS will now run the scraper every day at 9 AM.

#### Linux / macOS (Cron)
1. Open your terminal and type `crontab -e`.
2. Add the following line to run the compiled application daily at 9:00 AM:
   ```bash
   0 9 * * * /path/to/your/compiled/GuitarCenterScraper
   ```
3. Save and exit.

---

## Phase 2: Deep Scraping & Caching (Upcoming)

Phase 2 will expand the program to visit the individual listing pages to gather more detailed information and implement a caching system to prevent duplicate Telegram alerts.

### Planned Features for Phase 2
1. **Deep Scraping**:
   * Instead of just scraping the search page, the program will take the `href` link of valid items and make a secondary HTTP request to the actual listing page.
   * Extract the **Item Number / ID** from the listing page.
   * Extract **all high-resolution images** associated with the listing.
   * Extract detailed **Location** and **Condition** information.
2. **Caching System**:
   * Implement a local cache (e.g., a local SQLite database, JSON file, or simple text file).
   * Before sending a Telegram message, the program will check if the extracted **Item Number** exists in the cache.
   * If it exists, the item is skipped. If it is new, the item is posted to Telegram and the Item Number is saved to the cache.
3. **Media Groups**:
   * Upgrade the Telegram bot integration to send a `MediaGroup` (an album of multiple photos) rather than just a single thumbnail.

### Next Steps for AI Session
To begin Phase 2, provide the AI with the HTML source code of an **individual listing page** (e.g., the page you land on after clicking a used Schecter C1 Classic). The AI will use this HTML to write the extraction logic for the Item ID and the high-resolution image gallery.
