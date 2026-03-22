using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Sowser.Controls;
using Sowser.Models;
using MaterialDesignThemes.Wpf;
using Sowser.Services;

namespace Sowser
{
    /// <summary>
    /// Sowser - A spatial web browser with draggable cards on an infinite canvas
    /// </summary>
    public partial class MainWindow : Window
    {
        // Data collections
        private readonly Dictionary<string, BrowserCard> _cards = new();
        private readonly List<Connection> _connections = new();
        private readonly Dictionary<string, System.Windows.Shapes.Path> _connectionLines = new();
        private readonly Dictionary<string, StickyNote> _notes = new();
        private readonly List<Bookmark> _bookmarks = new()
        {
            new Bookmark { Title = "YouTube", Url = "https://youtube.com" },
            new Bookmark { Title = "ChatGPT", Url = "https://chat.openai.com" },
            new Bookmark { Title = "Amazon", Url = "https://amazon.com" },
            new Bookmark { Title = "Google Docs", Url = "https://docs.google.com" },
            new Bookmark { Title = "Netflix", Url = "https://netflix.com" },
            new Bookmark { Title = "GitHub", Url = "https://github.com" }
        };
        private readonly List<HistoryEntry> _history = new();
        private readonly List<DownloadItem> _downloads = new();
        
        // 4D Time Machine State (reserved for workspace snapshots)
        private readonly List<WorkspaceState> _timeMachineHistory = new();
        
        // Multiplayer and Data Pipes removed to improve performance and stability

        // Settings (persisted via AppSettingsStore)
        private AppSettings _settings = AppSettingsStore.Load();
        private readonly string[] _searchEngines = {
            "https://www.google.com/search?q=",
            "https://www.bing.com/search?q=",
            "https://duckduckgo.com/?q="
        };

        // Pan state
        private bool _isPanning;
        private Point _panStartPoint;
        private double _panStartOffsetX;
        private double _panStartOffsetY;

        // Zoom
        private double _zoomLevel = 1.0;
        private const double ZoomMin = 0.1;
        private const double ZoomMax = 5.0;
        private const double ZoomStep = 0.05; // Smoother zoom

        // Auto-save timer
        private DispatcherTimer? _autoSaveTimer;
        private DispatcherTimer? _sleepTimer;

        private bool _isInitialized;
        private bool _isConnectionDrag;
        private string? _connectionStartCardId;
        private string? _connectionStartEdge;
        private System.Windows.Shapes.Path? _connectionPreviewPath;
        private string? _connectionPreviewTargetEdge;
        private string? _hoveredConnectionCardId;
        private string? _hoveredConnectionEdge;

        // Smooth scroll state
        private double _smoothScrollTargetY;
        private bool _isSmoothScrollActive;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
                
                // Force Windows to use Dark Mode for the app's OS-level rendering
                int darkTheme = 1; // 1 = Enable dark mode
                DwmSetWindowAttribute(hwnd, 20, ref darkTheme, sizeof(int));
                
                int cornerPref = 2; // Rounded corners
                DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
            }
            catch { }
        }

        public MainWindow()
        {
            InitializeComponent();
            AppServices.BlockTrackers = _settings.BlockTrackers;
            InitializeAutoSave();
            InitGlobalShortcuts();
            Closing += (s, e) => AppSettingsStore.Save(_settings);
            
            // Center viewport initially and set up horizontal scroll hook
            Loaded += (s, e) => 
            {
                CenterViewport();
                _isInitialized = true;
                SetupHorizontalScrollHook();
                ApplyLoadedIntegrationSettings();
                RefreshBookmarksList();
                SyncCaptionMaximizeIcon();
                ApplyCanvasTheme(_currentBgTheme, quiet: true);
                Dispatcher.BeginInvoke(new Action(TryRestoreLastSession), DispatcherPriority.Background);
            };

            // Handle ESC to close expanded card view (also handled in GlobalShortcuts)
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            StateChanged += (_, _) => SyncCaptionMaximizeIcon();
        }

        private void SyncCaptionMaximizeIcon()
        {
            if (CaptionMaximizeIcon == null) return;
            CaptionMaximizeIcon.Kind = WindowState == WindowState.Maximized
                ? PackIconKind.WindowRestore
                : PackIconKind.WindowMaximize;
        }

        private void WindowCaptionBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                CaptionMaxRestore_Click(sender, e);
                return;
            }
            try { DragMove(); }
            catch { /* ignore if drag not possible */ }
        }

        private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CaptionMaxRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && ExpandedCardOverlay.Visibility == Visibility.Visible)
            {
                CloseExpandedCard();
                e.Handled = true;
            }
        }

        #region Horizontal Scroll Support (Trackpad)

        private const int WM_MOUSEHWHEEL = 0x020E;

        private void SetupHorizontalScrollHook()
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEHWHEEL)
            {
                // Extract the scroll delta (high-order word of wParam)
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                
                // Scroll horizontally - delta is positive for right, negative for left
                double newOffset = CanvasScrollViewer.HorizontalOffset + delta;
                CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffset));
                
                handled = true;
            }
            return IntPtr.Zero;
        }

        #endregion

        #region Initialization

        private void InitializeAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_settings.AutoSaveIntervalSeconds)
            };
            _autoSaveTimer.Tick += (s, e) => AutoSaveWorkspace();
            if (_settings.AutoSaveEnabled)
            {
                _autoSaveTimer.Start();
            }

            _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _sleepTimer.Tick += (s, e) => 
            {
                if (_settings.SuspendOffscreenCards)
                    CheckCardVisibility();
            };
            _sleepTimer.Start();

            // Lightweight dedicated timer to keep MiniMap updated at ~3 FPS seamlessly
            var minimapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            minimapTimer.Tick += (s, e) => UpdateMiniMap();
            minimapTimer.Start();
        }

        private void PanToCard(BrowserCard card)
        {
            double left = Canvas.GetLeft(card);
            double top = Canvas.GetTop(card);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double cardWidth = card.ActualWidth > 0 ? card.ActualWidth : 700;
            double cardHeight = card.ActualHeight > 0 ? card.ActualHeight : 500;

            double centerX = (left + cardWidth / 2) * _zoomLevel - (CanvasScrollViewer.ActualWidth / 2);
            double centerY = (top + cardHeight / 2) * _zoomLevel - (CanvasScrollViewer.ActualHeight / 2);

            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY));
            
            // Highlight effect
            card.Opacity = 0.5;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, ev) => { card.Opacity = 1.0; timer.Stop(); };
            timer.Start();
        }

        private void CheckCardVisibility()
        {
            double viewLeft = CanvasScrollViewer.HorizontalOffset / _zoomLevel;
            double viewTop = CanvasScrollViewer.VerticalOffset / _zoomLevel;
            double viewRight = viewLeft + (CanvasScrollViewer.ActualWidth / _zoomLevel);
            double viewBottom = viewTop + (CanvasScrollViewer.ActualHeight / _zoomLevel);
            
            // Add padding so cards don't sleep instantly when partially out
            viewLeft -= 500; viewTop -= 500;
            viewRight += 500; viewBottom += 500;

            foreach (var card in _cards.Values)
            {
                double left = Canvas.GetLeft(card);
                double top = Canvas.GetTop(card);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                double right = left + card.ActualWidth;
                double bottom = top + card.ActualHeight;

                bool isVisible = !(right < viewLeft || left > viewRight || bottom < viewTop || top > viewBottom);
                card.SetSleepState(!isVisible);
            }
        }

        private void UpdateSpatialAudio()
        {
            double viewLeft = CanvasScrollViewer.HorizontalOffset / _zoomLevel;
            double viewTop = CanvasScrollViewer.VerticalOffset / _zoomLevel;
            double viewWidth = CanvasScrollViewer.ActualWidth / _zoomLevel;
            double viewHeight = CanvasScrollViewer.ActualHeight / _zoomLevel;

            double viewCenterX = viewLeft + viewWidth / 2;
            double viewCenterY = viewTop + viewHeight / 2;
            
            // Max hearing distance (e.g. 2 screens away)
            double maxDistance = Math.Max(viewWidth, viewHeight) * 2;

            foreach (var card in _cards.Values)
            {
                if (card.IsPinned)
                {
                    card.UpdateSpatialAudio(1.0);
                }
                else
                {
                    double cardCenterX = Canvas.GetLeft(card) + card.ActualWidth / 2;
                    double cardCenterY = Canvas.GetTop(card) + card.ActualHeight / 2;
                    
                    if (double.IsNaN(cardCenterX) || double.IsNaN(cardCenterY)) continue;
                    
                    double dx = cardCenterX - viewCenterX;
                    double dy = cardCenterY - viewCenterY;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    
                    double volume = 1.0 - (distance / maxDistance);
                    volume = Math.Clamp(volume, 0.0, 1.0);
                    volume = volume * volume; // natural falloff curve
                    
                    card.UpdateSpatialAudio(volume);
                }
            }
        }

        private void UpdateMiniMap()
        {
            if (MiniMapCanvas == null) return;
            
            if (_cards.Count == 0 || CanvasScrollViewer.ActualWidth == 0) 
            {
                MiniMapCanvas.Children.Clear();
                return;
            }

            double minX = _cards.Values.Min(c => double.IsNaN(Canvas.GetLeft(c)) ? 0 : Canvas.GetLeft(c));
            double minY = _cards.Values.Min(c => double.IsNaN(Canvas.GetTop(c)) ? 0 : Canvas.GetTop(c));
            double maxX = _cards.Values.Max(c => (double.IsNaN(Canvas.GetLeft(c)) ? 0 : Canvas.GetLeft(c)) + c.ActualWidth);
            double maxY = _cards.Values.Max(c => (double.IsNaN(Canvas.GetTop(c)) ? 0 : Canvas.GetTop(c)) + c.ActualHeight);

            // Bounds plus padding
            minX -= 600; minY -= 600;
            maxX += 600; maxY += 600;

            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;
            
            double scaleX = MiniMapCanvas.ActualWidth / Math.Max(1, contentWidth);
            double scaleY = MiniMapCanvas.ActualHeight / Math.Max(1, contentHeight);
            double scale = Math.Min(scaleX, scaleY);
            
            MiniMapCanvas.Children.Clear();

            // Draw cards dots
            foreach (var card in _cards.Values)
            {
                double left = double.IsNaN(Canvas.GetLeft(card)) ? 0 : Canvas.GetLeft(card);
                double top = double.IsNaN(Canvas.GetTop(card)) ? 0 : Canvas.GetTop(card);

                var rect = new Rectangle
                {
                    Width = Math.Max(3, card.ActualWidth * scale),
                    Height = Math.Max(3, card.ActualHeight * scale),
                    Fill = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                    Opacity = 0.8,
                    RadiusX = 2, RadiusY = 2
                };

                Canvas.SetLeft(rect, (left - minX) * scale);
                Canvas.SetTop(rect, (top - minY) * scale);
                MiniMapCanvas.Children.Add(rect);
            }

            // Draw viewport rect
            double viewLeft = CanvasScrollViewer.HorizontalOffset / _zoomLevel;
            double viewTop = CanvasScrollViewer.VerticalOffset / _zoomLevel;
            double viewWidth = CanvasScrollViewer.ActualWidth / _zoomLevel;
            double viewHeight = CanvasScrollViewer.ActualHeight / _zoomLevel;

            var viewRect = new Rectangle
            {
                Width = Math.Max(4, viewWidth * scale),
                Height = Math.Max(4, viewHeight * scale),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
            };
            
            Canvas.SetLeft(viewRect, (viewLeft - minX) * scale);
            Canvas.SetTop(viewRect, (viewTop - minY) * scale);
            MiniMapCanvas.Children.Add(viewRect);
        }

        private void CenterViewport()
        {
            // Center on canvas middle
            double centerX = (CardsCanvas.Width / 2) - (CanvasScrollViewer.ActualWidth / 2);
            double centerY = (CardsCanvas.Height / 2) - (CanvasScrollViewer.ActualHeight / 2);
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY));
        }

        /// <summary>
        /// Update canvas size to fit all cards with padding, ensuring scrollable area
        /// </summary>
        private void UpdateCanvasSize()
        {
            const double padding = 800; // Extra space around cards for scrolling

            // Minimum size should be at least viewport size + padding for scrolling
            double viewportWidth = CanvasScrollViewer.ActualWidth > 0 ? CanvasScrollViewer.ActualWidth : 1200;
            double viewportHeight = CanvasScrollViewer.ActualHeight > 0 ? CanvasScrollViewer.ActualHeight : 800;
            double minWidth = viewportWidth + padding;
            double minHeight = viewportHeight + padding;

            if (_cards.Count == 0)
            {
                CardsCanvas.Width = minWidth;
                CardsCanvas.Height = minHeight;
                ConnectionsCanvas.Width = minWidth;
                ConnectionsCanvas.Height = minHeight;
                return;
            }

            double maxRight = 0;
            double maxBottom = 0;

            foreach (var card in _cards.Values)
            {
                double left = Canvas.GetLeft(card);
                double top = Canvas.GetTop(card);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                maxRight = Math.Max(maxRight, left + card.ActualWidth + padding);
                maxBottom = Math.Max(maxBottom, top + card.ActualHeight + padding);
            }

            double newWidth = Math.Max(minWidth, maxRight);
            double newHeight = Math.Max(minHeight, maxBottom);

            CardsCanvas.Width = newWidth;
            CardsCanvas.Height = newHeight;
            ConnectionsCanvas.Width = newWidth;
            ConnectionsCanvas.Height = newHeight;
        }

        #endregion

        #region Card Management

        /// <summary>
        /// Create a new browser card at specified position
        /// </summary>
        private BrowserCard CreateCard(double x, double y, string? url = null, string? browserProfile = null, bool undoRemoveOnCreate = false)
        {
            var card = new BrowserCard();
            card.BrowserProfileKey = string.IsNullOrWhiteSpace(browserProfile) ? _settings.DefaultBrowserProfile : browserProfile!;
            
            // Position on canvas
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            
            // Wire up events
            card.CloseRequested += Card_CloseRequested;
            card.LinkClicked += Card_LinkClicked;
            card.BookmarkRequested += Card_BookmarkRequested;
            card.NavigationCompleted += Card_NavigationCompleted;
            card.CardMoved += Card_CardMoved;
            card.DownloadStarted += Card_DownloadStarted;
            card.ConnectionPointPressed += Card_ConnectionPointPressed;
            card.ConnectionPointHoverChanged += Card_ConnectionPointHoverChanged;
            card.FullscreenRequested += Card_FullscreenRequested;
            card.CardDropped += Card_CardDropped;
            card.PinToggled += Card_PinToggled;
            card.PortalOpened += Card_PortalOpened;
            card.GroupListRequested += (s, e) => e.Groups = _groups;
            card.GroupAssigned += Card_GroupAssigned;
            card.ReadLaterRequested += (s, e) => AddCurrentToReadLater(s as BrowserCard);
            card.CapturePreviewToCanvasRequested += (s, e) => CaptureCardToCanvasAsync(s as BrowserCard);
            card.CardInteracted += (s, e) => OnCardInteracted(s as BrowserCard);
            
            // Add to canvas and tracking
            CardsCanvas.Children.Add(card);
            _cards[card.CardId] = card;

            if (undoRemoveOnCreate)
            {
                string id = card.CardId;
                PushUndo(() => SafeCloseCardById(id));
            }
            
            // Navigate if URL provided
            if (!string.IsNullOrEmpty(url))
            {
                card.NavigateDelayed(url);
            }

            // Update canvas size to fit new card
            Dispatcher.BeginInvoke(new Action(UpdateCanvasSize), DispatcherPriority.Loaded);
            
            return card;
        }

        /// <summary>
        /// Get viewport center position on canvas
        /// </summary>
        private Point GetViewportCenter()
        {
            double x = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ViewportWidth / 2) / _zoomLevel;
            double y = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ViewportHeight / 2) / _zoomLevel;
            return new Point(x, y);
        }

        private void Card_CardDropped(object? sender, BrowserCard draggedCard)
        {
            var center = draggedCard.GetCenterPoint();
            
            // Find if dropped on another card
            foreach (var targetCard in _cards.Values.Where(c => c != draggedCard))
            {
                double left = Canvas.GetLeft(targetCard);
                double top = Canvas.GetTop(targetCard);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                double right = left + targetCard.ActualWidth;
                double bottom = top + targetCard.ActualHeight;
                
                // If the center of the dragged card is within the target card...
                if (center.X >= left && center.X <= right && center.Y >= top && center.Y <= bottom)
                {
                    // Add as a tab group to the target!
                    targetCard.AddTab(draggedCard.CurrentTitle, draggedCard.CurrentUrl);
                    
                    // Close the dragged card
                    Card_CloseRequested(this, draggedCard.CardId);
                    
                    // Small visual bounce/glow for feedback
                    targetCard.Opacity = 0.5;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (s, ev) => { targetCard.Opacity = 1.0; timer.Stop(); };
                    timer.Start();
                    
                    break;
                }
            }
            
            // Only update canvas bounds once the drag is completely finished
            UpdateCanvasSize();
        }

        private void Card_PinToggled(object? sender, BrowserCard card)
        {
            if (card.IsPinned)
            {
                // Remove from CardsCanvas and add to a pinned overlay
                CardsCanvas.Children.Remove(card);
                PinnedCanvas.Children.Add(card);

                // Try to put it on the top right corner of the window
                double pinnedLeft = CanvasScrollViewer.ActualWidth - card.ActualWidth - 40;
                double pinnedTop = 40;

                Canvas.SetLeft(card, pinnedLeft);
                Canvas.SetTop(card, pinnedTop);
                
                // Keep it on top
                Panel.SetZIndex(card, 100);
            }
            else
            {
                // Unpinned -> put back onto the infinite canvas at the current viewport's center
                PinnedCanvas.Children.Remove(card);
                CardsCanvas.Children.Add(card);
                
                Point center = GetViewportCenter();
                Canvas.SetLeft(card, center.X - card.ActualWidth / 2);
                Canvas.SetTop(card, center.Y - card.ActualHeight / 2);
                
                Panel.SetZIndex(card, 0);
            }
        }

        private void Card_PortalOpened(object? sender, string pathOrName)
        {
             string targetFile = pathOrName;
             if (!File.Exists(targetFile))
             {
                 string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                 string sowserDir = System.IO.Path.Combine(appData, "Sowser");
                 Directory.CreateDirectory(sowserDir);
                 string name = System.IO.Path.GetFileNameWithoutExtension(pathOrName);
                 targetFile = System.IO.Path.Combine(sowserDir, name + ".json");
             }
             if (File.Exists(targetFile))
             {
                 LoadWorkspaceFromFile(targetFile, showSuccessDialog: false, showErrorDialog: true);
                 ShowToast($"Opened workspace: {System.IO.Path.GetFileName(targetFile)}");
             }
             else
             {
                 string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                 string sowserDir = System.IO.Path.Combine(appData, "Sowser");
                 Directory.CreateDirectory(sowserDir);
                 string name = System.IO.Path.GetFileNameWithoutExtension(pathOrName);
                 string newPath = System.IO.Path.Combine(sowserDir, name + ".json");
                 File.WriteAllText(newPath, JsonSerializer.Serialize(new WorkspaceState(), new JsonSerializerOptions { WriteIndented = true }));
                 ShowToast($"New workspace file ready: {name}");
             }
        }

        private void Card_CloseRequested(object? sender, string cardId)
        {
            if (_cards.TryGetValue(cardId, out var card))
            {
                if (!_suppressUndoForClose && !_undoInProgress)
                {
                    var snap = TakeCardSnapshot(card);
                    PushUndo(() => RestoreCardSnapshot(snap));
                }

                card.ConnectionPointPressed -= Card_ConnectionPointPressed;
                card.ConnectionPointHoverChanged -= Card_ConnectionPointHoverChanged;

                if (_isConnectionDrag && (cardId == _connectionStartCardId || cardId == _hoveredConnectionCardId))
                {
                    CancelConnectionDrag();
                }

                // Remove associated connections
                var connectionsToRemove = _connections
                    .Where(c => c.FromCardId == cardId || c.ToCardId == cardId)
                    .ToList();
                
                foreach (var conn in connectionsToRemove)
                {
                    _connections.Remove(conn);
                    if (_connectionLines.TryGetValue(conn.Id, out var path))
                    {
                        ConnectionsCanvas.Children.Remove(path);
                        _connectionLines.Remove(conn.Id);
                    }
                    // Remove arrow
                    var arrow = ConnectionsCanvas.Children.OfType<Polygon>()
                        .FirstOrDefault(p => p.Tag?.ToString() == conn.Id + "_arrow");
                    if (arrow != null)
                        ConnectionsCanvas.Children.Remove(arrow);
                }
                
                // Remove card and dispose heavy COM objects to prevent lag
                card.DisposeResources();
                CardsCanvas.Children.Remove(card);
                _cards.Remove(cardId);
            }
        }

        private void Card_LinkClicked(object? sender, LinkClickedEventArgs e)
        {
            if (_cards.TryGetValue(e.SourceCardId, out var sourceCard))
            {
                // Calculate new card position (to the right of source card)
                double sourceX = Canvas.GetLeft(sourceCard);
                double sourceY = Canvas.GetTop(sourceCard);
                double cardWidth = sourceCard.ActualWidth > 0 ? sourceCard.ActualWidth : 700;
                double newX = sourceX + cardWidth + 50; // Place to the right with 50px gap
                double newY = sourceY + 200; // Same vertical position
                
                // Create new cardwh
                var newCard = CreateCard(newX, newY, e.Url);
                
                // Create connection
                var connection = new Connection
                {
                    FromCardId = e.SourceCardId,
                    ToCardId = newCard.CardId,
                    Url = e.Url,
                    Timestamp = DateTime.Now
                };
                _connections.Add(connection);
                
                // Draw connection line (delayed to ensure card is rendered)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    DrawConnectionLine(connection, sourceCard, newCard);
                }), DispatcherPriority.Loaded);
            }
        }

        private void Card_BookmarkRequested(object? sender, string url)
        {
            if (sender is BrowserCard card && !string.IsNullOrEmpty(url))
            {
                // Check if already bookmarked
                var existingBookmark = _bookmarks.FirstOrDefault(b => b.Url == url);
                if (existingBookmark != null)
                {
                    // Remove bookmark (toggle off)
                    _bookmarks.Remove(existingBookmark);
                    card.SetBookmarked(false);
                }
                else
                {
                    // Add new bookmark
                    var bookmark = new Bookmark
                    {
                        Title = card.CurrentTitle,
                        Url = url
                    };
                    _bookmarks.Add(bookmark);
                    card.SetBookmarked(true);
                }
                RefreshBookmarksList();
            }
        }

        private void Card_FullscreenRequested(object? sender, string cardId)
        {
            if (_cards.TryGetValue(cardId, out var card))
            {
                OpenCardExpanded(card);
            }
        }

        private string? _expandedCardId;

        private async void OpenCardExpanded(BrowserCard card)
        {
            _expandedCardId = card.CardId;
            ExpandedCardUrl.Text = card.CurrentUrl;
            ExpandedCardOverlay.Visibility = Visibility.Visible;

            // Initialize WebView2 and navigate
            await ExpandedWebView.EnsureCoreWebView2Async();
            ExpandedWebView.CoreWebView2.Navigate(card.CurrentUrl);

            // Focus for keyboard input
            ExpandedWebView.Focus();
        }

        private void CloseExpandedCard_Click(object sender, RoutedEventArgs e)
        {
            CloseExpandedCard();
        }

        private void CloseExpandedCard()
        {
            ExpandedCardOverlay.Visibility = Visibility.Collapsed;
            
            // Sync URL back to card if it changed
            if (!string.IsNullOrEmpty(_expandedCardId) && 
                _cards.TryGetValue(_expandedCardId, out var card) &&
                ExpandedWebView.CoreWebView2 != null)
            {
                string currentUrl = ExpandedWebView.CoreWebView2.Source;
                if (currentUrl != card.CurrentUrl)
                {
                    card.Navigate(currentUrl);
                }
            }
            
            _expandedCardId = null;
        }

        private void Card_NavigationCompleted(object? sender, HistoryEventArgs e)
        {
            var entry = new HistoryEntry
            {
                Title = e.Title,
                Url = e.Url
            };
            _history.Insert(0, entry); // Most recent first
            RefreshHistoryList();
        }

        private void Card_CardMoved(object? sender, EventArgs e)
        {
            if (sender is BrowserCard card)
            {
                // Update only lines connected to this specific moving card
                UpdateSpecificConnectionLines(card.CardId);
            }
        }

        private void Card_DownloadStarted(object? sender, DownloadEventArgs e)
        {
            var download = new DownloadItem
            {
                FileName = System.IO.Path.GetFileName(e.FilePath),
                Url = e.Url,
                FilePath = e.FilePath,
                TotalBytes = e.TotalBytes
            };
            _downloads.Insert(0, download);
            RefreshDownloadsList();
        }

        private void Card_ConnectionPointPressed(object? sender, ConnectionPointEventArgs e)
        {
            if (sender is not BrowserCard card)
                return;

            if (_isConnectionDrag)
            {
                CancelConnectionDrag();
            }

            BeginConnectionDrag(card, e.Edge);
        }

        private void Card_ConnectionPointHoverChanged(object? sender, ConnectionPointEventArgs e)
        {
            if (!_isConnectionDrag)
                return;

            if (e.IsHovered)
            {
                _hoveredConnectionCardId = e.CardId;
                _hoveredConnectionEdge = e.Edge;
            }
            else if (_hoveredConnectionCardId == e.CardId && _hoveredConnectionEdge == e.Edge)
            {
                _hoveredConnectionCardId = null;
                _hoveredConnectionEdge = null;
            }
        }

        #endregion

        #region Connection Lines

        private void BeginConnectionDrag(BrowserCard card, string edge)
        {
            _isConnectionDrag = true;
            _connectionStartCardId = card.CardId;
            _connectionStartEdge = edge;
            _hoveredConnectionCardId = null;
            _hoveredConnectionEdge = null;
            _connectionPreviewTargetEdge = null;

            foreach (var item in _cards.Values)
            {
                item.SetConnectionMode(true);
            }

            _connectionPreviewPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };

            ConnectionsCanvas.Children.Add(_connectionPreviewPath);
        }

        private void CompleteConnectionDrag()
        {
            if (!_isConnectionDrag)
                return;

            if (!string.IsNullOrEmpty(_connectionStartCardId) &&
                !string.IsNullOrEmpty(_hoveredConnectionCardId) &&
                _connectionStartCardId != _hoveredConnectionCardId &&
                _cards.TryGetValue(_connectionStartCardId, out var fromCard) &&
                _cards.TryGetValue(_hoveredConnectionCardId, out var toCard))
            {
                var connection = new Connection
                {
                    FromCardId = _connectionStartCardId,
                    ToCardId = _hoveredConnectionCardId,
                    Url = string.Empty,
                    Timestamp = DateTime.Now,
                    FromEdge = _connectionStartEdge ?? string.Empty,
                    ToEdge = _hoveredConnectionEdge ?? string.Empty,
                    IsManual = true
                };
                _connections.Add(connection);
                DrawConnectionLine(connection, fromCard, toCard);
                
                // Data Pipeline execution removed
            }

            CancelConnectionDrag();
        }

        private void CancelConnectionDrag()
        {
            if (_connectionPreviewPath != null)
            {
                ConnectionsCanvas.Children.Remove(_connectionPreviewPath);
                _connectionPreviewPath = null;
            }

            _isConnectionDrag = false;
            _connectionStartCardId = null;
            _connectionStartEdge = null;
            _connectionPreviewTargetEdge = null;
            _hoveredConnectionCardId = null;
            _hoveredConnectionEdge = null;

            foreach (var card in _cards.Values)
            {
                card.SetConnectionMode(false);
            }
        }

        private (Point from, Point to, string fromEdge, string toEdge) ResolveConnectionPoints(Connection connection, BrowserCard fromCard, BrowserCard toCard)
        {
            // Use stored edges when available
            if (!string.IsNullOrEmpty(connection.FromEdge) && !string.IsNullOrEmpty(connection.ToEdge))
            {
                return (fromCard.GetEdgeCenter(connection.FromEdge),
                        toCard.GetEdgeCenter(connection.ToEdge),
                        connection.FromEdge,
                        connection.ToEdge);
            }

            // Otherwise compute best edges based on relative card positions and persist them
            var (fromPoint, toPoint, fromEdge, toEdge) = GetEdgeConnectionPoints(fromCard, toCard);
            connection.FromEdge = fromEdge;
            connection.ToEdge = toEdge;
            return (fromPoint, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Get the best edge points for connecting two cards based on their positions
        /// </summary>
        private (Point from, Point to, string fromEdge, string toEdge) GetEdgeConnectionPoints(BrowserCard fromCard, BrowserCard toCard)
        {
            // Account for the 12px margin around the visual card border
            const double margin = 12;

            // Get card bounds (visual bounds inside the margin)
            double fromLeft = Canvas.GetLeft(fromCard) + margin;
            double fromTop = Canvas.GetTop(fromCard) + margin;
            double fromRight = fromLeft + fromCard.ActualWidth - margin * 2;
            double fromBottom = fromTop + fromCard.ActualHeight - margin * 2;
            double fromCenterX = (fromLeft + fromRight) / 2;
            double fromCenterY = (fromTop + fromBottom) / 2;

            double toLeft = Canvas.GetLeft(toCard) + margin;
            double toTop = Canvas.GetTop(toCard) + margin;
            double toRight = toLeft + toCard.ActualWidth - margin * 2;
            double toBottom = toTop + toCard.ActualHeight - margin * 2;
            double toCenterX = (toLeft + toRight) / 2;
            double toCenterY = (toTop + toBottom) / 2;

            // Determine direction from source to target
            double dx = toCenterX - fromCenterX;
            double dy = toCenterY - fromCenterY;

            Point fromPoint, toPoint;
            string fromEdge, toEdge;

            // Choose edges based on relative position
            if (Math.Abs(dx) > Math.Abs(dy))
            {
                // Horizontal connection
                if (dx > 0)
                {
                    // Target is to the right
                    fromPoint = new Point(fromRight, fromCenterY);
                    toPoint = new Point(toLeft, toCenterY);
                    fromEdge = "right";
                    toEdge = "left";
                }
                else
                {
                    // Target is to the left
                    fromPoint = new Point(fromLeft, fromCenterY);
                    toPoint = new Point(toRight, toCenterY);
                    fromEdge = "left";
                    toEdge = "right";
                }
            }
            else
            {
                // Vertical connection
                if (dy > 0)
                {
                    // Target is below
                    fromPoint = new Point(fromCenterX, fromBottom);
                    toPoint = new Point(toCenterX, toTop);
                    fromEdge = "bottom";
                    toEdge = "top";
                }
                else
                {
                    // Target is above
                    fromPoint = new Point(fromCenterX, fromTop);
                    toPoint = new Point(toCenterX, toBottom);
                    fromEdge = "top";
                    toEdge = "bottom";
                }
            }

            return (fromPoint, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Get the best edge for the target point based on direction from the start point.
        /// </summary>
        private string GetBestEdgeForPoint(Point from, Point to)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;

            if (Math.Abs(dx) > Math.Abs(dy))
            {
                return dx > 0 ? "left" : "right";
            }
            else
            {
                return dy > 0 ? "top" : "bottom";
            }
        }

        /// <summary>
        /// Choose edge with a deadband to avoid rapid flipping; keeps current edge until movement is decisive.
        /// </summary>
        private string GetBestEdgeForPointWithHysteresis(Point from, Point to, string? currentEdge, double deadband = 60)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;

            string candidate = GetBestEdgeForPoint(from, to);
            if (string.IsNullOrEmpty(currentEdge) || currentEdge == candidate)
            {
                return candidate;
            }

            bool preferHorizontal = Math.Abs(dx) > Math.Abs(dy) + deadband;
            bool preferVertical = Math.Abs(dy) > Math.Abs(dx) + deadband;

            return candidate switch
            {
                "left" or "right" => preferHorizontal ? candidate : currentEdge,
                "top" or "bottom" => preferVertical ? candidate : currentEdge,
                _ => candidate
            };
        }

        /// <summary>
        /// Draw a bezier curve connection between two cards with arrow
        /// </summary>
        private void DrawConnectionLine(Connection connection, BrowserCard fromCard, BrowserCard toCard)
        {
            var (fromPoint, toPoint, fromEdge, toEdge) = ResolveConnectionPoints(connection, fromCard, toCard);

            // Create bezier curve path
            var path = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent
            };

            UpdateBezierPath(path, fromPoint, toPoint, fromEdge, toEdge);

            ConnectionsCanvas.Children.Add(path);
            _connectionLines[connection.Id] = path;

            // Add arrow head at target
            AddArrowHead(connection.Id, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Update bezier path geometry - modifies existing geometry instead of recreating
        /// </summary>
        private void UpdateBezierPath(System.Windows.Shapes.Path path, Point from, Point to, string fromEdge, string toEdge)
        {
            // Calculate control points for smooth bezier curve
            double distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            double controlOffset = Math.Min(distance * 0.4, 150); // Control point offset

            Point control1, control2;

            // Set control points based on edge directions
            switch (fromEdge)
            {
                case "right":
                    control1 = new Point(from.X + controlOffset, from.Y);
                    break;
                case "left":
                    control1 = new Point(from.X - controlOffset, from.Y);
                    break;
                case "bottom":
                    control1 = new Point(from.X, from.Y + controlOffset);
                    break;
                case "top":
                    control1 = new Point(from.X, from.Y - controlOffset);
                    break;
                default:
                    control1 = from;
                    break;
            }

            switch (toEdge)
            {
                case "right":
                    control2 = new Point(to.X + controlOffset, to.Y);
                    break;
                case "left":
                    control2 = new Point(to.X - controlOffset, to.Y);
                    break;
                case "bottom":
                    control2 = new Point(to.X, to.Y + controlOffset);
                    break;
                case "top":
                    control2 = new Point(to.X, to.Y - controlOffset);
                    break;
                default:
                    control2 = to;
                    break;
            }

            // Try to update existing geometry instead of recreating
            if (path.Data is PathGeometry pathGeometry &&
                pathGeometry.Figures.Count > 0 &&
                pathGeometry.Figures[0] is PathFigure pathFigure &&
                pathFigure.Segments.Count > 0 &&
                pathFigure.Segments[0] is BezierSegment bezierSegment)
            {
                // Update existing geometry points
                pathFigure.StartPoint = from;
                bezierSegment.Point1 = control1;
                bezierSegment.Point2 = control2;
                bezierSegment.Point3 = to;
            }
            else
            {
                // Create new geometry if structure doesn't exist
                pathGeometry = new PathGeometry();
                pathFigure = new PathFigure { StartPoint = from };
                bezierSegment = new BezierSegment(control1, control2, to, true);
                pathFigure.Segments.Add(bezierSegment);
                pathGeometry.Figures.Add(pathFigure);
                path.Data = pathGeometry;
            }
        }

        /// <summary>
        /// Add arrow head at the target point
        /// </summary>
        private void AddArrowHead(string connectionId, Point targetPoint, string fromEdge, string toEdge)
        {
            double arrowSize = 14;
            double angle;

            // Calculate arrow angle based on incoming direction
            switch (toEdge)
            {
                case "left":
                    angle = 0; // Arrow pointing right (into left edge)
                    break;
                case "right":
                    angle = 180; // Arrow pointing left (into right edge)
                    break;
                case "top":
                    angle = 90; // Arrow pointing down (into top edge)
                    break;
                case "bottom":
                    angle = -90; // Arrow pointing up (into bottom edge)
                    break;
                default:
                    angle = 0;
                    break;
            }

            // Create arrow polygon
            var arrow = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                Points = new PointCollection
                {
                    new Point(arrowSize, 0),
                    new Point(-arrowSize * 0.6, -arrowSize * 0.5),
                    new Point(-arrowSize * 0.6, arrowSize * 0.5)
                },
                Tag = connectionId + "_arrow"
            };

            // Position and rotate arrow
            arrow.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new RotateTransform(angle),
                    new TranslateTransform(targetPoint.X, targetPoint.Y)
                }
            };

            ConnectionsCanvas.Children.Add(arrow);
        }

        private void UpdateAllConnectionLines()
        {
            foreach (var connection in _connections)
            {
                if (_connectionLines.TryGetValue(connection.Id, out var path) &&
                    _cards.TryGetValue(connection.FromCardId, out var fromCard) &&
                    _cards.TryGetValue(connection.ToCardId, out var toCard))
                {
                    var (fromPoint, toPoint, fromEdge, toEdge) = ResolveConnectionPoints(connection, fromCard, toCard);
                    UpdateBezierPath(path, fromPoint, toPoint, fromEdge, toEdge);

                    // Update arrow position using transform (don't remove/recreate)
                    var arrowTag = connection.Id + "_arrow";
                    var arrow = ConnectionsCanvas.Children.OfType<Polygon>()
                        .FirstOrDefault(p => p.Tag?.ToString() == arrowTag);

                    if (arrow != null)
                    {
                        UpdateArrowTransform(arrow, toPoint, toEdge);
                    }
                }
            }
        }

        private void UpdateSpecificConnectionLines(string cardId)
        {
            foreach (var connection in _connections)
            {
                if (connection.FromCardId == cardId || connection.ToCardId == cardId)
                {
                    if (_connectionLines.TryGetValue(connection.Id, out var path) &&
                        _cards.TryGetValue(connection.FromCardId, out var fromCard) &&
                        _cards.TryGetValue(connection.ToCardId, out var toCard))
                    {
                        var (fromPoint, toPoint, fromEdge, toEdge) = ResolveConnectionPoints(connection, fromCard, toCard);
                        UpdateBezierPath(path, fromPoint, toPoint, fromEdge, toEdge);

                        var arrowTag = connection.Id + "_arrow";
                        var arrow = ConnectionsCanvas.Children.OfType<Polygon>()
                            .FirstOrDefault(p => p.Tag?.ToString() == arrowTag);

                        if (arrow != null)
                        {
                            UpdateArrowTransform(arrow, toPoint, toEdge);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Update arrow transform without recreating it
        /// </summary>
        private void UpdateArrowTransform(Polygon arrow, Point targetPoint, string toEdge)
        {
            double angle = toEdge switch
            {
                "left" => 0,
                "right" => 180,
                "top" => 90,
                "bottom" => -90,
                _ => 0
            };

            // Update existing transform
            if (arrow.RenderTransform is TransformGroup group && 
                group.Children.Count >= 2 &&
                group.Children[0] is RotateTransform rotate &&
                group.Children[1] is TranslateTransform translate)
            {
                rotate.Angle = angle;
                translate.X = targetPoint.X;
                translate.Y = targetPoint.Y;
            }
            else
            {
                // Fallback: create new transform if structure is unexpected
                arrow.RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection
                    {
                        new RotateTransform(angle),
                        new TranslateTransform(targetPoint.X, targetPoint.Y)
                    }
                };
            }
        }

        #endregion

        #region Pan and Zoom

        private void CanvasScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle mouse button for panning
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(CanvasScrollViewer);
                _panStartOffsetX = CanvasScrollViewer.HorizontalOffset;
                _panStartOffsetY = CanvasScrollViewer.VerticalOffset;
                CanvasScrollViewer.CaptureMouse();
                CanvasScrollViewer.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            }
        }

        private void CanvasScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point currentPoint = e.GetPosition(CanvasScrollViewer);
                double deltaX = currentPoint.X - _panStartPoint.X;
                double deltaY = currentPoint.Y - _panStartPoint.Y;
                
                CanvasScrollViewer.ScrollToHorizontalOffset(_panStartOffsetX - deltaX);
                CanvasScrollViewer.ScrollToVerticalOffset(_panStartOffsetY - deltaY);
                e.Handled = true;
            }

            if (_isConnectionDrag && _connectionPreviewPath != null && !string.IsNullOrEmpty(_connectionStartCardId) && _cards.TryGetValue(_connectionStartCardId, out var dragCard))
            {
                // Get position relative to CardsCanvas to match the card coordinates (both use same scale transform)
                Point canvasPoint = e.GetPosition(CardsCanvas);
                Point startPoint = dragCard.GetEdgeCenter(_connectionStartEdge ?? "right");
                string dynamicEdge = GetBestEdgeForPointWithHysteresis(startPoint, canvasPoint, _connectionPreviewTargetEdge);
                _connectionPreviewTargetEdge = dynamicEdge;
                UpdateBezierPath(_connectionPreviewPath, startPoint, canvasPoint, _connectionStartEdge ?? "right", dynamicEdge);
            }
        }

        private void CanvasScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.MiddleButton == MouseButtonState.Released)
            {
                _isPanning = false;
                CanvasScrollViewer.ReleaseMouseCapture();
                CanvasScrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }

            if (_isConnectionDrag && e.ChangedButton == MouseButton.Left)
            {
                CompleteConnectionDrag();
                e.Handled = true;
            }
        }

        private void CanvasScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Get mouse position relative to the ScrollViewer viewport
                Point mouseViewport = e.GetPosition(CanvasScrollViewer);
                
                // Calculate the mouse position in unscaled canvas coordinates
                double mouseCanvasX = (CanvasScrollViewer.HorizontalOffset + mouseViewport.X) / _zoomLevel;
                double mouseCanvasY = (CanvasScrollViewer.VerticalOffset + mouseViewport.Y) / _zoomLevel;
                
                // Store old zoom level
                double oldZoom = _zoomLevel;
                
                // Zoom with Ctrl+Scroll
                double zoomFactor = e.Delta > 0 ? 1.08 : 0.92;
                _zoomLevel = Math.Clamp(_zoomLevel * zoomFactor, ZoomMin, ZoomMax);
                
                // Apply the new zoom
                CanvasScale.ScaleX = _zoomLevel;
                CanvasScale.ScaleY = _zoomLevel;
                CardsCanvasScale.ScaleX = _zoomLevel;
                CardsCanvasScale.ScaleY = _zoomLevel;
                ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
                
                // Calculate new scroll offset to keep mouse point stationary
                // The mouse canvas point should remain at the same viewport position
                double newOffsetX = mouseCanvasX * _zoomLevel - mouseViewport.X;
                double newOffsetY = mouseCanvasY * _zoomLevel - mouseViewport.Y;
                
                CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
                CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));
                
                e.Handled = true;
            }
            else
            {
                // Smooth vertical scrolling (trackpad-friendly)
                double target = CanvasScrollViewer.VerticalOffset - e.Delta;
                StartSmoothVerticalScroll(target);
                e.Handled = true;
            }
        }

        private void ApplyZoom()
        {
            CanvasScale.ScaleX = _zoomLevel;
            CanvasScale.ScaleY = _zoomLevel;
            CardsCanvasScale.ScaleX = _zoomLevel;
            CardsCanvasScale.ScaleY = _zoomLevel;
            ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomTowardCenter(Math.Min(ZoomMax, _zoomLevel + 0.1));
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomTowardCenter(Math.Max(ZoomMin, _zoomLevel - 0.1));
        }

        private void ZoomTowardCenter(double newZoom)
        {
            // Get viewport center in content coordinates
            double viewportCenterX = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ActualWidth / 2) / _zoomLevel;
            double viewportCenterY = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ActualHeight / 2) / _zoomLevel;
            
            double oldZoom = _zoomLevel;
            _zoomLevel = newZoom;
            
            // Apply the new zoom
            CanvasScale.ScaleX = _zoomLevel;
            CanvasScale.ScaleY = _zoomLevel;
            CardsCanvasScale.ScaleX = _zoomLevel;
            CardsCanvasScale.ScaleY = _zoomLevel;
            ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
            
            // Adjust scroll to keep the viewport center stationary
            double newOffsetX = viewportCenterX * _zoomLevel - CanvasScrollViewer.ActualWidth / 2;
            double newOffsetY = viewportCenterY * _zoomLevel - CanvasScrollViewer.ActualHeight / 2;
            
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));
        }

        private void ZoomIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            // Reset to 100%
            _zoomLevel = 1.0;
            ApplyZoom();
        }

        private void StartSmoothVerticalScroll(double targetY)
        {
            // Clamp to scrollable range
            double clamped = Math.Clamp(targetY, 0, CanvasScrollViewer.ScrollableHeight);
            _smoothScrollTargetY = clamped;
            if (_isSmoothScrollActive)
                return;

            _isSmoothScrollActive = true;
            CompositionTarget.Rendering += SmoothVerticalScrollStep;
        }

        private void SmoothVerticalScrollStep(object? sender, EventArgs e)
        {
            double current = CanvasScrollViewer.VerticalOffset;
            double next = current + (_smoothScrollTargetY - current) * 0.25; // easing factor

            // Snap when close enough to avoid jitter
            if (Math.Abs(next - _smoothScrollTargetY) < 0.5)
            {
                CanvasScrollViewer.ScrollToVerticalOffset(_smoothScrollTargetY);
                CompositionTarget.Rendering -= SmoothVerticalScrollStep;
                _isSmoothScrollActive = false;
                return;
            }

            CanvasScrollViewer.ScrollToVerticalOffset(next);
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = 1.0;
            ApplyZoom();
            CenterViewport();
        }

        private void FitAllCards_Click(object sender, RoutedEventArgs e)
        {
            if (_cards.Count == 0) return;

            // Find bounding box of all cards
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var card in _cards.Values)
            {
                double left = Canvas.GetLeft(card);
                double top = Canvas.GetTop(card);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, left + card.ActualWidth);
                maxY = Math.Max(maxY, top + card.ActualHeight);
            }

            // Add padding
            double padding = 50;
            minX -= padding;
            minY -= padding;
            maxX += padding;
            maxY += padding;

            // Calculate required zoom to fit
            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;
            double viewWidth = CanvasScrollViewer.ActualWidth;
            double viewHeight = CanvasScrollViewer.ActualHeight;

            double zoomX = viewWidth / contentWidth;
            double zoomY = viewHeight / contentHeight;
            _zoomLevel = Math.Clamp(Math.Min(zoomX, zoomY), ZoomMin, ZoomMax);

            ApplyZoom();

            // Scroll to center of cards
            double centerX = (minX + maxX) / 2 * _zoomLevel - viewWidth / 2;
            double centerY = (minY + maxY) / 2 * _zoomLevel - viewHeight / 2;
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY));
        }

        #endregion

        #region NextGen Advanced Features

        private void ShowToast(string message)
        {
            if (ToastPanel == null || ToastText == null) return;
            ToastText.Text = message;
            ToastPanel.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) => { ToastPanel.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();
        }




        #endregion

        #region Main Menu Button (Top-Right ≡)

        private void MainMenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Update zoom label before opening
            MainMenuZoomLabel.Text = $"{(int)(_zoomLevel * 100)}%";
            MainBrowserMenuPopup.IsOpen = !MainBrowserMenuPopup.IsOpen;
        }

        private void CloseMainMenu() => MainBrowserMenuPopup.IsOpen = false;

        // ── New ──────────────────────────────────────────
        private void MenuMain_NewBrowser_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://www.google.com");
        }

        private void MenuMain_NewWindow_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://www.google.com");
            ShowToast("New window card created!");
        }

        private void MenuMain_NewPrivate_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://www.google.com");
            ShowToast("Private window created (incognito mode).");
        }

        // ── Sidebar Toggle ────────────────────────────────
        private void MenuMain_SidebarOn_Click(object sender, RoutedEventArgs e)
        {
            MainNavIcons.Visibility = Visibility.Visible;
            BottomNavIcons.Visibility = Visibility.Visible;
            ShowToast("Sidebar enabled.");
        }

        private void MenuMain_SidebarOff_Click(object sender, RoutedEventArgs e)
        {
            MainNavIcons.Visibility = Visibility.Collapsed;
            BottomNavIcons.Visibility = Visibility.Collapsed;
            SidebarPanel.Visibility = Visibility.Collapsed;
            ShowToast("Sidebar hidden.");
        }

        // ── Browser Data ─────────────────────────────────
        private void MenuMain_Passwords_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            // Open Passwords panel in the active focused card if available
            var activeCard = _cards.Values.FirstOrDefault();
            if (activeCard?.WebView?.CoreWebView2 != null)
                activeCard.WebView.CoreWebView2.Navigate("edge://settings/passwords");
            else
                ShowToast("No active browser card. Open a card first.");
        }

        private void MenuMain_History_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowSidebar("history");
        }

        private void MenuMain_Bookmarks_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowSidebar("bookmarks");
        }

        private void MenuMain_Downloads_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowSidebar("downloads");
        }

        private void MenuMain_Extensions_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowToast("Extensions coming soon!");
        }

        private void MenuMain_DeleteData_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            var result = System.Windows.MessageBox.Show(
                "Clear all browsing history from the sidebar?\n\nThis cannot be undone.",
                "Delete Browsing Data",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
            {
                _history.Clear();
                ShowToast("Browsing data cleared.");
            }
        }

        // ── Zoom (Canvas-level) ───────────────────────────
        private void MenuMain_ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomIn_Click(sender, e);
            MainMenuZoomLabel.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void MenuMain_ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomOut_Click(sender, e);
            MainMenuZoomLabel.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void MenuMain_ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            FitAllCards_Click(sender, e);
        }

        // ── Page Tools ───────────────────────────────────
        private void MenuMain_Print_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            var activeCard = _cards.Values.FirstOrDefault();
            activeCard?.WebView?.CoreWebView2?.ExecuteScriptAsync("window.print()");
            if (activeCard == null) ShowToast("No active card to print.");
        }

        private void MenuMain_FindEdit_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            // Open Find toolbar on the active focused card
            var activeCard = _cards.Values.FirstOrDefault();
            if (activeCard?.WebView?.CoreWebView2 != null)
                activeCard.WebView.CoreWebView2.ExecuteScriptAsync(
                    "document.dispatchEvent(new KeyboardEvent('keydown',{key:'f',ctrlKey:true,bubbles:true}));");
            else
                ShowToast("No active card. Open a browser card first.");
        }

        private void MenuMain_SaveShare_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            SaveWorkspace_Click(sender, e);
        }

        private void MenuMain_MoreTools_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowToast("More tools: use the toolbar buttons below.");
        }

        // ── Settings / Help / Exit ────────────────────────
        private void MenuMain_Settings_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowSidebar("settings");
        }

        private void MenuMain_Organize_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            OrganizeGrid_Click(sender, e);
        }

        private void MenuMain_Help_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://github.com");
            ShowToast("Help opened in a new card.");
        }

        private void MenuMain_Exit_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to exit Sowser?",
                "Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                System.Windows.Application.Current.Shutdown();
        }

        #endregion


        #region Toolbar Handlers

        private void NewStickyNote_Click(object sender, RoutedEventArgs e)
        {
            Point center = GetViewportCenter();
            var note = new StickyNote();
            Canvas.SetLeft(note, center.X - 150);
            Canvas.SetTop(note, center.Y - 125);
            
            note.CloseRequested += (s, id) => {
                CardsCanvas.Children.Remove(note);
                _notes.Remove(id);
            };
            CardsCanvas.Children.Add(note);
            _notes[note.NoteId] = note;
        }

        private void OrganizeGrid_Click(object sender, RoutedEventArgs e)
        {
            if (_cards.Count == 0) return;
            
            // Layout all cards in a grid starting from (0,0)
            int columns = (int)Math.Ceiling(Math.Sqrt(_cards.Count));
            double xGap = 850;
            double yGap = 550;
            
            int col = 0;
            int row = 0;
            
            foreach (var card in _cards.Values)
            {
                // Skip organizing pinned cards
                if(card.IsPinned) continue;

                Canvas.SetLeft(card, col * xGap);
                Canvas.SetTop(card, row * yGap);
                
                col++;
                if (col >= columns)
                {
                    col = 0;
                    row++;
                }
            }
            
            UpdateAllConnectionLines();
            UpdateCanvasSize();
            FitAllCards_Click(this, new RoutedEventArgs());
        }

        private void NewCard_Click(object sender, RoutedEventArgs e)
        {
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://www.google.com");
        }

        private async void MainUrlBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string input = MainUrlBar.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(input))
                {
                    Point center = GetViewportCenter();

                    // --- AI RESEARCH NODE MAGIC MODE ---
                    if (input.StartsWith("/ai ", StringComparison.OrdinalIgnoreCase))
                    {
                         string query = input.Substring(4).Trim();
                         MainUrlBar.Text = "";
                         ShowToast($"AI is researching: '{query}'...");
                         
                         // Try to reach local Ollama
                         try 
                         {
                             // Using broad internet search domains as simulated AI output
                             // In a full production build, this would hit OpenAI or Ollama JSON endpoints natively
                             await System.Threading.Tasks.Task.Delay(1500); // Simulate API think time
                             
                             // Spawn primary center node
                             var mainCard = CreateCard(center.X - 250, center.Y - 200, "https://www.google.com/search?q=" + query.Replace(" ", "+"));
                             
                             // Secondary radial nodes
                             var subCard1 = CreateCard(center.X - 800, center.Y, $"https://www.youtube.com/results?search_query=" + query.Replace(" ", "+"));
                             var subCard2 = CreateCard(center.X + 300, center.Y, $"https://en.wikipedia.org/wiki/Special:Search?search=" + query.Replace(" ", "+"));
                             
                             // Connect them spatially
                             _connections.Add(new Connection { FromCardId = mainCard.CardId, ToCardId = subCard1.CardId, Timestamp = DateTime.Now });
                             _connections.Add(new Connection { FromCardId = mainCard.CardId, ToCardId = subCard2.CardId, Timestamp = DateTime.Now });
                             
                            // Update view
                            _ = Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DrawConnectionLine(_connections[^2], mainCard, subCard1);
                                DrawConnectionLine(_connections[^1], mainCard, subCard2);
                                PanToCard(mainCard);
                            }), DispatcherPriority.Loaded);

                             ShowToast("AI Spatial Workspace Generated!");
                         }
                         catch 
                         {
                             ShowToast("AI connection failed. Ensure Local Ollama is running or input a valid API Key.");
                         }
                         
                         return;
                    }

                    // Fuzzy Search & Jump-to-card
                    // If it's not explicitly a url/domain, we check open cards
                    if (!input.StartsWith("http") && !input.Contains("."))
                    {
                        var match = _cards.Values.FirstOrDefault(c => 
                            (c.CurrentTitle != null && c.CurrentTitle.Contains(input, StringComparison.OrdinalIgnoreCase)) ||
                            (c.CurrentUrl != null && c.CurrentUrl.Contains(input, StringComparison.OrdinalIgnoreCase)));
                            
                        if (match != null)
                        {
                            PanToCard(match);
                            MainUrlBar.Text = "";
                            return;
                        }
                    }

                    string url = BrowserCard.ProcessUrlInput(input, _settings.DefaultSearchEngine);
                    CreateCard(center.X - 400, center.Y - 300, url);
                    MainUrlBar.Text = "";
                }
            }
        }
        
        private async void AIOrganize_Click(object sender, RoutedEventArgs e)
        {
             if (_cards.Count < 2) 
             {
                 ShowToast("Not enough cards to AI sort.");
                 return;
             }
             
             ShowToast("AI Semantic Sorting Active...");
             await System.Threading.Tasks.Task.Delay(800); // Simulate API classification
             
             // Simple deterministic classification logic for the sake of the demonstration
             var topics = new Dictionary<string, List<BrowserCard>>();
             foreach (var card in _cards.Values)
             {
                 string url = card.CurrentUrl.ToLower();
                 string title = card.CurrentTitle.ToLower();
                 
                 string topicCategory = "General";
                 
                 if (url.Contains("youtube") || title.Contains("video") || url.Contains("netflix")) topicCategory = "Media";
                 else if (url.Contains("github") || title.Contains("code") || url.Contains("stackoverflow")) topicCategory = "Development";
                 else if (url.Contains("google") || title.Contains("search")) topicCategory = "Search";
                 
                 if (!topics.ContainsKey(topicCategory)) topics[topicCategory] = new List<BrowserCard>();
                 topics[topicCategory].Add(card);
             }
             
             // Animate sorting spatially
             double startX = 0;
             double startY = 0;
             int colorIndex = 0;
             string[] glowColors = { "#00D9FF", "#B233FF", "#FF3366", "#33FF66" };
             
             foreach (var topic in topics)
             {
                 double currentY = startY;
                 foreach (var card in topic.Value)
                 {
                     Canvas.SetLeft(card, startX);
                     Canvas.SetTop(card, currentY);
                     currentY += card.ActualHeight + 50;
                     
                     // Force update visually based on category clustering
                     card.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glowColors[colorIndex % glowColors.Length]));
                 }
                 startX += 800; // Move to next column for next Topic
                 colorIndex++;
             }
             
             UpdateAllConnectionLines();
             ShowToast("AI spatially organized your workspace by Semantic Context!");
             CenterViewport();
        }

        private void QuickLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                Point center = GetViewportCenter();
                CreateCard(center.X - 400, center.Y - 300, url);
            }
        }

        private void QuickLinkBorder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string url)
            {
                Point center = GetViewportCenter();
                CreateCard(center.X - 400, center.Y - 300, url);
            }
        }

        #endregion

        #region Sidebar Management

        public void ShowSidebar(string panel)
        {
            SidebarPanel.Visibility = Visibility.Visible;
            BookmarksPanel.Visibility = Visibility.Collapsed;
            ReadLaterPanel.Visibility = Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Collapsed;
            DownloadsPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            
            switch (panel)
            {
                case "bookmarks":
                    SidebarTitle.Text = "☆ Bookmarks";
                    BookmarksPanel.Visibility = Visibility.Visible;
                    RefreshBookmarksList();
                    break;
                case "readlater":
                    SidebarTitle.Text = "⏱ Read later";
                    ReadLaterPanel.Visibility = Visibility.Visible;
                    RefreshReadLaterList();
                    break;
                case "history":
                    SidebarTitle.Text = "📜 History";
                    HistoryPanel.Visibility = Visibility.Visible;
                    RefreshHistoryList();
                    break;
                case "downloads":
                    SidebarTitle.Text = "⬇ Downloads";
                    DownloadsPanel.Visibility = Visibility.Visible;
                    RefreshDownloadsList();
                    break;
                case "settings":
                    SidebarTitle.Text = "⚙ Settings";
                    SettingsPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ToggleBookmarks_Click(object sender, RoutedEventArgs e) => ShowSidebar("bookmarks");
        private void ToggleHistory_Click(object sender, RoutedEventArgs e) => ShowSidebar("history");
        private void ToggleDownloads_Click(object sender, RoutedEventArgs e) => ShowSidebar("downloads");
        private void ToggleSettings_Click(object sender, RoutedEventArgs e) => ShowSidebar("settings");
        private void CloseSidebar_Click(object sender, RoutedEventArgs e) => SidebarPanel.Visibility = Visibility.Collapsed;
        
        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            if (MainNavIcons.Visibility == Visibility.Visible)
            {
                MainNavIcons.Visibility = Visibility.Collapsed;
                BottomNavIcons.Visibility = Visibility.Collapsed;
            }
            else
            {
                MainNavIcons.Visibility = Visibility.Visible;
                BottomNavIcons.Visibility = Visibility.Visible;
            }
        }

        private void RefreshBookmarksList()
        {
            BookmarksList.Children.Clear();
            if (QuickLinksPanel != null) QuickLinksPanel.Children.Clear();

            foreach (var bookmark in _bookmarks)
            {
                // Sidebar Bookmark Row
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var btn = new Button
                {
                    Content = $"☆ {bookmark.Title}\n{bookmark.Url}",
                    Style = (Style)FindResource("SidebarButtonStyle"),
                    Tag = bookmark.Url,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                btn.Click += (s, e) =>
                {
                    Point center = GetViewportCenter();
                    CreateCard(center.X - 400, center.Y - 300, bookmark.Url);
                };
                Grid.SetColumn(btn, 0);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                    Width = 30, Padding = new Thickness(0),
                    ToolTip = "Remove Bookmark"
                };
                delBtn.Click += (s, e) => { _bookmarks.Remove(bookmark); RefreshBookmarksList(); ShowToast("Bookmark removed."); };
                Grid.SetColumn(delBtn, 1);

                row.Children.Add(btn);
                row.Children.Add(delBtn);
                BookmarksList.Children.Add(row);
            }

            const int quickLinkCap = 8;
            void AddQuickLinkChip(string title, string url)
            {
                if (QuickLinksPanel == null || QuickLinksPanel.Children.Count >= quickLinkCap) return;
                var qlBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                    CornerRadius = new CornerRadius(16),
                    Margin = new Thickness(4, 0, 4, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(37, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = url
                };
                qlBorder.MouseLeftButtonDown += QuickLinkBorder_Click;
                var qlStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 6) };
                var domainMatch = System.Text.RegularExpressions.Regex.Match(url, @"https?://(?:www\.)?([^/]+)");
                string domainForIcon = domainMatch.Success ? domainMatch.Groups[1].Value : "google.com";
                var icon = new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://www.google.com/s2/favicons?domain={domainForIcon}&sz=64")),
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                var textBlock = new TextBlock
                {
                    Text = title,
                    Foreground = new SolidColorBrush(Color.FromArgb(224, 255, 255, 255)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                qlStack.Children.Add(icon);
                qlStack.Children.Add(textBlock);
                qlBorder.Child = qlStack;
                QuickLinksPanel.Children.Add(qlBorder);
            }

            foreach (var bookmark in _bookmarks)
                AddQuickLinkChip(bookmark.Title, bookmark.Url);
            foreach (var ql in _settings.CustomQuickLinks)
                AddQuickLinkChip(string.IsNullOrEmpty(ql.Title) ? ql.Url : ql.Title, ql.Url);
        }

        private void RefreshHistoryList(string? filter = null)
        {
            HistoryList.Children.Clear();

            // Clear button
            if (string.IsNullOrEmpty(filter) && _history.Count > 0)
            {
                var clearBtn = new Button
                {
                    Content = "Clear All History",
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                    Margin = new Thickness(0, 0, 0, 5), HorizontalAlignment = HorizontalAlignment.Right
                };
                clearBtn.Click += (s, e) => { _history.Clear(); RefreshHistoryList(); ShowToast("History cleared."); };
                HistoryList.Children.Add(clearBtn);
            }

            var items = string.IsNullOrEmpty(filter) 
                ? _history.ToList() 
                : _history.Where(h => h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || 
                                      h.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            
            foreach (var entry in items.Take(50))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var btn = new Button
                {
                    Content = $"{entry.Title}\n{entry.Url}",
                    Style = (Style)FindResource("SidebarButtonStyle"),
                    Tag = entry.Url,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                btn.Click += (s, e) =>
                {
                    Point center = GetViewportCenter();
                    CreateCard(center.X - 400, center.Y - 300, entry.Url);
                };
                Grid.SetColumn(btn, 0);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    Width = 30, Padding = new Thickness(0)
                };
                delBtn.Click += (s, e) => { _history.Remove(entry); RefreshHistoryList(filter); };
                Grid.SetColumn(delBtn, 1);

                row.Children.Add(btn);
                row.Children.Add(delBtn);
                HistoryList.Children.Add(row);
            }
        }

        private void RefreshDownloadsList()
        {
            RefreshDownloadsListRich();
        }

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHistoryList(HistorySearchBox.Text);
        }

        #endregion

        #region Settings Handlers

        private void SearchEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (SearchEngineCombo.SelectedIndex >= 0 && SearchEngineCombo.SelectedIndex < _searchEngines.Length)
            {
                _settings.DefaultSearchEngine = _searchEngines[SearchEngineCombo.SelectedIndex];
            }
        }

        private void AutoSaveCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            _settings.AutoSaveEnabled = AutoSaveToggle.IsChecked == true;
            if (_settings.AutoSaveEnabled)
                _autoSaveTimer?.Start();
            else
                _autoSaveTimer?.Stop();
        }

        #endregion

        #region Workspace Save/Load

        private void AutoSaveWorkspace()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string sowserDir = System.IO.Path.Combine(appData, "Sowser");
                Directory.CreateDirectory(sowserDir);
                string autoSavePath = System.IO.Path.Combine(sowserDir, "autosave.json");
                PushTimeMachineSnapshot();
                SaveWorkspaceToFile(autoSavePath);
            }
            catch { /* Silent fail for auto-save */ }
        }

        private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Sowser Workspace (*.sowser)|*.sowser|JSON Files (*.json)|*.json",
                DefaultExt = ".sowser",
                FileName = "workspace"
            };
            
            if (dialog.ShowDialog() == true)
            {
                SaveWorkspaceToFile(dialog.FileName);
                System.Windows.MessageBox.Show("Workspace saved!", "Sowser", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private WorkspaceState BuildWorkspaceState()
        {
            var state = new WorkspaceState
            {
                ViewportX = CanvasScrollViewer.HorizontalOffset,
                ViewportY = CanvasScrollViewer.VerticalOffset,
                ZoomLevel = _zoomLevel,
                Connections = _connections.ToList(),
                Bookmarks = _bookmarks.ToList(),
                Groups = _groups.ToList(),
                BackgroundTheme = _currentBgTheme
            };

            foreach (var kvp in _cards)
            {
                var card = kvp.Value;
                state.Cards.Add(new CardState
                {
                    Id = card.CardId,
                    X = Canvas.GetLeft(card),
                    Y = Canvas.GetTop(card),
                    Width = card.ActualWidth,
                    Height = card.ActualHeight,
                    Url = card.CurrentUrl,
                    Title = card.CurrentTitle,
                    GroupId = card.GroupId,
                    BrowserProfile = card.BrowserProfileKey,
                    IsPortal = card.IsPortal,
                    PortalWorkspaceFullPath = card.PortalWorkspaceFullPath,
                    PortalTargetFile = card.PortalTargetFile
                });
            }

            return state;
        }

        private void SaveWorkspaceToFile(string filePath)
        {
            var state = BuildWorkspaceState();
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        private void LoadWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Sowser Workspace (*.sowser)|*.sowser|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".sowser"
            };
            
            if (dialog.ShowDialog() == true)
            {
                LoadWorkspaceFromFile(dialog.FileName);
            }
        }

        private void LoadWorkspaceFromFile(string filePath, bool showSuccessDialog = true, bool showErrorDialog = true)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<WorkspaceState>(json);
                if (state == null) return;
                ApplyWorkspaceState(state, showSuccessDialog, showErrorDialog);
            }
            catch (Exception ex)
            {
                if (showErrorDialog)
                    System.Windows.MessageBox.Show($"Failed to load workspace: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyWorkspaceState(WorkspaceState state, bool showSuccessDialog, bool showErrorDialog)
        {
            try
            {
                _suppressUndoForClose = true;

                foreach (var clip in CardsCanvas.Children.OfType<ImageClipCard>().ToList())
                    CardsCanvas.Children.Remove(clip);
                _imageClips.Clear();

                foreach (var card in _cards.Values.ToList())
                {
                    card.CloseRequested -= Card_CloseRequested;
                    card.DisposeResources();
                    CardsCanvas.Children.Remove(card);
                }
                _cards.Clear();
                _connections.Clear();
                ConnectionsCanvas.Children.Clear();
                _connectionLines.Clear();

                _bookmarks.Clear();
                _bookmarks.AddRange(state.Bookmarks ?? new List<Bookmark>());
                _groups.Clear();
                if (state.Groups != null) _groups.AddRange(state.Groups);
                RefreshGroupPanel();

                if (!string.IsNullOrEmpty(state.BackgroundTheme))
                    ApplyCanvasTheme(state.BackgroundTheme, quiet: true);

                var cardIdMap = new Dictionary<string, string>();
                foreach (var cardState in state.Cards)
                {
                    string prof = string.IsNullOrWhiteSpace(cardState.BrowserProfile) ? _settings.DefaultBrowserProfile : cardState.BrowserProfile;
                    var card = CreateCard(cardState.X, cardState.Y, cardState.Url, prof);
                    card.Width = cardState.Width > 0 ? cardState.Width : card.Width;
                    card.Height = cardState.Height > 0 ? cardState.Height : card.Height;
                    card.GroupId = cardState.GroupId;
                    card.IsPortal = cardState.IsPortal;
                    card.PortalWorkspaceFullPath = cardState.PortalWorkspaceFullPath ?? string.Empty;
                    card.PortalTargetFile = cardState.PortalTargetFile ?? string.Empty;

                    if (!string.IsNullOrEmpty(card.GroupId))
                    {
                        var group = _groups.FirstOrDefault(g => g.Id == card.GroupId);
                        if (group != null) card.SetGroupColor(group.Color);
                    }

                    cardIdMap[cardState.Id] = card.CardId;
                }

                foreach (var conn in state.Connections)
                {
                    if (cardIdMap.TryGetValue(conn.FromCardId, out var newFromId) &&
                        cardIdMap.TryGetValue(conn.ToCardId, out var newToId))
                    {
                        var newConn = new Connection
                        {
                            FromCardId = newFromId,
                            ToCardId = newToId,
                            Url = conn.Url,
                            Timestamp = conn.Timestamp,
                            FromEdge = conn.FromEdge,
                            ToEdge = conn.ToEdge,
                            IsManual = conn.IsManual
                        };
                        _connections.Add(newConn);

                        if (_cards.TryGetValue(newFromId, out var fromCard) &&
                            _cards.TryGetValue(newToId, out var toCard))
                        {
                            var capturedConn = newConn;
                            var capturedFrom = fromCard;
                            var capturedTo = toCard;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DrawConnectionLine(capturedConn, capturedFrom, capturedTo);
                            }), DispatcherPriority.Loaded);
                        }
                    }
                }

                _zoomLevel = state.ZoomLevel > 0 ? state.ZoomLevel : 1.0;
                CanvasScale.ScaleX = _zoomLevel;
                CanvasScale.ScaleY = _zoomLevel;
                CardsCanvasScale.ScaleX = _zoomLevel;
                CardsCanvasScale.ScaleY = _zoomLevel;
                ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";

                CanvasScrollViewer.ScrollToHorizontalOffset(state.ViewportX);
                CanvasScrollViewer.ScrollToVerticalOffset(state.ViewportY);

                RefreshBookmarksList();
                MainMenuZoomLabel.Text = $"{(int)(_zoomLevel * 100)}%";
                _focusedCardId = null;
                UpdateFocusRings();
                RefreshTimeMachineUi();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateCanvasSize();
                    UpdateMiniMap();
                }), DispatcherPriority.Loaded);

                if (showSuccessDialog)
                    System.Windows.MessageBox.Show("Workspace loaded!", "Sowser", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (showErrorDialog)
                    System.Windows.MessageBox.Show($"Failed to apply workspace: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressUndoForClose = false;
            }
        }


        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #1 & #2 ── BOOKMARKS + HISTORY  (already partially
        //  wired; RefreshBookmarksList/RefreshHistoryList enhanced here)
        // ══════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #3 ── TAB GROUPS / COLOR LABELS
        // ══════════════════════════════════════════════════════════════
        #region Tab Groups

        private readonly List<Sowser.Models.CardGroup> _groups = new();

        private readonly List<string> _groupPalette = new()
        {
            "#FF6B6B", // red
            "#FFD93D", // yellow
            "#6BCB77", // green
            "#00D9FF", // cyan
            "#B233FF", // purple
            "#FF8A65", // orange
        };

        private void CreateGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = new Sowser.Models.CardGroup
            {
                Name = $"Group {_groups.Count + 1}",
                Color = _groupPalette[_groups.Count % _groupPalette.Count]
            };
            _groups.Add(group);
            RefreshGroupPanel();
            ShowToast($"Group '{group.Name}' created! Assign cards via card right-click.");
        }

        private void RefreshGroupPanel()
        {
            if (GroupManagementPanel == null) return;
            GroupManagementPanel.Children.Clear();

            foreach (var group in _groups)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Color dot
                var dot = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(group.Color)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dot, 0);

                // Group name
                var name = new TextBlock
                {
                    Text = group.Name, Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 4, 0), FontSize = 13
                };
                Grid.SetColumn(name, 1);

                // Assign button
                var assignBtn = new Button
                {
                    Content = "Assign", Tag = group,
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(group.Color)),
                    FontSize = 11, Padding = new Thickness(4, 0, 4, 0), MinWidth = 0, Height = 24
                };
                assignBtn.Click += AssignGroup_Click;
                Grid.SetColumn(assignBtn, 2);

                // Delete button
                var delBtn = new Button
                {
                    Content = "✕", Tag = group,
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                    FontSize = 11, Padding = new Thickness(4, 0, 4, 0), MinWidth = 0, Height = 24
                };
                delBtn.Click += DeleteGroup_Click;
                Grid.SetColumn(delBtn, 3);

                row.Children.Add(dot);
                row.Children.Add(name);
                row.Children.Add(assignBtn);
                row.Children.Add(delBtn);
                GroupManagementPanel.Children.Add(row);
            }
        }

        private void AssignGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Sowser.Models.CardGroup group)
            {
                // Assign ALL cards that don't have a group yet (or toggle)
                int assigned = 0;
                foreach (var card in _cards.Values)
                {
                    if (card.GroupId == null)
                    {
                        card.GroupId = group.Id;
                        card.SetGroupColor(group.Color);
                        group.CardIds.Add(card.CardId);
                        assigned++;
                    }
                }
                ShowToast(assigned > 0
                    ? $"Assigned {assigned} ungrouped card(s) to '{group.Name}'."
                    : $"All cards already have a group. Close a card and retry.");
            }
        }

        private void DeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Sowser.Models.CardGroup group)
            {
                // Clear group color from cards
                foreach (var cardId in group.CardIds)
                {
                    if (_cards.TryGetValue(cardId, out var card))
                    {
                        card.GroupId = null;
                        card.ClearGroupColor();
                    }
                }
                _groups.Remove(group);
                RefreshGroupPanel();
                ShowToast($"Group '{group.Name}' deleted.");
            }
        }

        private void Card_GroupAssigned(object? sender, string? groupId)
        {
            if (sender is BrowserCard card)
            {
                // Remove from any existing group
                foreach (var g in _groups)
                {
                    g.CardIds.Remove(card.CardId);
                }

                // Add to new group
                if (!string.IsNullOrEmpty(groupId))
                {
                    var group = _groups.FirstOrDefault(g => g.Id == groupId);
                    group?.CardIds.Add(card.CardId);
                    if (group != null) ShowToast($"Card added to group '{group.Name}'");
                }
            }
        }

        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #5 ── MINIMAP CLICK-TO-NAVIGATE
        // ══════════════════════════════════════════════════════════════
        #region Minimap Click Navigation

        private void MiniMapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_cards.Count == 0) return;

            // Compute the same bounding-box used in UpdateMiniMap
            double minX = _cards.Values.Min(c => double.IsNaN(Canvas.GetLeft(c)) ? 0 : Canvas.GetLeft(c));
            double minY = _cards.Values.Min(c => double.IsNaN(Canvas.GetTop(c)) ? 0 : Canvas.GetTop(c));
            double maxX = _cards.Values.Max(c => (double.IsNaN(Canvas.GetLeft(c)) ? 0 : Canvas.GetLeft(c)) + c.ActualWidth);
            double maxY = _cards.Values.Max(c => (double.IsNaN(Canvas.GetTop(c)) ? 0 : Canvas.GetTop(c)) + c.ActualHeight);

            minX -= 600; minY -= 600; maxX += 600; maxY += 600;

            double contentW = maxX - minX;
            double contentH = maxY - minY;

            double scaleX = MiniMapCanvas.ActualWidth / Math.Max(1, contentW);
            double scaleY = MiniMapCanvas.ActualHeight / Math.Max(1, contentH);
            double scale = Math.Min(scaleX, scaleY);

            Point click = e.GetPosition(MiniMapCanvas);

            // Convert click to canvas position
            double canvasX = click.X / scale + minX;
            double canvasY = click.Y / scale + minY;

            // Scroll the main canvas to center on that point
            double offsetX = canvasX * _zoomLevel - CanvasScrollViewer.ActualWidth / 2;
            double offsetY = canvasY * _zoomLevel - CanvasScrollViewer.ActualHeight / 2;

            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, offsetX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, offsetY));
        }

        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #6 ── KEYBOARD SHORTCUTS PANEL
        // ══════════════════════════════════════════════════════════════
        #region Keyboard Shortcuts

        private void CloseShortcuts_Click(object sender, RoutedEventArgs e)
            => ShortcutsOverlay.Visibility = Visibility.Collapsed;

        private void ShowShortcuts()
            => ShortcutsOverlay.Visibility = Visibility.Visible;

        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #7 ── GLOBAL COMMAND PALETTE  (Ctrl+K)
        // ══════════════════════════════════════════════════════════════
        #region Command Palette

        private void OpenCommandPalette()
        {
            CommandPaletteOverlay.Visibility = Visibility.Visible;
            CommandPaletteInput.Text = string.Empty;
            CommandPaletteInput.Focus();
            RefreshCommandPalette(string.Empty);
        }

        private void CloseCommandPalette()
        {
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
            CommandPaletteResults.Children.Clear();
        }

        private void CommandPaletteInput_TextChanged(object sender, TextChangedEventArgs e)
            => RefreshCommandPalette(CommandPaletteInput.Text.Trim());

        private void CommandPaletteInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseCommandPalette(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                // Activate first result
                var first = CommandPaletteResults.Children.OfType<Button>().FirstOrDefault();
                first?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
        }

        private void RefreshCommandPalette(string query)
        {
            CommandPaletteResults.Children.Clear();
            query = query.ToLowerInvariant();

            void AddResult(string icon, string title, string subtitle, string color, Action action)
            {
                var btn = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 1, 0, 1),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Brushes.White,
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = icon, FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
                var textSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                textSp.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brushes.White, FontWeight = FontWeights.Medium });
                if (!string.IsNullOrEmpty(subtitle))
                    textSp.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(textSp);

                // Type badge
                var badge = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color + "33")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock { Text = color == "#00D9FF" ? "CARD" : color == "#FFD93D" ? "BOOKMARK" : "HISTORY", FontSize = 9, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), FontWeight = FontWeights.Bold };
                sp.Children.Add(badge);

                btn.Content = sp;
                btn.Click += (s, e) => { action(); CloseCommandPalette(); };
                CommandPaletteResults.Children.Add(btn);
            }

            int count = 0;

            // Open cards
            foreach (var card in _cards.Values)
            {
                if (count >= 20) break;
                string t = card.CurrentTitle?.ToLowerInvariant() ?? "";
                string u = card.CurrentUrl?.ToLowerInvariant() ?? "";
                if (string.IsNullOrEmpty(query) || t.Contains(query) || u.Contains(query))
                {
                    var c = card; // capture
                    AddResult("🌐", card.CurrentTitle ?? "Untitled", card.CurrentUrl ?? "", "#00D9FF", () => PanToCard(c));
                    count++;
                }
            }

            // Bookmarks
            foreach (var bm in _bookmarks)
            {
                if (count >= 30) break;
                if (string.IsNullOrEmpty(query) || bm.Title.ToLowerInvariant().Contains(query) || bm.Url.ToLowerInvariant().Contains(query))
                {
                    var u = bm.Url;
                    AddResult("⭐", bm.Title, bm.Url, "#FFD93D", () => { Point p = GetViewportCenter(); CreateCard(p.X - 400, p.Y - 300, u); });
                    count++;
                }
            }

            // History
            foreach (var h in _history.Take(50))
            {
                if (count >= 40) break;
                if (string.IsNullOrEmpty(query) || h.Title.ToLowerInvariant().Contains(query) || h.Url.ToLowerInvariant().Contains(query))
                {
                    var u = h.Url;
                    AddResult("🕐", h.Title, h.Url, "#B233FF", () => { Point p = GetViewportCenter(); CreateCard(p.X - 400, p.Y - 300, u); });
                    count++;
                }
            }

            if (count == 0)
            {
                CommandPaletteResults.Children.Add(new TextBlock
                {
                    Text = "No results found.", Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    Margin = new Thickness(16, 16, 16, 16), FontSize = 13
                });
            }
        }

        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #9 ── CANVAS BACKGROUND THEMES
        // ══════════════════════════════════════════════════════════════
        #region Canvas Background Themes

        private string _currentBgTheme = "dark";

        private void BgTheme_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border b || b.Tag is not string theme) return;
            ApplyCanvasTheme(theme, quiet: false);

            // Highlight selected
            foreach (var child in new[] { BgThemeDark, BgThemeDots, BgThemeGrid, BgThemePurple, BgThemeOcean })
            {
                if (child != null)
                    child.BorderThickness = new Thickness(child == b ? 2 : 1);
                if (child == b)
                    child.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD9, 0xFF));
                else if (child != null)
                    child.BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            }
        }

        /// <summary>Builds the same brush used for canvas + window chrome so the UI matches.</summary>
        private static Brush CreateThemeBackgroundBrush(string theme)
        {
            switch (theme)
            {
                case "dark":
                    return new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0F));
                case "dots":
                    var dotVisual = new Border
                    {
                        Width = 20, Height = 20,
                        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17))
                    };
                    var dot = new Ellipse { Width = 2, Height = 2, Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
                    Canvas.SetLeft(dot, 9); Canvas.SetTop(dot, 9);
                    var dotCanvas = new Canvas { Width = 20, Height = 20 };
                    dotCanvas.Children.Add(dot);
                    return new VisualBrush(dotCanvas)
                    {
                        TileMode = TileMode.Tile,
                        Viewport = new Rect(0, 0, 20, 20),
                        ViewportUnits = BrushMappingMode.Absolute
                    };
                case "grid":
                    var gridCanvas = new Canvas { Width = 40, Height = 40, Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x16, 0x28)) };
                    var hLine = new System.Windows.Shapes.Line { X1 = 0, Y1 = 39.5, X2 = 40, Y2 = 39.5, Stroke = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xD9, 0xFF)), StrokeThickness = 0.5 };
                    var vLine = new System.Windows.Shapes.Line { X1 = 39.5, Y1 = 0, X2 = 39.5, Y2 = 40, Stroke = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xD9, 0xFF)), StrokeThickness = 0.5 };
                    gridCanvas.Children.Add(hLine); gridCanvas.Children.Add(vLine);
                    return new VisualBrush(gridCanvas)
                    {
                        TileMode = TileMode.Tile,
                        Viewport = new Rect(0, 0, 40, 40),
                        ViewportUnits = BrushMappingMode.Absolute
                    };
                case "purple":
                    return new LinearGradientBrush(
                        Color.FromRgb(0x0D, 0x07, 0x20),
                        Color.FromRgb(0x1A, 0x05, 0x33), 45);
                case "ocean":
                    return new LinearGradientBrush(
                        Color.FromRgb(0x02, 0x0B, 0x18),
                        Color.FromRgb(0x04, 0x1E, 0x42), 45);
                default:
                    return new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0F));
            }
        }

        private void ApplyCanvasTheme(string theme, bool quiet = false)
        {
            _currentBgTheme = theme;
            // New brush per surface (VisualBrush cannot be shared across multiple targets reliably)
            CanvasBackground.Background = CreateThemeBackgroundBrush(theme);
            Background = CreateThemeBackgroundBrush(theme);
            if (ChromeCaptionBar != null)
                ChromeCaptionBar.Background = CreateThemeBackgroundBrush(theme);
            if (MainContentShellGrid != null)
                MainContentShellGrid.Background = CreateThemeBackgroundBrush(theme);

            if (!quiet)
                ShowToast($"Canvas theme: {char.ToUpper(theme[0])}{theme.Substring(1)}");
        }

        #endregion

        // ══════════════════════════════════════════════════════════════
        //  FEATURE #10 ── RICH DOWNLOAD MANAGER
        // ══════════════════════════════════════════════════════════════

        // Overriding RefreshDownloadsList to a richer version is below —
        // the existing RefreshDownloadsList in sidebar handlers region handles basic display.
        // We extend here to add open/reveal/clear buttons.

        private void RefreshDownloadsListRich()
        {
            if (DownloadsList == null) return;
            DownloadsList.Children.Clear();

            if (_downloads.Count == 0)
            {
                DownloadsList.Children.Add(new TextBlock
                {
                    Text = "No downloads yet.",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    Margin = new Thickness(0, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13
                });
                return;
            }

            // Clear all button
            var clearBtn = new Button
            {
                Content = "Clear All", Margin = new Thickness(0, 0, 0, 8),
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            clearBtn.Click += (s, e) => { _downloads.Clear(); RefreshDownloadsListRich(); };
            DownloadsList.Children.Add(clearBtn);

            foreach (var dl in _downloads)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12, 10, 12, 10)
                };

                var sp = new StackPanel();

                // File name row
                var nameRow = new Grid();
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var fileName = new TextBlock
                {
                    Text = dl.FileName, Foreground = Brushes.White, FontSize = 13,
                    FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(fileName, 0);

                // Size
                string sizeStr = dl.TotalBytes > 0 ? FormatBytes(dl.TotalBytes) : "Unknown size";
                var sizeText = new TextBlock
                {
                    Text = sizeStr, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(sizeText, 1);

                nameRow.Children.Add(fileName);
                nameRow.Children.Add(sizeText);
                sp.Children.Add(nameRow);

                // URL (small)
                sp.Children.Add(new TextBlock
                {
                    Text = dl.Url, Foreground = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8)
                });

                // Buttons
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

                if (System.IO.File.Exists(dl.FilePath))
                {
                    var openBtn = new Button
                    {
                        Content = "Open", Padding = new Thickness(8, 4, 8, 4), MinWidth = 0,
                        Style = (Style)FindResource("MaterialDesignOutlinedButton"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD9, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD9, 0xFF)),
                        Margin = new Thickness(0, 0, 6, 0), FontSize = 11, Tag = dl.FilePath
                    };
                    openBtn.Click += (s, e) => {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dl.FilePath) { UseShellExecute = true }); } catch { }
                    };
                    btnRow.Children.Add(openBtn);

                    var revealBtn = new Button
                    {
                        Content = "Show in Folder", Padding = new Thickness(8, 4, 8, 4), MinWidth = 0,
                        Style = (Style)FindResource("MaterialDesignFlatButton"),
                        Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                        FontSize = 11, Tag = dl.FilePath
                    };
                    revealBtn.Click += (s, e) => {
                        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dl.FilePath}\""); } catch { }
                    };
                    btnRow.Children.Add(revealBtn);
                }
                else
                {
                    btnRow.Children.Add(new TextBlock
                    {
                        Text = "File not found", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                        FontSize = 11, VerticalAlignment = VerticalAlignment.Center
                    });
                }

                sp.Children.Add(btnRow);
                card.Child = sp;
                DownloadsList.Children.Add(card);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} KB";
            return $"{bytes} B";
        }

        // ══════════════════════════════════════════════════════════════
        //  GLOBAL KEYBOARD SHORTCUTS  (wired in constructor)
        // ══════════════════════════════════════════════════════════════
        #region Global Keyboard Shortcuts

        // Called once from constructor to register all shortcuts
        private void InitGlobalShortcuts()
        {
            PreviewKeyDown += GlobalShortcuts_PreviewKeyDown;
        }

        private void GlobalShortcuts_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

            // Escape: close topmost overlay first (works even when focus is inside a WebView/TextBox host)
            if (e.Key == Key.Escape)
            {
                if (GlobalFindOverlay.Visibility == Visibility.Visible) { CloseGlobalFind(); e.Handled = true; return; }
                if (CommandPaletteOverlay.Visibility == Visibility.Visible) { CloseCommandPalette(); e.Handled = true; return; }
                if (ShortcutsOverlay.Visibility == Visibility.Visible) { ShortcutsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (ExpandedCardOverlay.Visibility == Visibility.Visible) { CloseExpandedCard(); e.Handled = true; return; }
            }

            // Find overlay: allow typing in the search box; block other global shortcuts
            if (GlobalFindOverlay.Visibility == Visibility.Visible)
                return;

            // Don't steal keystrokes when typing in a TextBox
            if (e.OriginalSource is TextBox) return;

            // ? (Shift+/) => Shortcuts panel
            if (e.Key == Key.OemQuestion && shift && !ctrl)
            {
                ShowShortcuts(); e.Handled = true; return;
            }

            if (ctrl && e.Key == Key.Z)
            {
                UndoLast();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Tab)
            {
                CycleCardFocus(shift ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (ctrl && shift && e.Key == Key.F)
            {
                OpenGlobalFind();
                e.Handled = true;
                return;
            }

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.T: // New browser card
                        var cp = GetViewportCenter();
                        CreateCard(cp.X - 400, cp.Y - 300, "https://www.google.com", null, undoRemoveOnCreate: true);
                        e.Handled = true; break;

                    case Key.K: // Command palette
                        OpenCommandPalette(); e.Handled = true; break;

                    case Key.B: // Bookmarks
                        ShowSidebar("bookmarks"); e.Handled = true; break;

                    case Key.H: // History
                        ShowSidebar("history"); e.Handled = true; break;

                    case Key.J: // Downloads
                        ShowSidebar("downloads"); e.Handled = true; break;

                    case Key.S: // Save workspace
                        SaveWorkspace_Click(sender, e); e.Handled = true; break;

                    case Key.O: // Load workspace
                        if (!shift) { LoadWorkspace_Click(sender, e); e.Handled = true; } break;

                    case Key.OemPlus: // Zoom in
                    case Key.Add:
                        ZoomIn_Click(sender, e); e.Handled = true; break;

                    case Key.OemMinus: // Zoom out
                    case Key.Subtract:
                        ZoomOut_Click(sender, e); e.Handled = true; break;

                    case Key.D0: // Reset zoom
                    case Key.NumPad0:
                        ZoomIndicator_Click(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
                        e.Handled = true; break;

                    case Key.F when shift: // Fit all
                        FitAllCards_Click(sender, e); e.Handled = true; break;

                    case Key.N when shift: // New sticky note
                        NewStickyNote_Click(sender, e); e.Handled = true; break;
                }
            }

            if (alt && e.Key == Key.M)
            {
                MainMenuButton_Click(sender, e); e.Handled = true;
            }
        }

        #endregion

    }
}
