# Tavern Character Card Manager (WPF / .NET 10)

A fast, portable Windows desktop manager for **TavernAI / SillyTavern** character cards (`.png`), built with **WPF (.NET 10)**, **SQLite**, and **WebView2**, with optional integration for **Botbooru** and local **OpenAI-compatible LLMs**.

---

## Features

### Local Card Library & Editor
* **V1 / V2 / V3 PNG Metadata Codec:** Reads and writes `tEXt:chara` and `tEXt:ccv3` Base64 JSON chunks directly inside PNG files while preserving all other chunks (including `parameters`, `character_book`, and `extensions`).
* **Blazing-Fast Virtualized List:** Handles 2,000+ cards smoothly using WPF UI virtualization and downscaled on-load thumbnails.
* **Portable Per-Folder Cache:** Each card folder stores its own `<CardFolder>\.cardmanager\cache.db` (keyed by filename, so folders can be moved or backed up anywhere) and cached galleries/links. App-wide settings and learned tag categories live in `app.db` next to the executable.
* **Full Card Editor:** Edit `Name`, `Creator`, `Version`, `Tagline`, `Nickname`, `Description`, `Personality`, `Scenario`, `First Message`, `Alternate Greetings`, `System Prompt`, `Post-History Instructions`, `Creator Notes`, `Message Example`, and `Tags`.
* **Search, Exclude & Quick Filters:**
  * Search across name, creator, tags, description, filename, or `#ID`.
  * Prefix any term with `-` (or right-click any tag chip) to exclude it.
  * Quick filters for `Missing 'original'`, `Original Only`, `Has Lorebook`, `V3 Cards Only`, `Has Botbooru ID`, `Not Synced (Botbooru)`, and `Synced Only`.
* **Full-Size Image Lightbox:** Click any card portrait or gallery thumbnail to inspect it full-size (`Esc` or click to close).

### Botbooru Integration
* **Account Sign-In (`Botbooru Login`):** Uses an embedded Edge **WebView2** browser to sign in to `https://botbooru.com/account` with your own account (needed for NSFW posts and `slop_score`). Session data stays strictly local in `app.db` / `webview_data/`.
* **Categorized Tags & Tag Browser:** Syncs tag categories (`Writer`, `Artist`, `Copyright`, `Characters`, `Scenarios`, `Settings`, `Activities`, `General`, `Meta`, `Language`, `Auto`) and provides a collapsible **Tag Browser** drawer to filter by category and include/exclude tags in one click.
* **Gallery & Image Swap:** View and cache a card's `mini_gallery` images and click **Use as Card Image** (or **Replace Image from Local PNG...**) to embed your card's metadata into a different portrait PNG.
* **Links & Forks:** View `Master`, `Fork`, and `Sibling` relationships, jump directly to linked cards already in your folder, or download missing ones.
* **Writing Style Score:** Displays Botbooru's `slop_score` band (`Original` / `Mixed` / `Common`) and originality bar.
* **Built-In Botbooru Browser (`Browse Botbooru`):**
  * Supports `category:tag` / `-category:tag` search syntax, sort modes, `SFW Only`, `Hide AI`, and `Hide Already in Folder`.
  * Memory-efficient 4-page sliding-window lazy loader with smooth scrolling, `✓ In Folder` badges, single-card inspector, and checkbox batch downloads.
  * **Rate-Limit Friendly:** Batch downloads pause `3000ms` between cards (~18–20/min) and automatically halt on HTTP `429`.

### Local LLM Q&A (`Ask AI` Tab)
* Connect to any local **OpenAI-compatible** `/v1/chat/completions` endpoint (oobabooga, LM Studio, Ollama, etc.) to ask questions about the selected card, with one-click audit prompts (`Quick Audit`, `World & Setting`, `Character Count`, `Narrator vs Character`, `Relation to User`, `Check Red Flags`, `Summarize Card`, `Suggest Tagline`, `Suggest Missing Tags`).

---

## Requirements & Running from Source

* **Windows 10 / 11**
* **[.NET 10 SDK](https://dotnet.microsoft.com/download)**
* **Microsoft Edge WebView2 Runtime** (pre-installed on Windows 10/11)

```powershell
git clone <your-repo-url>
cd CharacterCardManager
dotnet run --project CharacterCardManager\CharacterCardManager.csproj
```

Or open `CharacterCardManager.slnx` in **Visual Studio 2022/2026** and press **F5**.

---

## Notes on Botbooru Usage
* No credentials are hardcoded or bypassed — users sign in with their own Botbooru account.
* Sync and batch download features include polite delays and `429` rate-limit handling. Please respect Botbooru's Terms of Service and rate limits.
