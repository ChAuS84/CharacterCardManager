using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CharacterCardManager.Helpers;
using CharacterCardManager.Models;
using CharacterCardManager.Services;
using Microsoft.Win32;

namespace CharacterCardManager;

public partial class MainWindow : Window
{
    private readonly CardCacheDb _db = new();
    private readonly BotbooruSyncService _botbooru = new();
    private readonly LlmChatService _llm = new();

    private readonly List<CardSummary> _allCards = [];
    private readonly ObservableCollection<CardSummary> _filteredCards = [];
    private readonly ObservableCollection<AiChatBubble> _aiChat = [];
    private Dictionary<string, string> _tagCategories = new(StringComparer.OrdinalIgnoreCase);

    private string? _currentFolder;
    private CharacterCardData? _currentCard;
    private bool _isLoadingEditor;
    private bool _isDirty;
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _llmCts;

    public sealed class AiChatBubble
    {
        public string Role { get; set; } = "user";
        public string SenderLabel => Role == "user" ? "You" : "AI";
        public string SenderColorHex => Role == "user" ? "#63CAB7" : "#BE8CFF";
        public string Content { get; set; } = string.Empty;
    }

    public sealed class TagGroupView
    {
        public string CategoryHeader { get; set; } = string.Empty;
        public string CategoryColorHex { get; set; } = "#6E6E8C";
        public List<TagChipItem> Tags { get; set; } = [];
    }

    public sealed class GalleryItemView
    {
        public string Label { get; set; } = string.Empty;
        public string PreviewUrl { get; set; } = string.Empty;
        public string MainViewUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
    }

    public sealed class TagBrowserEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Uncategorized";
        public string ShortCategory { get; set; } = "Uncat:";
        public string ColorHex { get; set; } = "#6E6E8C";
        public int Count { get; set; }
    }

    public sealed class LinkedCardView
    {
        public long Id { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
        public string RelationColorHex { get; set; } = "#63CAB7";
        public bool IsInFolder { get; set; }
        public bool NotInFolder => !IsInFolder;
        public string DisplayTitle => $"{CharacterName}  (#{Id})";
        public string LocalStatusText => IsInFolder ? "In current folder" : "Not in current folder";
        public string LocalStatusColorHex => IsInFolder ? "#63CAB7" : "#6E6E8C";
    }

    public sealed class LinksCachePayload
    {
        public bool IsFork { get; set; }
        public BotbooruSyncService.BotbooruLinkedPost? ForkMaster { get; set; }
        public List<BotbooruSyncService.BotbooruLinkedPost> Forks { get; set; } = [];
        public List<BotbooruSyncService.BotbooruLinkedPost> Siblings { get; set; } = [];
    }

    private List<TagBrowserEntry> _allTagEntries = [];

    public MainWindow()
    {
        InitializeComponent();
        LstCards.ItemsSource = _filteredCards;
        ItemsAiChat.ItemsSource = _aiChat;
        _tagCategories = _db.GetAllTagCategories();
        _botbooru.SetSession(
            _db.GetSetting("botbooru_cookie"),
            _db.GetSetting("botbooru_ua"),
            _db.GetSetting("botbooru_token"));

        string? savedEp = _db.GetSetting("llm_endpoint");
        if (!string.IsNullOrWhiteSpace(savedEp)) TxtLlmEndpoint.Text = savedEp;
        TxtLlmModel.Text = _db.GetSetting("llm_model") ?? string.Empty;
        TxtLlmApiKey.Text = _db.GetSetting("llm_apikey") ?? string.Empty;

        UpdateLoginButtonState();
        Loaded += MainWindow_Loaded;
    }

    private void UpdateLoginButtonState()
    {
        if (_botbooru.HasSession)
        {
            BtnBotbooruLogin.Content = "Botbooru: Signed In";
            BtnBotbooruLogin.Style = (Style)FindResource("AccentButtonStyle");
        }
        else
        {
            BtnBotbooruLogin.Content = "Botbooru Login";
            BtnBotbooruLogin.Style = (Style)FindResource("DarkButtonStyle");
        }
    }

    private void BotbooruLogin_Click(object sender, RoutedEventArgs e)
    {
        var win = new BotbooruLoginWindow(_db.GetSetting("botbooru_cookie")) { Owner = this };
        if (win.ShowDialog() == true)
        {
            string cookie = win.WasCleared ? string.Empty : (win.ResultCookieHeader ?? string.Empty);
            string ua = win.WasCleared ? string.Empty : (win.ResultUserAgent ?? string.Empty);
            string token = win.WasCleared ? string.Empty : (win.ResultBearerToken ?? string.Empty);
            _db.SetSetting("botbooru_cookie", cookie);
            _db.SetSetting("botbooru_ua", ua);
            _db.SetSetting("botbooru_token", token);
            _botbooru.SetSession(cookie, ua, token);
            UpdateLoginButtonState();
            TxtStatus.Text = _botbooru.HasSession
                ? "Botbooru session saved — NSFW posts & galleries are now accessible."
                : "Botbooru session cleared.";
        }
    }

    private void BrowseBotbooru_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            MessageBox.Show("Open a target card folder first.", "No Folder Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existingIds = new HashSet<long>(_allCards.Where(c => c.BotbooruPostId.HasValue).Select(c => c.BotbooruPostId!.Value));
        var win = new BotbooruBrowserWindow(
            _botbooru,
            _currentFolder,
            existingIds,
            (savedPath, tags) =>
            {
                try
                {
                    var parsed = PngCardCodec.ReadCard(savedPath);
                    var summary = parsed.ToSummary();
                    _db.UpsertCards(_currentFolder, [summary]);

                    var newCats = new List<(string, string)>();
                    foreach (var t in tags)
                    {
                        if (!_tagCategories.TryGetValue(t.Name, out var ex) || !string.Equals(ex, t.Category, StringComparison.OrdinalIgnoreCase))
                        {
                            _tagCategories[t.Name] = t.Category;
                            newCats.Add((t.Name, t.Category));
                        }
                    }
                    if (newCats.Count > 0)
                        _db.UpsertTagCategories(newCats);

                    _allCards.Add(summary);
                    _allCards.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    RebuildTagBrowserIndex();
                    ApplyFilters();
                }
                catch
                {
                    // Ignore parse issues
                }
            })
        {
            Owner = this
        };
        win.Show();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string? lastFolder = _db.GetSetting("last_folder");
        if (!string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder))
        {
            await LoadFolderAsync(lastFolder);
            return;
        }

        // Fallback to SFWCards if running inside the workspace
        string? probe = FindSFWCardsFolder();
        if (probe is not null)
        {
            await LoadFolderAsync(probe);
        }
    }

    private static string? FindSFWCardsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "SFWCards");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Tavern Character Cards Folder",
            InitialDirectory = _currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true)
        {
            await LoadFolderAsync(dlg.FolderName);
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentFolder) && Directory.Exists(_currentFolder))
        {
            await LoadFolderAsync(_currentFolder);
        }
    }

    private async Task LoadFolderAsync(string folderPath)
    {
        _currentFolder = folderPath;
        _db.SetSetting("last_folder", folderPath);
        TxtFolderPath.Text = folderPath;
        TxtFolderPath.Foreground = (System.Windows.Media.Brush)FindResource("TextMainBrush");
        TxtStatus.Text = "Scanning folder...";

        var result = await Task.Run(() =>
        {
            var cached = _db.GetCachedCardsInFolder(folderPath);
            var pngFiles = Directory.EnumerateFiles(folderPath, "*.png", SearchOption.TopDirectoryOnly).ToList();
            var diskSet = new HashSet<string>(pngFiles, StringComparer.OrdinalIgnoreCase);

            var removed = cached.Keys
                .Where(k => !diskSet.Contains(k))
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();
            var toUpsert = new List<CardSummary>();
            var all = new List<CardSummary>(pngFiles.Count);
            int parsedCount = 0;
            int skippedCount = 0;

            foreach (var file in pngFiles)
            {
                var fi = new FileInfo(file);
                if (cached.TryGetValue(file, out var existing) &&
                    existing.FileSize == fi.Length &&
                    existing.LastModifiedTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    all.Add(existing);
                    continue;
                }

                try
                {
                    var card = PngCardCodec.ReadCard(file);
                    var summary = card.ToSummary();
                    toUpsert.Add(summary);
                    all.Add(summary);
                    parsedCount++;
                }
                catch
                {
                    skippedCount++;
                }
            }

            if (toUpsert.Count > 0 || removed.Count > 0)
            {
                _db.UpsertCards(folderPath, toUpsert, removed);
            }

            all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return (all, parsedCount, skippedCount);
        });

        _allCards.Clear();
        _allCards.AddRange(result.all);
        RebuildTagBrowserIndex();
        ApplyFilters();

        TxtStatus.Text = $"Loaded {_allCards.Count} cards ({result.parsedCount} newly parsed, {result.skippedCount} non-card PNGs skipped)";
    }

    private void SearchOrFilter_Changed(object sender, EventArgs e)
    {
        if (IsLoaded)
            ApplyFilters();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Text = string.Empty;
        CmbQuickFilter.SelectedIndex = 0;
    }

    private void ApplyFilters()
    {
        string rawSearch = TxtSearch.Text.Trim();
        int quickMode = CmbQuickFilter.SelectedIndex;

        var includeTerms = new List<string>();
        var excludeTerms = new List<string>();

        if (!string.IsNullOrEmpty(rawSearch))
        {
            foreach (var token in rawSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('-') && token.Length > 1)
                    excludeTerms.Add(token[1..]);
                else
                    includeTerms.Add(token);
            }
        }

        _filteredCards.Clear();
        foreach (var card in _allCards)
        {
            if (quickMode == 1 && card.HasOriginalTag) continue;
            if (quickMode == 2 && !card.HasOriginalTag) continue;
            if (quickMode == 3 && card.LorebookCount == 0) continue;
            if (quickMode == 4 && card.SpecVersion != "v3") continue;
            if (quickMode == 5 && !card.BotbooruPostId.HasValue) continue;
            if (quickMode == 6 && (card.IsBotbooruSynced || !card.BotbooruPostId.HasValue)) continue;
            if (quickMode == 7 && !card.IsBotbooruSynced) continue;

            if (excludeTerms.Count > 0 && excludeTerms.Any(ex => MatchesCard(card, ex)))
                continue;

            if (includeTerms.Count > 0 && !includeTerms.All(inc => MatchesCard(card, inc)))
                continue;

            _filteredCards.Add(card);
        }

        TxtCount.Text = $"{_filteredCards.Count} / {_allCards.Count} cards";
        TxtListVisibleCount.Text = $"Showing {_filteredCards.Count} / {_allCards.Count} cards";

        if (_filteredCards.Count > 0)
            LstCards.ScrollIntoView(_filteredCards[0]);
    }

    private static bool MatchesCard(CardSummary card, string term)
    {
        if (term.StartsWith('#') && long.TryParse(term[1..], out var id))
            return card.BotbooruPostId == id;

        if (term.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = term[4..];
            return !string.IsNullOrEmpty(tag) &&
                   card.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }

        if (term.StartsWith("creator:", StringComparison.OrdinalIgnoreCase))
        {
            string creator = term[8..];
            return !string.IsNullOrEmpty(creator) &&
                   card.Creator.Contains(creator, StringComparison.OrdinalIgnoreCase);
        }

        if (term.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            string name = term[5..];
            return !string.IsNullOrEmpty(name) &&
                   card.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
        }

        return card.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || card.Creator.Contains(term, StringComparison.OrdinalIgnoreCase)
            || card.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || card.Tagline.Contains(term, StringComparison.OrdinalIgnoreCase)
            || card.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || card.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void LstCards_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int selCount = LstCards.SelectedItems.Count;
        TxtListSelectedCount.Text = selCount > 1 ? $"{selCount} selected" : string.Empty;

        if (LstCards.SelectedItem is not CardSummary summary)
        {
            if (LstCards.SelectedItems.Count == 0)
            {
                _currentCard = null;
                EditorScroll.Visibility = Visibility.Collapsed;
                TxtEmptySelection.Visibility = Visibility.Visible;
            }
            return;
        }

        LoadCardIntoEditor(summary.FilePath);
    }

    private void LoadCardIntoEditor(string filePath)
    {
        try
        {
            var card = PngCardCodec.ReadCard(filePath);
            _currentCard = card;
            _isLoadingEditor = true;

            TxtEmptySelection.Visibility = Visibility.Collapsed;
            EditorScroll.Visibility = Visibility.Visible;

            ImgPreview.Source = ThumbnailConverter.LoadFullPreview(card.FilePath, 240);
            TxtHeaderName.Text = string.IsNullOrWhiteSpace(card.Name) ? card.FileName : card.Name;
            TxtHeaderTagline.Text = card.Tagline;
            TxtHeaderTagline.Visibility = string.IsNullOrWhiteSpace(card.Tagline) ? Visibility.Collapsed : Visibility.Visible;

            var summary = _allCards.FirstOrDefault(c => string.Equals(c.FilePath, card.FilePath, StringComparison.OrdinalIgnoreCase));
            BtnHeaderSync.Visibility = card.BotbooruPostId.HasValue && summary?.IsBotbooruSynced != true
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateHeaderMeta(card, summary?.GalleryCount ?? 0, summary?.IsFork ?? false, summary?.ForkCount ?? 0, summary?.SiblingCount ?? 0);
            UpdateWritingStyleDisplay(summary?.SlopScore);
            TxtHeaderFile.Text = card.FilePath;
            TxtBotbooruLink.Text = card.BotbooruPostId.HasValue ? $"botbooru.com/character/{card.BotbooruPostId}" : string.Empty;

            EdName.Text = card.Name;
            EdCreator.Text = card.Creator;
            EdVersion.Text = card.CharacterVersion;
            EdTagline.Text = card.Tagline;
            EdNickname.Text = card.Nickname;
            EdDescription.Text = card.Description;
            EdPersonality.Text = card.Personality;
            EdScenario.Text = card.Scenario;

            EdFirstMessage.Text = card.FirstMessage;
            EdSystemPrompt.Text = card.SystemPrompt;
            EdPostHistory.Text = card.PostHistoryInstructions;
            EdMesExample.Text = card.MessageExample;
            EdCreatorNotes.Text = card.CreatorNotes;

            RefreshAltGreetingsList(0);
            RefreshTagGroups();
            _aiChat.Clear();
            _llmCts?.Cancel();
            BtnLoadGallery.IsEnabled = card.BotbooruPostId.HasValue;
            if (_currentFolder is not null && card.BotbooruPostId is long pid && TryLoadCachedGallery(_currentFolder, pid))
            {
                // Loaded from portable .cardmanager\galleries\<pid>\
            }
            else
            {
                ItemsGallery.ItemsSource = null;
                TxtGalleryInfo.Text = card.BotbooruPostId.HasValue
                    ? "Click 'Load Botbooru Gallery' to view & cache extra images for this card."
                    : "No Botbooru ID on this card (you can still replace from a local PNG).";
            }

            BtnLoadLinks.IsEnabled = card.BotbooruPostId.HasValue;
            if (_currentFolder is not null && card.BotbooruPostId is long lpid && TryLoadCachedLinks(_currentFolder, lpid))
            {
                // Loaded from portable .cardmanager\links\<lpid>.json
            }
            else
            {
                ItemsLinks.ItemsSource = null;
                BannerForkStatus.Visibility = Visibility.Collapsed;
                TxtLinksStatus.Text = card.BotbooruPostId.HasValue
                    ? "Click 'Load Links & Forks' to check fork/sibling relationships."
                    : "No Botbooru ID on this card.";
            }
            SetDirty(false);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Failed to load card: {ex.Message}";
        }
        finally
        {
            _isLoadingEditor = false;
        }
    }

    private void RefreshAltGreetingsList(int selectIndex)
    {
        LstAltGreetings.Items.Clear();
        if (_currentCard is null) return;

        for (int i = 0; i < _currentCard.AlternateGreetings.Count; i++)
        {
            string preview = _currentCard.AlternateGreetings[i].Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 28) preview = preview[..28] + "…";
            LstAltGreetings.Items.Add($"#{i + 1}: {preview}");
        }

        TxtAltGreetLabel.Text = $"Alternate Greetings ({_currentCard.AlternateGreetings.Count})";

        if (_currentCard.AlternateGreetings.Count > 0)
        {
            int idx = Math.Clamp(selectIndex, 0, _currentCard.AlternateGreetings.Count - 1);
            LstAltGreetings.SelectedIndex = idx;
            EdAltGreeting.Text = _currentCard.AlternateGreetings[idx];
            EdAltGreeting.IsEnabled = true;
        }
        else
        {
            EdAltGreeting.Text = string.Empty;
            EdAltGreeting.IsEnabled = false;
        }
    }

    private void RefreshTagGroups()
    {
        if (_currentCard is null)
        {
            ItemsTagGroups.ItemsSource = null;
            return;
        }

        var groups = new Dictionary<string, List<TagChipItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in _currentCard.Tags)
        {
            string cat = _tagCategories.TryGetValue(tag, out var found) ? found : "Uncategorized";
            if (!groups.TryGetValue(cat, out var list))
            {
                list = [];
                groups[cat] = list;
            }
            list.Add(new TagChipItem
            {
                Name = tag,
                Category = cat,
                ColorHex = BotbooruSyncService.GetCategoryColorHex(cat)
            });
        }

        var ordered = groups
            .OrderBy(kv => BotbooruSyncService.GetCategorySortOrder(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new TagGroupView
            {
                CategoryHeader = $"{kv.Key} ({kv.Value.Count})",
                CategoryColorHex = BotbooruSyncService.GetCategoryColorHex(kv.Key),
                Tags = kv.Value
            })
            .ToList();

        ItemsTagGroups.ItemsSource = ordered;
    }

    private void EditorField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_isLoadingEditor && _currentCard is not null)
            SetDirty(true);
    }

    private void LstAltGreetings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentCard is null) return;
        int idx = LstAltGreetings.SelectedIndex;
        if (idx >= 0 && idx < _currentCard.AlternateGreetings.Count)
        {
            _isLoadingEditor = true;
            EdAltGreeting.Text = _currentCard.AlternateGreetings[idx];
            EdAltGreeting.IsEnabled = true;
            _isLoadingEditor = false;
        }
    }

    private void EdAltGreeting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingEditor || _currentCard is null) return;
        int idx = LstAltGreetings.SelectedIndex;
        if (idx >= 0 && idx < _currentCard.AlternateGreetings.Count)
        {
            _currentCard.AlternateGreetings[idx] = EdAltGreeting.Text;
            SetDirty(true);
        }
    }

    private void AddAltGreeting_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null) return;
        _currentCard.AlternateGreetings.Add(string.Empty);
        RefreshAltGreetingsList(_currentCard.AlternateGreetings.Count - 1);
        SetDirty(true);
    }

    private void RemoveAltGreeting_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null) return;
        int idx = LstAltGreetings.SelectedIndex;
        if (idx >= 0 && idx < _currentCard.AlternateGreetings.Count)
        {
            _currentCard.AlternateGreetings.RemoveAt(idx);
            RefreshAltGreetingsList(idx - 1);
            SetDirty(true);
        }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => CommitNewTag();

    private void TxtNewTag_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNewTag();
            e.Handled = true;
        }
    }

    private void CommitNewTag()
    {
        if (_currentCard is null) return;
        string raw = TxtNewTag.Text.Trim();
        if (string.IsNullOrEmpty(raw)) return;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!_currentCard.Tags.Contains(part, StringComparer.OrdinalIgnoreCase))
                _currentCard.Tags.Add(part);
        }

        TxtNewTag.Text = string.Empty;
        RefreshTagGroups();
        SetDirty(true);
    }

    private void RemoveTag_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentCard is null) return;
        if (sender is FrameworkElement fe && fe.Tag is string tagName)
        {
            _currentCard.Tags.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
            RefreshTagGroups();
            SetDirty(true);
        }
    }

    private void TagChip_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            AppendSearchTerm("tag:" + tb.Text);
    }

    private void TagChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            AppendSearchTerm("-tag:" + tb.Text);
    }

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        BtnSave.Content = dirty ? "Save to PNG *" : "Save to PNG";
        BtnSave.IsEnabled = dirty;
        BtnRevert.IsEnabled = dirty;
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is not null)
            LoadCardIntoEditor(_currentCard.FilePath);
    }

    private void SyncEditorFieldsToCard()
    {
        if (_currentCard is null) return;
        _currentCard.Name = EdName.Text;
        _currentCard.Creator = EdCreator.Text;
        _currentCard.CharacterVersion = EdVersion.Text;
        _currentCard.Tagline = EdTagline.Text;
        _currentCard.Nickname = EdNickname.Text;
        _currentCard.Description = EdDescription.Text;
        _currentCard.Personality = EdPersonality.Text;
        _currentCard.Scenario = EdScenario.Text;
        _currentCard.FirstMessage = EdFirstMessage.Text;
        _currentCard.SystemPrompt = EdSystemPrompt.Text;
        _currentCard.PostHistoryInstructions = EdPostHistory.Text;
        _currentCard.MessageExample = EdMesExample.Text;
        _currentCard.CreatorNotes = EdCreatorNotes.Text;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null) return;

        SyncEditorFieldsToCard();

        try
        {
            PngCardCodec.WriteCard(_currentCard);
            var updatedSummary = _currentCard.ToSummary();
            if (_currentFolder is not null)
                _db.UpsertCards(_currentFolder, [updatedSummary]);

            int allIdx = _allCards.FindIndex(c => string.Equals(c.FilePath, updatedSummary.FilePath, StringComparison.OrdinalIgnoreCase));
            if (allIdx >= 0)
            {
                updatedSummary.GalleryCount = _allCards[allIdx].GalleryCount;
                updatedSummary.IsFork = _allCards[allIdx].IsFork;
                updatedSummary.ForkCount = _allCards[allIdx].ForkCount;
                updatedSummary.SiblingCount = _allCards[allIdx].SiblingCount;
                updatedSummary.SlopScore = _allCards[allIdx].SlopScore;
                updatedSummary.IsBotbooruSynced = _allCards[allIdx].IsBotbooruSynced;
                _allCards[allIdx] = updatedSummary;
            }

            int filtIdx = _filteredCards.ToList().FindIndex(c => string.Equals(c.FilePath, updatedSummary.FilePath, StringComparison.OrdinalIgnoreCase));
            if (filtIdx >= 0)
            {
                _filteredCards[filtIdx] = updatedSummary;
                LstCards.SelectedItem = updatedSummary;
            }

            TxtHeaderName.Text = _currentCard.Name;
            TxtHeaderTagline.Text = _currentCard.Tagline;
            TxtHeaderTagline.Visibility = string.IsNullOrWhiteSpace(_currentCard.Tagline) ? Visibility.Collapsed : Visibility.Visible;
            UpdateHeaderMeta(_currentCard, updatedSummary.GalleryCount, updatedSummary.IsFork, updatedSummary.ForkCount, updatedSummary.SiblingCount);
            SetDirty(false);
            TxtStatus.Text = $"Saved changes to {_currentCard.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save PNG:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TxtBotbooruLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentCard?.BotbooruPostId is long id)
        {
            OpenUrl($"https://botbooru.com/character/{id}");
        }
    }

    private void TxtHeaderFile_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentCard is not null && !string.IsNullOrWhiteSpace(_currentCard.FilePath))
        {
            Clipboard.SetText(_currentCard.FilePath);
            TxtStatus.Text = $"Copied file path to clipboard: {_currentCard.FilePath}";
        }
    }

    private void OpenSelectedBotbooru_Click(object sender, RoutedEventArgs e)
    {
        if (LstCards.SelectedItem is CardSummary s && s.BotbooruPostId is long id)
        {
            OpenUrl($"https://botbooru.com/character/{id}");
        }
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (LstCards.SelectedItem is CardSummary s && File.Exists(s.FilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{s.FilePath}\"");
        }
    }

    private void RenameCard_Click(object sender, RoutedEventArgs e)
    {
        var target = (LstCards.SelectedItem as CardSummary)
            ?? (_currentCard is not null ? _allCards.FirstOrDefault(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase)) : null);

        if (target is null || _currentFolder is null || !File.Exists(target.FilePath))
            return;

        string suggestedStem = string.IsNullOrWhiteSpace(target.Name) ? Path.GetFileNameWithoutExtension(target.FileName) : target.Name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        suggestedStem = string.Concat(suggestedStem.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        string suggestedName = target.BotbooruPostId.HasValue
            ? $"{suggestedStem}-{target.BotbooruPostId.Value}.png"
            : $"{suggestedStem}.png";

        var dlg = new Window
        {
            Title = "Rename Card File",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 540,
            Height = 185,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("BgDeepBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextMainBrush")
        };

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "New filename (.png):",
            Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var txtName = new TextBox { Text = target.FileName, FontSize = 13 };
        txtName.SelectAll();
        root.Children.Add(txtName);

        var btnRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var btnSuggest = new Button
        {
            Content = $"Use \"{suggestedName}\"",
            Style = (Style)FindResource("VioletButtonStyle"),
            Padding = new Thickness(10, 5, 10, 5)
        };
        btnSuggest.Click += (_, _) =>
        {
            txtName.Text = suggestedName;
            txtName.Focus();
        };
        Grid.SetColumn(btnSuggest, 0);
        btnRow.Children.Add(btnSuggest);

        var btnCancel = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("DarkButtonStyle"),
            Padding = new Thickness(14, 5, 14, 5)
        };
        btnCancel.Click += (_, _) => dlg.DialogResult = false;
        Grid.SetColumn(btnCancel, 2);
        btnRow.Children.Add(btnCancel);

        var btnOk = new Button
        {
            Content = "Rename",
            Style = (Style)FindResource("AccentButtonStyle"),
            Padding = new Thickness(16, 5, 16, 5),
            IsDefault = true
        };
        btnOk.Click += (_, _) => dlg.DialogResult = true;
        Grid.SetColumn(btnOk, 4);
        btnRow.Children.Add(btnOk);

        root.Children.Add(btnRow);
        dlg.Content = root;
        txtName.Focus();

        if (dlg.ShowDialog() != true) return;

        string newName = txtName.Text.Trim();
        newName = string.Concat(newName.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        if (!newName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            newName += ".png";

        if (string.IsNullOrWhiteSpace(newName) || newName == ".png" ||
            string.Equals(newName, target.FileName, StringComparison.OrdinalIgnoreCase))
            return;

        string dir = Path.GetDirectoryName(target.FilePath) ?? _currentFolder;
        string newPath = Path.Combine(dir, newName);
        if (File.Exists(newPath))
        {
            MessageBox.Show($"A file named '{newName}' already exists in this folder.", "Rename Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string oldPath = target.FilePath;
            string oldName = target.FileName;
            File.Move(oldPath, newPath);
            ThumbnailConverter.Invalidate(oldPath);

            target.FilePath = newPath;
            target.FileName = newName;
            _db.UpsertCards(_currentFolder, [target], [oldName]);
            target.NotifyUpdated();

            if (_currentCard is not null && string.Equals(_currentCard.FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentCard.FilePath = newPath;
                _currentCard.FileName = newName;
                TxtHeaderFile.Text = newPath;
            }

            TxtStatus.Text = $"Renamed '{oldName}' → '{newName}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to rename file:\n{ex.Message}", "Rename Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = LstCards.SelectedItems.Cast<CardSummary>().ToList();
        if (selected.Count == 0) return;

        var dlg = new OpenFolderDialog { Title = $"Move {selected.Count} card(s) to folder..." };
        if (dlg.ShowDialog() != true) return;

        int moved = 0;
        var removedNames = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                string dest = Path.Combine(dlg.FolderName, item.FileName);
                File.Move(item.FilePath, dest, overwrite: false);
                removedNames.Add(item.FileName);
                _allCards.Remove(item);
                moved++;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error moving {item.FileName}: {ex.Message}";
            }
        }

        if (removedNames.Count > 0 && _currentFolder is not null)
            _db.UpsertCards(_currentFolder, [], removedNames);

        ApplyFilters();
        TxtStatus.Text = $"Moved {moved} card(s) to {dlg.FolderName}";
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = LstCards.SelectedItems.Cast<CardSummary>().ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(
            $"Permanently delete {selected.Count} selected PNG file(s) from disk?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var removedNames = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                if (File.Exists(item.FilePath))
                    File.Delete(item.FilePath);
                removedNames.Add(item.FileName);
                _allCards.Remove(item);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error deleting {item.FileName}: {ex.Message}";
            }
        }

        if (removedNames.Count > 0 && _currentFolder is not null)
            _db.UpsertCards(_currentFolder, [], removedNames);

        ApplyFilters();
        TxtStatus.Text = $"Deleted {removedNames.Count} card(s)";
    }

    private async void SyncSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = LstCards.SelectedItems.Cast<CardSummary>()
            .Where(c => c.BotbooruPostId.HasValue)
            .ToList();

        if (targets.Count == 0)
        {
            TxtStatus.Text = "Select one or more cards with a Botbooru ID (#) to sync.";
            return;
        }

        await SyncCardsFromBotbooruAsync(targets);
    }

    private void CancelSync_Click(object sender, RoutedEventArgs e)
    {
        _syncCts?.Cancel();
    }

    private async Task SyncCardsFromBotbooruAsync(List<CardSummary> targets)
    {
        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        BtnCancelSync.Visibility = Visibility.Visible;

        int done = 0;
        int learnedTags = 0;
        var batchCategories = new List<(string Tag, string Category)>();

        try
        {
            foreach (var card in targets)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                TxtStatus.Text = $"Syncing Botbooru tags ({done}/{targets.Count}): {card.Name}...";

                try
                {
                    var tags = await _botbooru.FetchPostTagsAsync(card.BotbooruPostId!.Value, ct);
                    foreach (var t in tags)
                    {
                        if (!_tagCategories.TryGetValue(t.Name, out var existing) ||
                            !string.Equals(existing, t.Category, StringComparison.OrdinalIgnoreCase))
                        {
                            _tagCategories[t.Name] = t.Category;
                            batchCategories.Add((t.Name, t.Category));
                            learnedTags++;
                        }
                    }

                    if (batchCategories.Count >= 50)
                    {
                        _db.UpsertTagCategories(batchCategories);
                        batchCategories.Clear();
                    }

                    if (!card.IsBotbooruSynced && _currentFolder is not null)
                    {
                        card.IsBotbooruSynced = true;
                        _db.MarkSynced(_currentFolder, card.FileName);
                        RefreshFilteredItem(card);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip transient 404/network errors on individual posts
                }

                await Task.Delay(120, ct);
            }

            if (batchCategories.Count > 0)
                _db.UpsertTagCategories(batchCategories);

            RefreshTagGroups();
            RebuildTagBrowserIndex();
            TxtStatus.Text = $"Synced {done} card(s) from Botbooru — learned/updated {learnedTags} tag categories.";
        }
        catch (OperationCanceledException)
        {
            if (batchCategories.Count > 0)
                _db.UpsertTagCategories(batchCategories);
            RefreshTagGroups();
            TxtStatus.Text = $"Sync cancelled at {done}/{targets.Count} cards.";
        }
        finally
        {
            _syncCts = null;
            BtnCancelSync.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateHeaderMeta(CharacterCardData card, int galleryCount, bool isFork, int forkCount, int siblingCount)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(card.Creator) ? "Unknown creator" : card.Creator,
            card.SpecVersion.ToUpperInvariant(),
            $"Lorebook: {card.LorebookCount}",
            $"Gallery: {galleryCount}"
        };
        if (isFork)
            parts.Add("Fork");
        else if (forkCount > 0)
            parts.Add($"Master ({forkCount} fork{(forkCount == 1 ? "" : "s")})");
        if (siblingCount > 0)
            parts.Add($"Siblings: {siblingCount}");

        TxtHeaderMeta.Text = string.Join(" • ", parts);
    }

    private void UpdateWritingStyleDisplay(double? slopScore)
    {
        if (!slopScore.HasValue || double.IsNaN(slopScore.Value))
        {
            PanelWritingStyle.Visibility = Visibility.Collapsed;
            return;
        }

        double raw = slopScore.Value;
        double originality = Math.Clamp(100.0 - raw, 0.0, 100.0);
        string word = raw > 60 ? "Common" : (raw > 30 ? "Mixed" : "Original");
        string hex = raw > 60 ? "#8B5CF6" : (raw > 30 ? "#0EA5E9" : "#16A34A");

        TxtWritingStyle.Text = $"Writing style: {word} ({raw:0.0})";
        BarWritingStyle.Width = 140.0 * (originality / 100.0);
        BarWritingStyle.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        PanelWritingStyle.Visibility = Visibility.Visible;
    }

    private void SaveCurrentCardSlopScore(double? slopScore)
    {
        if (_currentCard is null || !slopScore.HasValue) return;
        if (_currentFolder is not null)
            _db.UpdateSlopScore(_currentFolder, _currentCard.FileName, slopScore);

        var summary = _allCards.FirstOrDefault(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase));
        if (summary is not null)
            summary.SlopScore = slopScore;

        UpdateWritingStyleDisplay(slopScore);
    }

    private void UpdateCurrentCardGalleryCount(int galleryCount)
    {
        if (_currentCard is null) return;

        if (_currentFolder is not null)
            _db.UpdateGalleryCount(_currentFolder, _currentCard.FileName, galleryCount);

        int allIdx = _allCards.FindIndex(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase));
        var summary = allIdx >= 0 ? _allCards[allIdx] : null;
        if (summary is not null)
        {
            if (summary.GalleryCount != galleryCount)
            {
                summary.GalleryCount = galleryCount;
                RefreshFilteredItem(summary);
            }
            UpdateHeaderMeta(_currentCard, summary.GalleryCount, summary.IsFork, summary.ForkCount, summary.SiblingCount);
        }
        else
        {
            UpdateHeaderMeta(_currentCard, galleryCount, false, 0, 0);
        }
    }

    private void UpdateCurrentCardLinkStats(bool isFork, int forkCount, int siblingCount)
    {
        if (_currentCard is null) return;

        if (_currentFolder is not null)
            _db.UpdateLinkStats(_currentFolder, _currentCard.FileName, isFork, forkCount, siblingCount);

        int allIdx = _allCards.FindIndex(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase));
        var summary = allIdx >= 0 ? _allCards[allIdx] : null;
        if (summary is not null)
        {
            if (summary.IsFork != isFork || summary.ForkCount != forkCount || summary.SiblingCount != siblingCount)
            {
                summary.IsFork = isFork;
                summary.ForkCount = forkCount;
                summary.SiblingCount = siblingCount;
                RefreshFilteredItem(summary);
            }
            UpdateHeaderMeta(_currentCard, summary.GalleryCount, summary.IsFork, summary.ForkCount, summary.SiblingCount);
        }
        else
        {
            UpdateHeaderMeta(_currentCard, 0, isFork, forkCount, siblingCount);
        }
    }

    private void RefreshFilteredItem(CardSummary summary)
    {
        summary.NotifyUpdated();
    }

    private bool TryLoadCachedGallery(string folderPath, long postId)
    {
        string dir = CardCacheDb.GetFolderGalleryDir(folderPath, postId);
        string metaPath = Path.Combine(dir, "gallery.json");
        if (!File.Exists(metaPath)) return false;

        try
        {
            var cached = JsonSerializer.Deserialize<List<GalleryItemView>>(File.ReadAllText(metaPath));
            if (cached is null || cached.Count == 0) return false;

            foreach (var item in cached)
            {
                if (!string.IsNullOrEmpty(item.PreviewUrl) && !Path.IsPathRooted(item.PreviewUrl))
                    item.PreviewUrl = Path.Combine(dir, item.PreviewUrl);
            }

            ItemsGallery.ItemsSource = cached;
            int extraCount = cached.Count(x => x.Label != "Main Card Image");
            UpdateCurrentCardGalleryCount(extraCount);
            TxtGalleryInfo.Text = $"Loaded {cached.Count} cached image(s) from .cardmanager\\galleries\\{postId}.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void LoadGallery_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard?.BotbooruPostId is not long postId) return;

        BtnLoadGallery.IsEnabled = false;
        TxtGalleryInfo.Text = $"Fetching gallery for #{postId}...";

        try
        {
            var details = await _botbooru.FetchPostDetailsAsync(postId);

            // Also cache any tag categories we just received
            if (details.Tags.Count > 0)
            {
                var newlyLearned = new List<(string, string)>();
                foreach (var t in details.Tags)
                {
                    if (!_tagCategories.TryGetValue(t.Name, out var ex) || !string.Equals(ex, t.Category, StringComparison.OrdinalIgnoreCase))
                    {
                        _tagCategories[t.Name] = t.Category;
                        newlyLearned.Add((t.Name, t.Category));
                    }
                }
                if (newlyLearned.Count > 0)
                {
                    _db.UpsertTagCategories(newlyLearned);
                    RefreshTagGroups();
                }
            }

            // Fill missing tagline from Botbooru if empty locally
            if (string.IsNullOrWhiteSpace(EdTagline.Text) && !string.IsNullOrWhiteSpace(details.Tagline))
            {
                EdTagline.Text = details.Tagline;
                SetDirty(true);
            }

            var items = new List<GalleryItemView>();
            if (!string.IsNullOrWhiteSpace(details.MainImageUrl))
            {
                items.Add(new GalleryItemView
                {
                    Label = "Main Card Image",
                    PreviewUrl = details.MainImageUrl,
                    MainViewUrl = details.MainImageUrl,
                    DownloadUrl = details.MainImageUrl
                });
            }

            foreach (var g in details.GalleryImages.OrderBy(x => x.Position))
            {
                items.Add(new GalleryItemView
                {
                    Label = $"Gallery #{g.Position + 1} ({g.Width}×{g.Height})",
                    PreviewUrl = g.PreviewUrl,
                    MainViewUrl = g.MainViewUrl,
                    DownloadUrl = g.DownloadUrl
                });
            }

            // Cache previews locally inside <CardFolder>\.cardmanager\galleries\<postId>\
            if (_currentFolder is not null && items.Count > 0)
            {
                string galleryDir = CardCacheDb.GetFolderGalleryDir(_currentFolder, postId);
                Directory.CreateDirectory(galleryDir);

                var toSave = new List<GalleryItemView>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    string fileName = $"img_{i}.png";
                    string localPath = Path.Combine(galleryDir, fileName);

                    try
                    {
                        byte[] previewBytes = await _botbooru.DownloadBytesAsync(item.PreviewUrl);
                        await File.WriteAllBytesAsync(localPath, previewBytes);
                        item.PreviewUrl = localPath;
                        toSave.Add(new GalleryItemView
                        {
                            Label = item.Label,
                            PreviewUrl = fileName,
                            MainViewUrl = item.MainViewUrl,
                            DownloadUrl = item.DownloadUrl
                        });
                    }
                    catch
                    {
                        toSave.Add(item);
                    }
                }

                await File.WriteAllTextAsync(
                    Path.Combine(galleryDir, "gallery.json"),
                    JsonSerializer.Serialize(toSave));
            }

            ItemsGallery.ItemsSource = items;
            UpdateCurrentCardGalleryCount(details.GalleryImages.Count);
            SaveCurrentCardSlopScore(details.SlopScore);
            TxtGalleryInfo.Text = items.Count > 0
                ? $"Loaded & cached {items.Count} image(s) for #{postId}."
                : $"No gallery images found for #{postId}.";
        }
        catch (Exception ex)
        {
            TxtGalleryInfo.Text = ex.Message.Contains("404") && !_botbooru.HasSession
                ? $"Post #{postId} returned 404 — if this is an NSFW card, click 'Botbooru Login' first."
                : $"Failed to load gallery: {ex.Message}";
        }
        finally
        {
            BtnLoadGallery.IsEnabled = _currentCard?.BotbooruPostId.HasValue == true;
        }
    }

    private async void UseGalleryImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null) return;
        if (sender is not Button btn || btn.Tag is not string url || string.IsNullOrWhiteSpace(url)) return;

        btn.IsEnabled = false;
        TxtStatus.Text = "Downloading selected gallery PNG...";

        try
        {
            byte[] pngBytes = await _botbooru.DownloadBytesAsync(url);
            ApplyNewCardImage(pngBytes);
            TxtStatus.Text = $"Embedded card metadata into new image for {_currentCard.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply gallery image:\n{ex.Message}", "Image Swap Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void ReplaceLocalImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null) return;

        var dlg = new OpenFileDialog
        {
            Title = "Select a PNG Image to Embed This Card Into",
            Filter = "PNG Images (*.png)|*.png"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            byte[] pngBytes = File.ReadAllBytes(dlg.FileName);
            ApplyNewCardImage(pngBytes);
            TxtStatus.Text = $"Replaced card image using {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to replace image:\n{ex.Message}", "Image Swap Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyNewCardImage(byte[] newPngBytes)
    {
        if (_currentCard is null) return;

        SyncEditorFieldsToCard();
        PngCardCodec.WriteCard(_currentCard, newPngBytes);
        ThumbnailConverter.Invalidate(_currentCard.FilePath);

        ImgPreview.Source = ThumbnailConverter.LoadFullPreview(_currentCard.FilePath, 240);

        var updatedSummary = _currentCard.ToSummary();
        if (_currentFolder is not null)
            _db.UpsertCards(_currentFolder, [updatedSummary]);

        int allIdx = _allCards.FindIndex(c => string.Equals(c.FilePath, updatedSummary.FilePath, StringComparison.OrdinalIgnoreCase));
        if (allIdx >= 0) _allCards[allIdx] = updatedSummary;

        int filtIdx = _filteredCards.ToList().FindIndex(c => string.Equals(c.FilePath, updatedSummary.FilePath, StringComparison.OrdinalIgnoreCase));
        if (filtIdx >= 0)
        {
            _filteredCards[filtIdx] = updatedSummary;
            LstCards.SelectedItem = updatedSummary;
        }

        SetDirty(false);
    }

    private void ImgPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentCard is not null && File.Exists(_currentCard.FilePath))
            ShowImageLightbox(_currentCard.FilePath, _currentCard.Name, isLocalFile: true);
    }

    private void GalleryImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
            ShowImageLightbox(url, _currentCard?.Name ?? "Gallery Preview", isLocalFile: false);
    }

    private void ShowImageLightbox(string source, string title, bool isLocalFile)
    {
        var win = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900,
            Height = 900,
            Background = (System.Windows.Media.Brush)FindResource("BgDeepBrush")
        };

        var img = new Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin = new Thickness(12),
            Cursor = Cursors.Hand
        };

        if (isLocalFile)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(source, UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
        }
        else
        {
            img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(source, UriKind.Absolute));
        }

        img.MouseLeftButtonUp += (_, _) => win.Close();
        win.KeyDown += (_, ke) => { if (ke.Key == Key.Escape) win.Close(); };
        win.Content = img;
        win.ShowDialog();
    }

    private void ToggleTagBrowser_Click(object sender, RoutedEventArgs e)
    {
        bool isOpen = ColTagBrowser.Width.Value > 0;
        ColTagBrowser.Width = isOpen ? new GridLength(0) : new GridLength(275);
        BtnTagBrowser.Style = (Style)FindResource(isOpen ? "VioletButtonStyle" : "AccentButtonStyle");
    }

    private void TagBrowserFilter_Changed(object sender, EventArgs e)
    {
        if (IsLoaded)
            RefreshTagBrowserList();
    }

    private void RebuildTagBrowserIndex()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            foreach (var tag in card.Tags)
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        _allTagEntries = counts
            .Select(kv =>
            {
                string cat = _tagCategories.TryGetValue(kv.Key, out var found) ? found : "Uncategorized";
                return new TagBrowserEntry
                {
                    Name = kv.Key,
                    Category = cat,
                    ShortCategory = GetShortCategoryLabel(cat),
                    ColorHex = BotbooruSyncService.GetCategoryColorHex(cat),
                    Count = kv.Value
                };
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RefreshTagBrowserList();
    }

    private static string GetShortCategoryLabel(string category) => category.ToLowerInvariant() switch
    {
        "copyright" => "Copy:",
        "character" or "characters" => "Char:",
        "scenarios" => "Scen:",
        "settings" => "Sett:",
        "activities" => "Act:",
        "general" => "Gen:",
        "writer" => "Writer:",
        "artist" => "Artist:",
        "language" => "Lang:",
        "meta" => "Meta:",
        "auto" => "Auto:",
        "nsfl" => "NSFL:",
        _ => "Uncat:"
    };

    private void RefreshTagBrowserList()
    {
        string selectedCat = (CmbTagCategory.SelectedItem as ComboBoxItem)?.Content as string ?? "All Categories";
        string filterText = TxtTagFilter.Text.Trim();

        IEnumerable<TagBrowserEntry> query = _allTagEntries;
        if (selectedCat != "All Categories")
        {
            query = query.Where(t => string.Equals(t.Category, selectedCat, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(filterText))
        {
            query = query.Where(t => t.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase));
        }

        LstTagBrowser.ItemsSource = query.ToList();
    }

    private void TagBrowserInclude_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            AppendSearchTerm("tag:" + tag);
    }

    private void TagBrowserIncludeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            AppendSearchTerm("tag:" + tag);
    }

    private void TagBrowserExcludeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            AppendSearchTerm("-tag:" + tag);
    }

    private void AppendSearchTerm(string term)
    {
        if (!TxtSearch.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            TxtSearch.Text = string.IsNullOrWhiteSpace(TxtSearch.Text) ? term : $"{TxtSearch.Text.Trim()} {term}";
    }

    private bool TryLoadCachedLinks(string folderPath, long postId)
    {
        string path = Path.Combine(CardCacheDb.GetFolderCacheDir(folderPath), "links", $"{postId}.json");
        if (!File.Exists(path)) return false;

        try
        {
            var payload = JsonSerializer.Deserialize<LinksCachePayload>(File.ReadAllText(path));
            if (payload is null) return false;
            ApplyLinksPayload(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyLinksPayload(LinksCachePayload payload)
    {
        var folderIds = new HashSet<long>(_allCards.Where(c => c.BotbooruPostId.HasValue).Select(c => c.BotbooruPostId!.Value));
        var list = new List<LinkedCardView>();

        if (payload.ForkMaster is not null)
        {
            list.Add(new LinkedCardView
            {
                Id = payload.ForkMaster.Id,
                CharacterName = payload.ForkMaster.CharacterName,
                Relation = "Master",
                RelationColorHex = "#BE8CFF",
                IsInFolder = folderIds.Contains(payload.ForkMaster.Id)
            });
        }

        foreach (var f in payload.Forks)
        {
            list.Add(new LinkedCardView
            {
                Id = f.Id,
                CharacterName = f.CharacterName,
                Relation = "Fork",
                RelationColorHex = "#ED6464",
                IsInFolder = folderIds.Contains(f.Id)
            });
        }

        foreach (var s in payload.Siblings)
        {
            list.Add(new LinkedCardView
            {
                Id = s.Id,
                CharacterName = s.CharacterName,
                Relation = "Sibling",
                RelationColorHex = "#63CAB7",
                IsInFolder = folderIds.Contains(s.Id)
            });
        }

        if (payload.IsFork && payload.ForkMaster is not null)
        {
            BannerForkStatus.Visibility = Visibility.Visible;
            BannerForkStatus.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22ED6464"));
            BannerForkStatus.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentNegBrush");
            TxtForkBanner.Foreground = (System.Windows.Media.Brush)FindResource("AccentNegBrush");
            TxtForkBanner.Text = $"This card is a FORK of {payload.ForkMaster.CharacterName} (#{payload.ForkMaster.Id}).";
        }
        else if (payload.Forks.Count > 0)
        {
            BannerForkStatus.Visibility = Visibility.Visible;
            BannerForkStatus.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2263CAB7"));
            BannerForkStatus.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentPosBrush");
            TxtForkBanner.Foreground = (System.Windows.Media.Brush)FindResource("AccentPosBrush");
            TxtForkBanner.Text = $"This is a MASTER card with {payload.Forks.Count} fork(s).";
        }
        else
        {
            BannerForkStatus.Visibility = Visibility.Collapsed;
        }

        ItemsLinks.ItemsSource = list;
        UpdateCurrentCardLinkStats(payload.IsFork, payload.Forks.Count, payload.Siblings.Count);
        TxtLinksStatus.Text = list.Count > 0
            ? $"{list.Count} linked card(s) found ({list.Count(x => x.IsInFolder)} in current folder)."
            : "No fork or sibling links on Botbooru.";
    }

    private async void LoadLinks_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard?.BotbooruPostId is not long postId) return;

        BtnLoadLinks.IsEnabled = false;
        TxtLinksStatus.Text = $"Fetching links & forks for #{postId}...";

        try
        {
            var details = await _botbooru.FetchPostDetailsAsync(postId);
            var payload = new LinksCachePayload
            {
                IsFork = details.IsFork,
                ForkMaster = details.ForkMaster,
                Forks = details.Forks,
                Siblings = details.Siblings
            };

            if (_currentFolder is not null)
            {
                string linksDir = Path.Combine(CardCacheDb.GetFolderCacheDir(_currentFolder), "links");
                Directory.CreateDirectory(linksDir);
                await File.WriteAllTextAsync(
                    Path.Combine(linksDir, $"{postId}.json"),
                    JsonSerializer.Serialize(payload));
            }

            ApplyLinksPayload(payload);
            SaveCurrentCardSlopScore(details.SlopScore);
        }
        catch (Exception ex)
        {
            TxtLinksStatus.Text = ex.Message.Contains("404") && !_botbooru.HasSession
                ? $"Post #{postId} returned 404 — if this is an NSFW card, click 'Botbooru Login' first."
                : $"Failed to load links: {ex.Message}";
        }
        finally
        {
            BtnLoadLinks.IsEnabled = _currentCard?.BotbooruPostId.HasValue == true;
        }
    }

    private void JumpToLinkedCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not long targetId) return;
        var match = _filteredCards.FirstOrDefault(c => c.BotbooruPostId == targetId)
            ?? _allCards.FirstOrDefault(c => c.BotbooruPostId == targetId);

        if (match is null) return;

        if (!_filteredCards.Contains(match))
        {
            TxtSearch.Text = string.Empty;
            CmbQuickFilter.SelectedIndex = 0;
        }

        LstCards.SelectedItem = match;
        LstCards.ScrollIntoView(match);
    }

    private void OpenLinkedBotbooru_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is long targetId)
            OpenUrl($"https://botbooru.com/character/{targetId}");
    }

    private async void DownloadLinkedCard_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        if (sender is not Button btn || btn.Tag is not LinkedCardView item) return;

        btn.IsEnabled = false;
        TxtStatus.Text = $"Downloading {item.CharacterName} (#{item.Id}) from Botbooru...";

        try
        {
            var (bytes, fileName) = await _botbooru.DownloadCardPngAsync(item.Id, item.CharacterName);
            string destPath = Path.Combine(_currentFolder, fileName);
            if (File.Exists(destPath))
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                destPath = Path.Combine(_currentFolder, $"{stem}_{item.Id}{ext}");
            }

            await File.WriteAllBytesAsync(destPath, bytes);
            var parsed = PngCardCodec.ReadCard(destPath);
            var summary = parsed.ToSummary();
            _db.UpsertCards(_currentFolder, [summary]);

            string? currentPath = _currentCard?.FilePath;

            _allCards.Add(summary);
            _allCards.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            RebuildTagBrowserIndex();
            ApplyFilters();

            if (currentPath is not null)
            {
                var prev = _filteredCards.FirstOrDefault(c => string.Equals(c.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
                if (prev is not null)
                {
                    LstCards.SelectedItem = prev;
                    LstCards.ScrollIntoView(prev);
                }
            }

            TxtStatus.Text = $"Downloaded {Path.GetFileName(destPath)} to folder.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to download card #{item.Id}:\n{ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            btn.IsEnabled = true;
        }
    }

    private async void HeaderSync_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCard is null || !_currentCard.BotbooruPostId.HasValue || _currentFolder is null) return;
        var summary = _allCards.FirstOrDefault(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase));
        if (summary is null) return;

        BtnHeaderSync.IsEnabled = false;
        try
        {
            await SyncCardsAllAsync([summary]);
        }
        finally
        {
            BtnHeaderSync.IsEnabled = true;
            if (summary.IsBotbooruSynced)
                BtnHeaderSync.Visibility = Visibility.Collapsed;
        }
    }

    private async void SyncSelectedAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = LstCards.SelectedItems.Cast<CardSummary>()
            .Where(c => c.BotbooruPostId.HasValue)
            .ToList();

        if (targets.Count == 0 || _currentFolder is null)
        {
            TxtStatus.Text = "Select one or more cards with a Botbooru ID (#) to sync.";
            return;
        }

        await SyncCardsAllAsync(targets);
    }

    private async Task SyncCardsAllAsync(List<CardSummary> targets)
    {
        if (_currentFolder is null) return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        BtnCancelSync.Visibility = Visibility.Visible;

        int done = 0;
        var batchCategories = new List<(string Tag, string Category)>();

        try
        {
            foreach (var card in targets)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                long pid = card.BotbooruPostId!.Value;
                TxtStatus.Text = $"Syncing Tags, Gallery & Links ({done}/{targets.Count}): {card.Name}...";

                try
                {
                    var details = await _botbooru.FetchPostDetailsAsync(pid, ct);

                    // 1) Tag categories
                    foreach (var t in details.Tags)
                    {
                        if (!_tagCategories.TryGetValue(t.Name, out var ex) || !string.Equals(ex, t.Category, StringComparison.OrdinalIgnoreCase))
                        {
                            _tagCategories[t.Name] = t.Category;
                            batchCategories.Add((t.Name, t.Category));
                        }
                    }

                    // 2) Gallery cache
                    var galleryItems = new List<GalleryItemView>();
                    if (!string.IsNullOrWhiteSpace(details.MainImageUrl))
                    {
                        galleryItems.Add(new GalleryItemView
                        {
                            Label = "Main Card Image",
                            PreviewUrl = details.MainImageUrl,
                            MainViewUrl = details.MainImageUrl,
                            DownloadUrl = details.MainImageUrl
                        });
                    }
                    foreach (var g in details.GalleryImages.OrderBy(x => x.Position))
                    {
                        galleryItems.Add(new GalleryItemView
                        {
                            Label = $"Gallery #{g.Position + 1} ({g.Width}×{g.Height})",
                            PreviewUrl = g.PreviewUrl,
                            MainViewUrl = g.MainViewUrl,
                            DownloadUrl = g.DownloadUrl
                        });
                    }
                    if (galleryItems.Count > 0)
                    {
                        string galleryDir = CardCacheDb.GetFolderGalleryDir(_currentFolder, pid);
                        Directory.CreateDirectory(galleryDir);
                        var toSave = new List<GalleryItemView>(galleryItems.Count);
                        for (int i = 0; i < galleryItems.Count; i++)
                        {
                            var gi = galleryItems[i];
                            string fn = $"img_{i}.png";
                            string lp = Path.Combine(galleryDir, fn);
                            try
                            {
                                if (!File.Exists(lp))
                                {
                                    byte[] pb = await _botbooru.DownloadBytesAsync(gi.PreviewUrl, ct);
                                    await File.WriteAllBytesAsync(lp, pb, ct);
                                }
                                toSave.Add(new GalleryItemView
                                {
                                    Label = gi.Label,
                                    PreviewUrl = fn,
                                    MainViewUrl = gi.MainViewUrl,
                                    DownloadUrl = gi.DownloadUrl
                                });
                            }
                            catch
                            {
                                toSave.Add(gi);
                            }
                        }
                        await File.WriteAllTextAsync(Path.Combine(galleryDir, "gallery.json"), JsonSerializer.Serialize(toSave), ct);
                    }

                    // 3) Links & Forks cache
                    var linksPayload = new LinksCachePayload
                    {
                        IsFork = details.IsFork,
                        ForkMaster = details.ForkMaster,
                        Forks = details.Forks,
                        Siblings = details.Siblings
                    };
                    string linksDir = Path.Combine(CardCacheDb.GetFolderCacheDir(_currentFolder), "links");
                    Directory.CreateDirectory(linksDir);
                    await File.WriteAllTextAsync(Path.Combine(linksDir, $"{pid}.json"), JsonSerializer.Serialize(linksPayload), ct);

                    // 4) Update DB + in-memory summary
                    card.GalleryCount = details.GalleryImages.Count;
                    card.IsFork = details.IsFork;
                    card.ForkCount = details.Forks.Count;
                    card.SiblingCount = details.Siblings.Count;
                    card.SlopScore = details.SlopScore;
                    card.IsBotbooruSynced = true;
                    _db.UpdateGalleryCount(_currentFolder, card.FileName, card.GalleryCount);
                    _db.UpdateLinkStats(_currentFolder, card.FileName, card.IsFork, card.ForkCount, card.SiblingCount);
                    _db.UpdateSlopScore(_currentFolder, card.FileName, card.SlopScore);
                    _db.MarkSynced(_currentFolder, card.FileName);
                    RefreshFilteredItem(card);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip transient errors on individual posts
                }

                await Task.Delay(120, ct);
            }

            if (batchCategories.Count > 0)
                _db.UpsertTagCategories(batchCategories);

            RefreshTagGroups();
            RebuildTagBrowserIndex();

            if (_currentCard?.BotbooruPostId is long curPid)
            {
                TryLoadCachedGallery(_currentFolder, curPid);
                TryLoadCachedLinks(_currentFolder, curPid);
                var curSummary = _allCards.FirstOrDefault(c => string.Equals(c.FilePath, _currentCard.FilePath, StringComparison.OrdinalIgnoreCase));
                UpdateWritingStyleDisplay(curSummary?.SlopScore);
                if (curSummary?.IsBotbooruSynced == true)
                    BtnHeaderSync.Visibility = Visibility.Collapsed;
            }

            TxtStatus.Text = $"Full sync complete for {done} card(s) (Tags, Gallery & Links).";
        }
        catch (OperationCanceledException)
        {
            if (batchCategories.Count > 0)
                _db.UpsertTagCategories(batchCategories);
            RefreshTagGroups();
            RebuildTagBrowserIndex();
            TxtStatus.Text = $"Full sync cancelled at {done}/{targets.Count} cards.";
        }
        finally
        {
            _syncCts = null;
            BtnCancelSync.Visibility = Visibility.Collapsed;
        }
    }

    private void LlmSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        _db.SetSetting("llm_endpoint", TxtLlmEndpoint.Text.Trim());
        _db.SetSetting("llm_model", TxtLlmModel.Text.Trim());
        _db.SetSetting("llm_apikey", TxtLlmApiKey.Text.Trim());
    }

    private void ClearAiChat_Click(object sender, RoutedEventArgs e)
    {
        _llmCts?.Cancel();
        _aiChat.Clear();
    }

    private async void AiQuickPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string prompt)
            await SendAiPromptAsync(prompt);
    }

    private async void TxtAiPrompt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await AskAiFromInputAsync();
        }
    }

    private async void AskAiSend_Click(object sender, RoutedEventArgs e)
    {
        if (_llmCts is not null)
        {
            _llmCts.Cancel();
            return;
        }
        await AskAiFromInputAsync();
    }

    private async Task AskAiFromInputAsync()
    {
        string prompt = TxtAiPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;
        TxtAiPrompt.Text = string.Empty;
        await SendAiPromptAsync(prompt);
    }

    private async Task SendAiPromptAsync(string prompt)
    {
        if (_currentCard is null) return;
        string endpoint = TxtLlmEndpoint.Text.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            TxtStatus.Text = "Enter an OpenAI-compatible endpoint URL (e.g. http://127.0.0.1:5000).";
            return;
        }

        SyncEditorFieldsToCard();
        LlmSettings_LostFocus(this, new RoutedEventArgs());

        _aiChat.Add(new AiChatBubble { Role = "user", Content = prompt });
        ScrollAiChat.ScrollToEnd();

        _llmCts?.Cancel();
        _llmCts = new CancellationTokenSource();
        var ct = _llmCts.Token;

        BtnAskAi.Content = "Cancel";
        TxtStatus.Text = "Asking local LLM...";

        try
        {
            var history = _aiChat.Select(m => new LlmChatService.ChatMessage(m.Role, m.Content)).ToList();
            string reply = await _llm.AskAsync(
                endpoint,
                TxtLlmModel.Text,
                TxtLlmApiKey.Text,
                _currentCard,
                history,
                ct);

            _aiChat.Add(new AiChatBubble { Role = "assistant", Content = reply });
            ScrollAiChat.ScrollToEnd();
            TxtStatus.Text = "AI response received.";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "AI request cancelled.";
        }
        catch (Exception ex)
        {
            _aiChat.Add(new AiChatBubble { Role = "assistant", Content = $"Error: {ex.Message}" });
            ScrollAiChat.ScrollToEnd();
            TxtStatus.Text = $"AI request failed: {ex.Message}";
        }
        finally
        {
            _llmCts = null;
            BtnAskAi.Content = "Send";
        }
    }
}
