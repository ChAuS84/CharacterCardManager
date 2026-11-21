using System.IO;
using System.Text.Json;
using CharacterCardManager.Models;
using Microsoft.Data.Sqlite;

namespace CharacterCardManager.Services;

/// <summary>
/// Manages two portable SQLite databases:
/// 1) App-level DB (next to .exe): settings, Botbooru auth, and shared tag_categories.
/// 2) Per-folder DB (&lt;folder&gt;\.cardmanager\cache.db): card metadata keyed by file_name so folders are portable.
/// </summary>
public sealed class CardCacheDb
{
    private readonly string _appConnStr;

    public CardCacheDb()
    {
        string appDbPath = Path.Combine(AppContext.BaseDirectory, "app.db");
        _appConnStr = $"Data Source={appDbPath}";
        InitializeAppDb();
    }

    private void InitializeAppDb()
    {
        using var conn = new SqliteConnection(_appConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tag_categories (
                tag_name TEXT PRIMARY KEY COLLATE NOCASE,
                category TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static string GetFolderCacheDir(string folderPath)
        => Path.Combine(folderPath, ".cardmanager");

    public static string GetFolderGalleryDir(string folderPath, long postId)
        => Path.Combine(folderPath, ".cardmanager", "galleries", postId.ToString());

    private static string GetFolderDbConnStr(string folderPath)
    {
        string cacheDir = GetFolderCacheDir(folderPath);
        Directory.CreateDirectory(cacheDir);
        string dbPath = Path.Combine(cacheDir, "cache.db");
        return $"Data Source={dbPath}";
    }

    private static void EnsureFolderDbSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cards (
                file_name TEXT PRIMARY KEY,
                file_size INTEGER NOT NULL,
                last_modified_ticks INTEGER NOT NULL,
                spec_version TEXT NOT NULL,
                botbooru_post_id INTEGER NULL,
                name TEXT NOT NULL,
                creator TEXT NOT NULL,
                tagline TEXT NOT NULL,
                description TEXT NOT NULL,
                personality TEXT NOT NULL,
                scenario TEXT NOT NULL,
                first_mes TEXT NOT NULL,
                tags_json TEXT NOT NULL,
                lorebook_count INTEGER NOT NULL,
                alt_greeting_count INTEGER NOT NULL,
                has_original_tag INTEGER NOT NULL,
                gallery_count INTEGER NOT NULL DEFAULT 0,
                is_fork INTEGER NOT NULL DEFAULT 0,
                fork_count INTEGER NOT NULL DEFAULT 0,
                sibling_count INTEGER NOT NULL DEFAULT 0,
                slop_score REAL NULL,
                is_synced INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        string[] migrations =
        [
            "ALTER TABLE cards ADD COLUMN gallery_count INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE cards ADD COLUMN is_fork INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE cards ADD COLUMN fork_count INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE cards ADD COLUMN sibling_count INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE cards ADD COLUMN slop_score REAL NULL;",
            "ALTER TABLE cards ADD COLUMN is_synced INTEGER NOT NULL DEFAULT 0;"
        ];
        foreach (var sql in migrations)
        {
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists
            }
        }
    }

    public string? GetSetting(string key)
    {
        using var conn = new SqliteConnection(_appConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection(_appConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, CardSummary> GetCachedCardsInFolder(string folderPath)
    {
        var result = new Dictionary<string, CardSummary>(StringComparer.OrdinalIgnoreCase);

        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_name, file_size, last_modified_ticks, spec_version, botbooru_post_id,
                   name, creator, tagline, description, personality, scenario, first_mes,
                   tags_json, lorebook_count, alt_greeting_count, has_original_tag, gallery_count,
                   is_fork, fork_count, sibling_count, slop_score, is_synced
            FROM cards;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string fileName = reader.GetString(0);
            string fullPath = Path.Combine(folderPath, fileName);
            var summary = new CardSummary
            {
                FilePath = fullPath,
                FileName = fileName,
                FileSize = reader.GetInt64(1),
                LastModifiedTicks = reader.GetInt64(2),
                SpecVersion = reader.GetString(3),
                BotbooruPostId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Name = reader.GetString(5),
                Creator = reader.GetString(6),
                Tagline = reader.GetString(7),
                Description = reader.GetString(8),
                Personality = reader.GetString(9),
                Scenario = reader.GetString(10),
                FirstMessage = reader.GetString(11),
                Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(12)) ?? [],
                LorebookCount = reader.GetInt32(13),
                AlternateGreetingCount = reader.GetInt32(14),
                HasOriginalTag = reader.GetInt32(15) == 1,
                GalleryCount = reader.GetInt32(16),
                IsFork = reader.GetInt32(17) == 1,
                ForkCount = reader.GetInt32(18),
                SiblingCount = reader.GetInt32(19),
                SlopScore = reader.IsDBNull(20) ? null : reader.GetDouble(20),
                IsBotbooruSynced = reader.GetInt32(21) == 1
            };
            result[fullPath] = summary;
        }
        return result;
    }

    public void MarkSynced(string folderPath, string fileName)
    {
        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE cards SET is_synced = 1 WHERE file_name = $name;";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.ExecuteNonQuery();
    }

    public void UpdateSlopScore(string folderPath, string fileName, double? slopScore)
    {
        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE cards SET slop_score = $ss WHERE file_name = $name;";
        cmd.Parameters.AddWithValue("$ss", slopScore.HasValue ? slopScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.ExecuteNonQuery();
    }

    public void UpdateGalleryCount(string folderPath, string fileName, int galleryCount)
    {
        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE cards SET gallery_count = $cnt WHERE file_name = $name;";
        cmd.Parameters.AddWithValue("$cnt", galleryCount);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLinkStats(string folderPath, string fileName, bool isFork, int forkCount, int siblingCount)
    {
        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE cards SET is_fork = $fork, fork_count = $fc, sibling_count = $sc WHERE file_name = $name;";
        cmd.Parameters.AddWithValue("$fork", isFork ? 1 : 0);
        cmd.Parameters.AddWithValue("$fc", forkCount);
        cmd.Parameters.AddWithValue("$sc", siblingCount);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.ExecuteNonQuery();
    }

    public void UpsertCards(string folderPath, IEnumerable<CardSummary> cards, IEnumerable<string>? removedFileNames = null)
    {
        using var conn = new SqliteConnection(GetFolderDbConnStr(folderPath));
        conn.Open();
        EnsureFolderDbSchema(conn);
        using var tx = conn.BeginTransaction();

        if (removedFileNames is not null)
        {
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM cards WHERE file_name = $name;";
            var pDelName = delCmd.Parameters.Add("$name", SqliteType.Text);
            foreach (var fileName in removedFileNames)
            {
                pDelName.Value = fileName;
                delCmd.ExecuteNonQuery();
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO cards (
                file_name, file_size, last_modified_ticks, spec_version, botbooru_post_id,
                name, creator, tagline, description, personality, scenario, first_mes,
                tags_json, lorebook_count, alt_greeting_count, has_original_tag, gallery_count,
                is_fork, fork_count, sibling_count, slop_score, is_synced
            ) VALUES (
                $file_name, $file_size, $last_modified_ticks, $spec_version, $botbooru_post_id,
                $name, $creator, $tagline, $description, $personality, $scenario, $first_mes,
                $tags_json, $lorebook_count, $alt_greeting_count, $has_original_tag, $gallery_count,
                $is_fork, $fork_count, $sibling_count, $slop_score, $is_synced
            )
            ON CONFLICT(file_name) DO UPDATE SET
                file_size = excluded.file_size,
                last_modified_ticks = excluded.last_modified_ticks,
                spec_version = excluded.spec_version,
                botbooru_post_id = excluded.botbooru_post_id,
                name = excluded.name,
                creator = excluded.creator,
                tagline = excluded.tagline,
                description = excluded.description,
                personality = excluded.personality,
                scenario = excluded.scenario,
                first_mes = excluded.first_mes,
                tags_json = excluded.tags_json,
                lorebook_count = excluded.lorebook_count,
                alt_greeting_count = excluded.alt_greeting_count,
                has_original_tag = excluded.has_original_tag,
                gallery_count = MAX(cards.gallery_count, excluded.gallery_count),
                is_fork = MAX(cards.is_fork, excluded.is_fork),
                fork_count = MAX(cards.fork_count, excluded.fork_count),
                sibling_count = MAX(cards.sibling_count, excluded.sibling_count),
                slop_score = COALESCE(excluded.slop_score, cards.slop_score),
                is_synced = MAX(cards.is_synced, excluded.is_synced);
            """;

        var pFileName = cmd.Parameters.Add("$file_name", SqliteType.Text);
        var pFileSize = cmd.Parameters.Add("$file_size", SqliteType.Integer);
        var pTicks = cmd.Parameters.Add("$last_modified_ticks", SqliteType.Integer);
        var pSpec = cmd.Parameters.Add("$spec_version", SqliteType.Text);
        var pPostId = cmd.Parameters.Add("$botbooru_post_id", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCreator = cmd.Parameters.Add("$creator", SqliteType.Text);
        var pTagline = cmd.Parameters.Add("$tagline", SqliteType.Text);
        var pDesc = cmd.Parameters.Add("$description", SqliteType.Text);
        var pPers = cmd.Parameters.Add("$personality", SqliteType.Text);
        var pScen = cmd.Parameters.Add("$scenario", SqliteType.Text);
        var pFirst = cmd.Parameters.Add("$first_mes", SqliteType.Text);
        var pTags = cmd.Parameters.Add("$tags_json", SqliteType.Text);
        var pLore = cmd.Parameters.Add("$lorebook_count", SqliteType.Integer);
        var pAlt = cmd.Parameters.Add("$alt_greeting_count", SqliteType.Integer);
        var pOrig = cmd.Parameters.Add("$has_original_tag", SqliteType.Integer);
        var pGallery = cmd.Parameters.Add("$gallery_count", SqliteType.Integer);
        var pIsFork = cmd.Parameters.Add("$is_fork", SqliteType.Integer);
        var pForkCount = cmd.Parameters.Add("$fork_count", SqliteType.Integer);
        var pSiblingCount = cmd.Parameters.Add("$sibling_count", SqliteType.Integer);
        var pSlopScore = cmd.Parameters.Add("$slop_score", SqliteType.Real);
        var pIsSynced = cmd.Parameters.Add("$is_synced", SqliteType.Integer);

        foreach (var c in cards)
        {
            pFileName.Value = c.FileName;
            pFileSize.Value = c.FileSize;
            pTicks.Value = c.LastModifiedTicks;
            pSpec.Value = c.SpecVersion;
            pPostId.Value = c.BotbooruPostId.HasValue ? c.BotbooruPostId.Value : DBNull.Value;
            pName.Value = c.Name;
            pCreator.Value = c.Creator;
            pTagline.Value = c.Tagline;
            pDesc.Value = c.Description;
            pPers.Value = c.Personality;
            pScen.Value = c.Scenario;
            pFirst.Value = c.FirstMessage;
            pTags.Value = JsonSerializer.Serialize(c.Tags);
            pLore.Value = c.LorebookCount;
            pAlt.Value = c.AlternateGreetingCount;
            pOrig.Value = c.HasOriginalTag ? 1 : 0;
            pGallery.Value = c.GalleryCount;
            pIsFork.Value = c.IsFork ? 1 : 0;
            pForkCount.Value = c.ForkCount;
            pSiblingCount.Value = c.SiblingCount;
            pSlopScore.Value = c.SlopScore.HasValue ? c.SlopScore.Value : DBNull.Value;
            pIsSynced.Value = c.IsBotbooruSynced ? 1 : 0;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public Dictionary<string, string> GetAllTagCategories()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqliteConnection(_appConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_name, category FROM tag_categories;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    public void UpsertTagCategories(IEnumerable<(string Tag, string Category)> entries)
    {
        using var conn = new SqliteConnection(_appConnStr);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tag_categories (tag_name, category) VALUES ($tag, $cat)
            ON CONFLICT(tag_name) DO UPDATE SET category = excluded.category;
            """;
        var pTag = cmd.Parameters.Add("$tag", SqliteType.Text);
        var pCat = cmd.Parameters.Add("$cat", SqliteType.Text);

        foreach (var (tag, cat) in entries)
        {
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(cat))
                continue;
            pTag.Value = tag.Trim();
            pCat.Value = cat.Trim();
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
