using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CharacterCardManager.Models;
using CharacterCardManager.Services;

namespace CharacterCardManager;

public partial class BotbooruBrowserWindow : Window
{
    private const int PageSize = 30;
    private const int MaxWindowCards = 240; // ~36 MB with 200px thumbnails

    private readonly BotbooruSyncService _botbooru;
    private readonly string _targetFolder;
    private readonly HashSet<long> _existingIds;
    private readonly Action<string, List<BotbooruSyncService.BotbooruTagInfo>> _onCardDownloaded;

    private readonly List<BotbooruSyncService.BotbooruSearchItem> _fetchedAll = [];
    private readonly List<BotbooruSyncService.BotbooruSearchItem> _displayPool = [];
    private readonly Dictionary<long, BotbooruSyncService.BotbooruSearchItem> _checkedItems = [];
    private readonly ObservableCollection<ResultCardView> _visibleCards = [];

    private int _windowStartIdx;
    private int _windowEndIdx;
    private int _serverTotal;
    private bool _serverTotalCapped;
    private bool _hasMore = true;
    private bool _isFetching;
    private bool _isAdjustingScroll;
    private CancellationTokenSource? _fetchCts;
    private CancellationTokenSource? _dlCts;
    private ResultCardView? _selectedItem;

    // Smooth scrolling state
    private double _targetScrollY;
    private bool _isSmoothScrolling;

    public sealed class ResultCardView : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isInFolder;
        private bool _isSelected;

        public required BotbooruSyncService.BotbooruSearchItem Data { get; init; }
        public long Id => Data.Id;
        public string CharacterName => string.IsNullOrWhiteSpace(Data.CharacterName) ? $"Card #{Data.Id}" : Data.CharacterName;
        public string ThumbnailUrl => Data.ThumbnailUrl;
        public string IdDisplay => $"#{Data.Id}";
        public string StatsLine => $"↓{Data.Downloads} • {Data.TokenCount} tok";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                OnChanged(nameof(IsChecked));
                OnChanged(nameof(CardBorderHex));
                OnChanged(nameof(CardBorderThickness));
            }
        }

        public bool IsInFolder
        {
            get => _isInFolder;
            set
            {
                _isInFolder = value;
                OnChanged(nameof(IsInFolder));
                OnChanged(nameof(CardBorderHex));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnChanged(nameof(IsSelected));
                OnChanged(nameof(CardBorderHex));
            }
        }

        public string CardBorderHex =>
            _isChecked ? "#BE8CFF" :
            _isInFolder ? "#63CAB7" :
            _isSelected ? "#E1E1F0" :
            "#2A2A3D";

        public Thickness CardBorderThickness => _isChecked ? new Thickness(2.5) : new Thickness(1.5);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public BotbooruBrowserWindow(
        BotbooruSyncService botbooru,
        string targetFolder,
        HashSet<long> existingIds,
        Action<string, List<BotbooruSyncService.BotbooruTagInfo>> onCardDownloaded)
    {
        InitializeComponent();
        _botbooru = botbooru;
        _targetFolder = targetFolder;
        _existingIds = existingIds;
        _onCardDownloaded = onCardDownloaded;

        ItemsResults.ItemsSource = _visibleCards;
        Loaded += async (_, _) => await ResetAndSearchAsync();
        Closed += (_, _) => StopSmoothScroll();
    }

    private int GetColumnsCount()
    {
        double w = ItemsResults.ActualWidth;
        if (w <= 0) w = ScrollResults.ActualWidth - 36;
        return Math.Max(1, (int)(w / 194.0));
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await ResetAndSearchAsync();

    private async void TxtQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ResetAndSearchAsync();
        }
    }

    private async void FilterOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            await ResetAndSearchAsync();
    }

    private void HideInFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RebuildPoolAndWindow();
    }

    private async Task ResetAndSearchAsync()
    {
        _fetchCts?.Cancel();
        StopSmoothScroll();
        _fetchedAll.Clear();
        _displayPool.Clear();
        _visibleCards.Clear();
        _windowStartIdx = 0;
        _windowEndIdx = 0;
        _hasMore = true;
        _targetScrollY = 0;
        BorderEndOfResults.Visibility = Visibility.Collapsed;
        ScrollResults.ScrollToTop();
        UpdateBatchButton();
        await LoadDownAsync();
        if (_hasMore)
            await LoadDownAsync();
    }

    private void ScrollResults_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_isSmoothScrolling)
        {
            _targetScrollY = ScrollResults.VerticalOffset;
            _isSmoothScrolling = true;
            CompositionTarget.Rendering += OnSmoothScrollFrame;
        }

        _targetScrollY = Math.Clamp(_targetScrollY - e.Delta * 0.85, 0, ScrollResults.ScrollableHeight);
    }

    private void OnSmoothScrollFrame(object? sender, EventArgs e)
    {
        double current = ScrollResults.VerticalOffset;
        double diff = _targetScrollY - current;

        if (Math.Abs(diff) < 0.75)
        {
            ScrollResults.ScrollToVerticalOffset(_targetScrollY);
            StopSmoothScroll();
            return;
        }

        ScrollResults.ScrollToVerticalOffset(current + diff * 0.24);
    }

    private void StopSmoothScroll()
    {
        if (_isSmoothScrolling)
        {
            _isSmoothScrolling = false;
            CompositionTarget.Rendering -= OnSmoothScrollFrame;
        }
    }

    private async void ScrollResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isFetching || _isAdjustingScroll) return;

        if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 550)
        {
            if (_windowEndIdx < _displayPool.Count || _hasMore)
                await LoadDownAsync();
        }
        else if (e.VerticalChange < 0 && e.VerticalOffset < 260 && _windowStartIdx > 0)
        {
            LoadUp();
        }
    }

    private async Task LoadDownAsync()
    {
        if (_isFetching) return;
        _isFetching = true;

        try
        {
            if (_windowEndIdx >= _displayPool.Count && _hasMore)
            {
                _fetchCts?.Cancel();
                _fetchCts = new CancellationTokenSource();
                var ct = _fetchCts.Token;

                string query = TxtQuery.Text.Trim();
                string sort = (CmbSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "latest";
                bool sfwOnly = ChkSfwOnly.IsChecked == true;
                bool hideAi = ChkHideAi.IsChecked == true;

                TxtStatus.Text = _fetchedAll.Count == 0 ? "Searching Botbooru..." : $"Loading more cards ({_fetchedAll.Count} cached)...";

                var res = await _botbooru.SearchPostsAsync(query, sort, PageSize, _fetchedAll.Count, sfwOnly, hideAi, ct);
                _serverTotal = res.Total;
                _serverTotalCapped = res.TotalIsCapped;
                _hasMore = res.Posts.Count == PageSize;

                bool hideInFolder = ChkHideInFolder.IsChecked == true;
                foreach (var post in res.Posts)
                {
                    _fetchedAll.Add(post);
                    if (!hideInFolder || !_existingIds.Contains(post.Id))
                        _displayPool.Add(post);
                }
            }

            if (_windowEndIdx < _displayPool.Count)
            {
                int cols = GetColumnsCount();
                int batch = Math.Max(cols, (PageSize / cols) * cols);
                int take = Math.Min(batch, _displayPool.Count - _windowEndIdx);

                for (int i = 0; i < take; i++)
                {
                    _visibleCards.Add(CreateViewModel(_displayPool[_windowEndIdx + i]));
                }
                _windowEndIdx += take;

                // Cull full rows from top so columns never shift
                if (_visibleCards.Count > MaxWindowCards)
                {
                    int toRemove = ((_visibleCards.Count - MaxWindowCards) / cols) * cols;
                    if (toRemove > 0)
                    {
                        ScrollResults.UpdateLayout();
                        double beforeHeight = ScrollResults.ExtentHeight;

                        for (int i = 0; i < toRemove; i++)
                            _visibleCards.RemoveAt(0);
                        _windowStartIdx += toRemove;

                        ScrollResults.UpdateLayout();
                        double removedHeight = Math.Max(0, beforeHeight - ScrollResults.ExtentHeight);
                        if (removedHeight > 0)
                        {
                            _isAdjustingScroll = true;
                            StopSmoothScroll();
                            double newOffset = Math.Max(0, ScrollResults.VerticalOffset - removedHeight);
                            _targetScrollY = newOffset;
                            ScrollResults.ScrollToVerticalOffset(newOffset);
                            _ = Dispatcher.BeginInvoke(() => _isAdjustingScroll = false, System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                }
            }

            UpdateCountLabels();
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            _isFetching = false;
        }
    }

    private void LoadUp()
    {
        if (_windowStartIdx <= 0) return;

        _isFetching = true;
        try
        {
            int cols = GetColumnsCount();
            int batch = Math.Max(cols, (PageSize / cols) * cols);
            int toAdd = Math.Min(_windowStartIdx, batch);

            double beforeHeight = ScrollResults.ExtentHeight;
            int newStart = _windowStartIdx - toAdd;

            for (int i = 0; i < toAdd; i++)
            {
                _visibleCards.Insert(i, CreateViewModel(_displayPool[newStart + i]));
            }
            _windowStartIdx = newStart;

            ScrollResults.UpdateLayout();
            double addedHeight = Math.Max(0, ScrollResults.ExtentHeight - beforeHeight);

            if (_visibleCards.Count > MaxWindowCards)
            {
                int toRemove = ((_visibleCards.Count - MaxWindowCards) / cols) * cols;
                for (int i = 0; i < toRemove; i++)
                {
                    _visibleCards.RemoveAt(_visibleCards.Count - 1);
                    _windowEndIdx--;
                }
            }

            if (addedHeight > 0)
            {
                _isAdjustingScroll = true;
                StopSmoothScroll();
                double newOffset = ScrollResults.VerticalOffset + addedHeight;
                _targetScrollY = newOffset;
                ScrollResults.ScrollToVerticalOffset(newOffset);
                _ = Dispatcher.BeginInvoke(() => _isAdjustingScroll = false, System.Windows.Threading.DispatcherPriority.Background);
            }

            UpdateCountLabels();
        }
        finally
        {
            _isFetching = false;
        }
    }

    private void RebuildPoolAndWindow()
    {
        bool hideInFolder = ChkHideInFolder.IsChecked == true;
        _displayPool.Clear();
        foreach (var post in _fetchedAll)
        {
            if (!hideInFolder || !_existingIds.Contains(post.Id))
                _displayPool.Add(post);
        }

        _visibleCards.Clear();
        _windowStartIdx = 0;
        _windowEndIdx = Math.Min(MaxWindowCards, _displayPool.Count);
        for (int i = 0; i < _windowEndIdx; i++)
            _visibleCards.Add(CreateViewModel(_displayPool[i]));

        ScrollResults.ScrollToTop();
        _targetScrollY = 0;
        UpdateCountLabels();
    }

    private ResultCardView CreateViewModel(BotbooruSyncService.BotbooruSearchItem post)
    {
        return new ResultCardView
        {
            Data = post,
            IsInFolder = _existingIds.Contains(post.Id),
            IsChecked = _checkedItems.ContainsKey(post.Id),
            IsSelected = _selectedItem?.Id == post.Id
        };
    }

    private void UpdateCountLabels()
    {
        string cap = _serverTotalCapped ? "+" : "";
        int totalPages = Math.Max(1, (int)Math.Ceiling(_serverTotal / (double)PageSize));
        int currentPage = Math.Max(1, (int)Math.Ceiling(_windowEndIdx / (double)PageSize));
        TxtTotalCount.Text = $"Page {currentPage} / {totalPages}{cap} • Window: {_visibleCards.Count} • Loaded: {_fetchedAll.Count} / {_serverTotal}{cap}";

        bool atVeryEnd = !_hasMore && _windowEndIdx >= _displayPool.Count;
        BorderEndOfResults.Visibility = atVeryEnd ? Visibility.Visible : Visibility.Collapsed;
        if (atVeryEnd)
        {
            TxtEndOfResults.Text = _displayPool.Count == 0
                ? "— No results found —"
                : $"— Reached the end of search results ({_displayPool.Count} shown) —";
            TxtStatus.Text = "Reached the end of search results.";
        }
        else
        {
            TxtStatus.Text = $"Showing cards {_windowStartIdx + 1}–{_windowEndIdx} (scroll up/down to lazy-load)";
        }
    }

    private void CardCheckBox_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is ResultCardView item)
            SyncCheckedState(item);
        UpdateBatchButton();
    }

    private void SyncCheckedState(ResultCardView item)
    {
        if (item.IsChecked)
            _checkedItems[item.Id] = item.Data;
        else
            _checkedItems.Remove(item.Id);
    }

    private void UpdateBatchButton()
    {
        int count = _checkedItems.Count;
        BtnDownloadSelected.Content = $"Download Selected ({count})";
        BtnDownloadSelected.IsEnabled = count > 0 || _dlCts is not null;
    }

    private void SelectAllMissing_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleCards)
        {
            if (!item.IsInFolder)
            {
                item.IsChecked = true;
                _checkedItems[item.Id] = item.Data;
            }
        }
        UpdateBatchButton();
    }

    private void ClearChecks_Click(object sender, RoutedEventArgs e)
    {
        _checkedItems.Clear();
        foreach (var item in _visibleCards)
            item.IsChecked = false;
        UpdateBatchButton();
    }

    private void ResultImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindAncestor<CheckBox>(dep) is not null)
            return;

        if (sender is FrameworkElement fe && fe.Tag is ResultCardView item)
        {
            item.IsChecked = !item.IsChecked;
            SyncCheckedState(item);
            UpdateBatchButton();
            e.Handled = true;
        }
    }

    private void ResultCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindAncestor<CheckBox>(dep) is not null)
            return;

        if (sender is not FrameworkElement fe || fe.Tag is not ResultCardView item)
            return;

        if (_selectedItem is not null)
            _selectedItem.IsSelected = false;

        _selectedItem = item;
        _selectedItem.IsSelected = true;

        ShowInspector(item);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ShowInspector(ResultCardView item)
    {
        TxtEmptyInspector.Visibility = Visibility.Collapsed;
        InspectorScroll.Visibility = Visibility.Visible;

        var d = item.Data;
        if (string.IsNullOrWhiteSpace(d.ThumbnailUrl))
        {
            ImgInspector.Source = null;
        }
        else
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(d.ThumbnailUrl, UriKind.Absolute);
            bmp.DecodePixelWidth = 380;
            bmp.EndInit();
            ImgInspector.Source = bmp;
        }

        TxtInspName.Text = item.CharacterName;
        TxtInspTagline.Text = d.Tagline;
        TxtInspTagline.Visibility = string.IsNullOrWhiteSpace(d.Tagline) ? Visibility.Collapsed : Visibility.Visible;

        var stats = new List<string> { $"{d.TokenCount} tok", $"↓{d.Downloads}", $"👁{d.Views}", $"★{d.FavoriteCount}" };
        if (d.IsFork) stats.Add("Fork");
        else if (d.ForkCount > 0) stats.Add($"Master({d.ForkCount})");
        if (d.SiblingCount > 0) stats.Add($"Siblings:{d.SiblingCount}");

        TxtInspStats.Text = string.Join(" • ", stats);
        TxtInspLink.Text = $"botbooru.com/character/{d.Id}";
        TxtInspDesc.Text = string.IsNullOrWhiteSpace(d.DescriptionExcerpt) ? "(No description excerpt)" : d.DescriptionExcerpt;

        BtnInspDownload.Content = item.IsInFolder ? "Already in Folder (Re-download)" : "Download to Folder";

        ItemsInspTags.ItemsSource = d.Tags
            .OrderBy(t => BotbooruSyncService.GetCategorySortOrder(t.Category))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagChipItem
            {
                Name = t.Name,
                Category = t.Category,
                ColorHex = BotbooruSyncService.GetCategoryColorHex(t.Category)
            })
            .ToList();
    }

    private void InspLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedItem is not null)
            Process.Start(new ProcessStartInfo($"https://botbooru.com/character/{_selectedItem.Id}") { UseShellExecute = true });
    }

    private void InspectorImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedItem is null || string.IsNullOrWhiteSpace(_selectedItem.ThumbnailUrl)) return;

        var win = new Window
        {
            Title = _selectedItem.CharacterName,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 880,
            Height = 880,
            Background = (Brush)FindResource("BgDeepBrush")
        };
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(12),
            Cursor = Cursors.Hand,
            Source = new BitmapImage(new Uri(_selectedItem.ThumbnailUrl, UriKind.Absolute))
        };
        img.MouseLeftButtonUp += (_, _) => win.Close();
        win.KeyDown += (_, ke) => { if (ke.Key == Key.Escape) win.Close(); };
        win.Content = img;
        win.ShowDialog();
    }

    private async void InspTag_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TagChipItem tag)
        {
            string q = $"{tag.Category.ToLowerInvariant()}:{tag.Name}";
            if (!TxtQuery.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                TxtQuery.Text = string.IsNullOrWhiteSpace(TxtQuery.Text) ? q : $"{TxtQuery.Text.Trim()} {q}";
                await ResetAndSearchAsync();
            }
        }
    }

    private async void InspTag_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TagChipItem tag)
        {
            string q = $"-{tag.Category.ToLowerInvariant()}:{tag.Name}";
            if (!TxtQuery.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                TxtQuery.Text = string.IsNullOrWhiteSpace(TxtQuery.Text) ? q : $"{TxtQuery.Text.Trim()} {q}";
                await ResetAndSearchAsync();
            }
        }
    }

    private async void DownloadSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        BtnInspDownload.IsEnabled = false;
        try
        {
            await DownloadOneAsync(_selectedItem.Data, CancellationToken.None);
            BtnInspDownload.Content = "Already in Folder (Re-download)";
            TxtStatus.Text = $"Downloaded {_selectedItem.CharacterName} (#{_selectedItem.Id})";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Download failed:\n{ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnInspDownload.IsEnabled = true;
        }
    }

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_dlCts is not null)
        {
            _dlCts.Cancel();
            return;
        }

        var queue = _checkedItems.Values.ToList();
        if (queue.Count == 0) return;

        _dlCts = new CancellationTokenSource();
        var ct = _dlCts.Token;
        BtnDownloadSelected.Content = "Cancel Download";

        int done = 0;
        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var item = queue[i];
                ct.ThrowIfCancellationRequested();
                done++;
                TxtStatus.Text = $"Downloading ({done}/{queue.Count}): {item.CharacterName}...";
                try
                {
                    await DownloadOneAsync(item, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    TxtStatus.Text = $"Rate limited (429) after {done - 1} card(s) — stopping batch to protect account.";
                    return;
                }
                catch
                {
                    // Continue batch on single non-rate-limit failure
                }

                if (i + 1 < queue.Count)
                    await Task.Delay(3000, ct);
            }
            TxtStatus.Text = $"Downloaded {done} card(s) to {_targetFolder}.";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"Batch download cancelled at {done}/{queue.Count}.";
        }
        finally
        {
            _dlCts = null;
            UpdateBatchButton();
        }
    }

    private async Task DownloadOneAsync(BotbooruSyncService.BotbooruSearchItem item, CancellationToken ct)
    {
        var (bytes, fileName) = await _botbooru.DownloadCardPngAsync(item.Id, item.CharacterName, ct);
        string destPath = Path.Combine(_targetFolder, fileName);
        if (File.Exists(destPath))
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            destPath = Path.Combine(_targetFolder, $"{stem}_{item.Id}{ext}");
        }

        await File.WriteAllBytesAsync(destPath, bytes, ct);
        _existingIds.Add(item.Id);
        _checkedItems.Remove(item.Id);

        var visible = _visibleCards.FirstOrDefault(v => v.Id == item.Id);
        if (visible is not null)
        {
            visible.IsInFolder = true;
            visible.IsChecked = false;
        }

        _onCardDownloaded(destPath, item.Tags);
    }
}
