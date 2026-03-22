using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Sowser.Services;

namespace Sowser.Controls
{
    /// <summary>
    /// A draggable browser card with WebView2 integration
    /// </summary>
    public partial class BrowserCard : UserControl
    {
        // Static command for fullscreen
        public static readonly RoutedCommand FullscreenCommand = new RoutedCommand();

        public string CardId { get; } = Guid.NewGuid().ToString();
        public string CurrentUrl { get; private set; } = string.Empty;
        public string CurrentTitle { get; set; } = "New Tab";

        // Events for parent window communication
        public event EventHandler<string>? CloseRequested;
        public event EventHandler<LinkClickedEventArgs>? LinkClicked;
        public event EventHandler<string>? BookmarkRequested;
        public event EventHandler<HistoryEventArgs>? NavigationCompleted;
        public event EventHandler? CardMoved;
        public event EventHandler<DownloadEventArgs>? DownloadStarted;
        public event EventHandler<ConnectionPointEventArgs>? ConnectionPointPressed;
        public event EventHandler<ConnectionPointEventArgs>? ConnectionPointHoverChanged;
        public event EventHandler<string>? FullscreenRequested;
        public event EventHandler<BrowserCard>? CardDropped;
        public event EventHandler<BrowserCard>? PinToggled;
        public event EventHandler<string>? PortalOpened;
        public event EventHandler<GroupRequestEventArgs>? GroupListRequested;
        public event EventHandler<string?>? GroupAssigned;
        public event EventHandler? ReadLaterRequested;
        public event EventHandler? CapturePreviewToCanvasRequested;
        public event EventHandler? CardInteracted;

        /// <summary>WebView2 user-data subfolder name (isolated cookies/storage per profile).</summary>
        public string BrowserProfileKey { get; set; } = "default";
        
        public bool IsGhostMode { get; private set; }
        public bool IsPinned { get; private set; }
        public bool IsPortal { get; set; }
        public string PortalTargetFile { get; set; } = string.Empty;
        public string PortalWorkspaceFullPath { get; set; } = string.Empty;

        // Per-card browser page zoom level (1.0 = 100%)
        private double _pageZoomLevel = 1.0;

        // Drag state
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        // Resize state
        private bool _isResizing;
        private Point _resizeStartPoint;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private FrameworkElement? _activeResizeHandle;

        // WebView initialization state
        private bool _webViewInitialized;
        private string? _pendingUrl;

        // Connection overlay state
        private bool _isPointerOver;
        private bool _forceConnectionOverlay;

        // Event throttling for CardMoved
        private DispatcherTimer? _cardMovedThrottleTimer;
        private bool _needsCardMovedEvent;

        public BrowserCard()
        {
            InitializeComponent();
            Loaded += BrowserCard_Loaded;
            
            // Register fullscreen command
            CommandBindings.Add(new CommandBinding(FullscreenCommand, OnFullscreenCommand));
            
            // Setup throttle timer for CardMoved events (60 FPS)
            _cardMovedThrottleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
            };
            _cardMovedThrottleTimer.Tick += (s, e) =>
            {
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        private void OnFullscreenCommand(object sender, ExecutedRoutedEventArgs e)
        {
            FullscreenRequested?.Invoke(this, CardId);
        }

        private void BrowserCard_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= BrowserCard_Loaded; // Only run once
            InitializeWebView();
        }

        private void Card_MouseEnter(object sender, MouseEventArgs e)
        {
            _isPointerOver = true;
            UpdateConnectionOverlayVisibility();
        }

        private void Card_MouseLeave(object sender, MouseEventArgs e)
        {
            _isPointerOver = false;
            UpdateConnectionOverlayVisibility();
        }

        public void SetConnectionMode(bool isActive)
        {
            _forceConnectionOverlay = isActive;
            UpdateConnectionOverlayVisibility();
        }

        private void UpdateConnectionOverlayVisibility()
        {
            if (ConnectionOverlay == null)
                return;

            ConnectionOverlay.Visibility = (_forceConnectionOverlay || _isPointerOver)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void InitializeWebView()
        {
            if (_webViewInitialized) return;
            
            try
            {
                var env = await WebViewProfileEnvironment.GetAsync(BrowserProfileKey);
                await WebView.EnsureCoreWebView2Async(env);
                
                if (_webViewInitialized) return; // Double-check after await
                _webViewInitialized = true;
                
                // Handle navigation events
                WebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                WebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                WebView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

                TrackerBlocklist.AttachIfEnabled(WebView.CoreWebView2);
                
                // Navigate to pending URL if any
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    Navigate(_pendingUrl);
                    _pendingUrl = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 initialization failed: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task<byte[]?> CapturePreviewPngAsync()
        {
            try
            {
                var env = await WebViewProfileEnvironment.GetAsync(BrowserProfileKey);
                await WebView.EnsureCoreWebView2Async(env);
                if (WebView.CoreWebView2 == null) return null;
                using var ms = new MemoryStream();
                await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        public void SetFocusIndicator(bool focused)
        {
            if (focused)
            {
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD9, 0xFF));
                CardBorder.BorderThickness = new Thickness(2);
            }
            else
            {
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
                CardBorder.BorderThickness = new Thickness(1);
            }
        }

        /// <summary>
        /// Navigate to a URL
        /// </summary>
        public void Navigate(string url)
        {
            CurrentUrl = url;
            UrlTextBlock.Text = url;
            
            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(url);
            }
            else
            {
                // Queue for after initialization
                _pendingUrl = url;
            }
        }

        /// <summary>
        /// Navigate with delayed WebView2 initialization
        /// </summary>
        public void NavigateDelayed(string url)
        {
            _pendingUrl = url;
            CurrentUrl = url;
            UrlTextBlock.Text = url;
            
            // InitializeWebView will handle navigation when ready
            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(url);
            }
        }

        #region WebView2 Events

        private bool _isInitialNavigation = true;

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Skip link interception during initial page load
            if (_isInitialNavigation)
            {
                return;
            }
            
            // Only intercept user-initiated navigations that are actual link clicks
            // Not redirects (same domain) or form submissions
            if (e.IsUserInitiated && !string.IsNullOrEmpty(CurrentUrl))
            {
                // Check if it's a different page (not just a redirect on same domain)
                if (!IsSameDomainRedirect(CurrentUrl, e.Uri))
                {
                    // Cancel navigation and spawn new card
                    e.Cancel = true;
                    LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, e.Uri));
                }
            }
        }

        private bool IsSameDomainRedirect(string currentUrl, string newUrl)
        {
            try
            {
                var current = new Uri(currentUrl);
                var next = new Uri(newUrl);
                
                string currentHost = current.Host.Replace("www.", "");
                string nextHost = next.Host.Replace("www.", "");
                
                // Allow subdomains by checking if either ends with the other + a dot
                return currentHost.Equals(nextHost, StringComparison.OrdinalIgnoreCase) ||
                       currentHost.EndsWith("." + nextHost, StringComparison.OrdinalIgnoreCase) ||
                       nextHost.EndsWith("." + currentHost, StringComparison.OrdinalIgnoreCase) ||
                       // Special case: google.com vs news.google.com (ends with "google.com")
                       currentHost.Contains(GetBaseDomain(nextHost)) || 
                       nextHost.Contains(GetBaseDomain(currentHost));
            }
            catch
            {
                return false;
            }
        }

        private string GetBaseDomain(string host)
        {
            var parts = host.Split('.');
            if (parts.Length >= 2)
            {
                return parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            }
            return host;
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialNavigation = false; // First navigation complete
            
            if (e.IsSuccess && WebView.CoreWebView2 != null)
            {
                CurrentUrl = WebView.CoreWebView2.Source;
                UrlTextBlock.Text = CurrentUrl;
                NavigationCompleted?.Invoke(this, new HistoryEventArgs(CurrentTitle, CurrentUrl));
                CardInteracted?.Invoke(this, EventArgs.Empty);
                // Theme Injection Removed
            }
        }

        private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
        {
            if (WebView.CoreWebView2 != null)
            {
                CurrentTitle = WebView.CoreWebView2.DocumentTitle;
            }
        }

        private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            DownloadStarted?.Invoke(this, new DownloadEventArgs(
                e.DownloadOperation.Uri,
                e.ResultFilePath,
                (long)(e.DownloadOperation.TotalBytesToReceive ?? 0)
            ));
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Spawn new popups/tabs as separate semantic spatial cards (instead of internal WebView navigation)
            e.Handled = true;
            LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, e.Uri));
        }

        #endregion

        #region Title Bar Drag

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                 if (IsPortal && (!string.IsNullOrEmpty(PortalWorkspaceFullPath) || !string.IsNullOrEmpty(PortalTargetFile)))
                 {
                      string path = !string.IsNullOrEmpty(PortalWorkspaceFullPath) ? PortalWorkspaceFullPath : PortalTargetFile;
                      PortalOpened?.Invoke(this, path);
                 }
                 else 
                 {
                      FullscreenRequested?.Invoke(this, CardId);
                 }
                 return;
            }

            if (e.ClickCount == 1)
            {
                _isDragging = true;
                _dragStartPoint = e.GetPosition(Parent as Canvas);
                _dragStartLeft = Canvas.GetLeft(this);
                _dragStartTop = Canvas.GetTop(this);
                
                if (double.IsNaN(_dragStartLeft)) _dragStartLeft = 0;
                if (double.IsNaN(_dragStartTop)) _dragStartTop = 0;
                
                ((UIElement)sender).CaptureMouse();
                
                // Start throttle timer
                _cardMovedThrottleTimer?.Start();
                CardInteracted?.Invoke(this, EventArgs.Empty);
            }
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && Parent is Canvas canvas)
            {
                Point currentPoint = e.GetPosition(canvas);
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                double newLeft = Math.Max(0, _dragStartLeft + deltaX);
                double newTop = Math.Max(0, _dragStartTop + deltaY);

                Canvas.SetLeft(this, newLeft);
                Canvas.SetTop(this, newTop);
                
                // Set flag for throttled event
                _needsCardMovedEvent = true;
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ((UIElement)sender).ReleaseMouseCapture();
                
                // Stop throttle timer and fire final event
                _cardMovedThrottleTimer?.Stop();
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
                
                CardDropped?.Invoke(this, this);
            }
        }

        private void TitleBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Already handled via ClickCount == 2
        }

        #endregion

        #region Resize Handles

        private double _resizeStartLeft;
        private double _resizeStartTop;

        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _activeResizeHandle = sender as FrameworkElement;
            _resizeStartPoint = e.GetPosition(Parent as Canvas);
            _resizeStartWidth = ActualWidth;
            _resizeStartHeight = ActualHeight;
            _resizeStartLeft = Canvas.GetLeft(this);
            _resizeStartTop = Canvas.GetTop(this);
            if (double.IsNaN(_resizeStartLeft)) _resizeStartLeft = 0;
            if (double.IsNaN(_resizeStartTop)) _resizeStartTop = 0;
            ((UIElement)sender).CaptureMouse();
            
            // Start throttle timer
            _cardMovedThrottleTimer?.Start();
        }

        private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && Parent is Canvas canvas)
            {
                Point currentPoint = e.GetPosition(canvas);
                double deltaX = currentPoint.X - _resizeStartPoint.X;
                double deltaY = currentPoint.Y - _resizeStartPoint.Y;

                // Right edge resize (E, SE, NE)
                if (_activeResizeHandle == ResizeHandleE || 
                    _activeResizeHandle == ResizeHandleSE || 
                    _activeResizeHandle == ResizeHandleNE)
                {
                    Width = Math.Max(MinWidth, _resizeStartWidth + deltaX);
                }
                
                // Left edge resize (W, SW, NW) - also moves position
                if (_activeResizeHandle == ResizeHandleW || 
                    _activeResizeHandle == ResizeHandleSW || 
                    _activeResizeHandle == ResizeHandleNW)
                {
                    double newWidth = _resizeStartWidth - deltaX;
                    if (newWidth >= MinWidth)
                    {
                        Width = newWidth;
                        Canvas.SetLeft(this, _resizeStartLeft + deltaX);
                    }
                }
                
                // Bottom edge resize (S, SE, SW)
                if (_activeResizeHandle == ResizeHandleS || 
                    _activeResizeHandle == ResizeHandleSE || 
                    _activeResizeHandle == ResizeHandleSW)
                {
                    Height = Math.Max(MinHeight, _resizeStartHeight + deltaY);
                }
                
                // Top edge resize (N, NE, NW) - also moves position
                if (_activeResizeHandle == ResizeHandleN || 
                    _activeResizeHandle == ResizeHandleNE || 
                    _activeResizeHandle == ResizeHandleNW)
                {
                    double newHeight = _resizeStartHeight - deltaY;
                    if (newHeight >= MinHeight)
                    {
                        Height = newHeight;
                        Canvas.SetTop(this, _resizeStartTop + deltaY);
                    }
                }
                
                // Set flag for throttled event
                _needsCardMovedEvent = true;
            }
        }

        private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                ((UIElement)sender).ReleaseMouseCapture();
                
                // Stop throttle timer and fire final event
                _cardMovedThrottleTimer?.Stop();
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion

        #region Connection Points

        private void ConnectionPoint_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointPressed?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, true));
                e.Handled = true;
            }
        }

        private void ConnectionPoint_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointHoverChanged?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, true));
            }
        }

        private void ConnectionPoint_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointHoverChanged?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, false));
            }
        }

        public Point GetEdgeCenter(string edge)
        {
            double left = Canvas.GetLeft(this);
            double top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            // Account for the 12px margin around the visual card border
            const double margin = 12;
            double visualLeft = left + margin;
            double visualTop = top + margin;
            double visualWidth = ActualWidth - margin * 2;
            double visualHeight = ActualHeight - margin * 2;
            double centerX = visualLeft + visualWidth / 2;
            double centerY = visualTop + visualHeight / 2;

            return edge switch
            {
                "top" => new Point(centerX, visualTop),
                "bottom" => new Point(centerX, visualTop + visualHeight),
                "left" => new Point(visualLeft, centerY),
                "right" => new Point(visualLeft + visualWidth, centerY),
                _ => new Point(centerX, centerY)
            };
        }

        #endregion

        #region Button Handlers

        private void GhostButton_Click(object sender, RoutedEventArgs e)
        {
            IsGhostMode = !IsGhostMode;
            UpdateOpacity();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            IsPinned = !IsPinned;
            var kind = IsPinned ? MaterialDesignThemes.Wpf.PackIconKind.Pin : MaterialDesignThemes.Wpf.PackIconKind.PinOutline;
            PinIcon.Kind = kind;
            if (MenuPinIcon != null) MenuPinIcon.Kind = kind;
            if (MenuPinText != null) MenuPinText.Text = IsPinned ? "Unpin from Viewport" : "Pin to Viewport";
            PinToggled?.Invoke(this, this);
        }

        public async void UpdateSpatialAudio(double volume)
        {
            if (WebView.CoreWebView2 != null)
            {
                try
                {
                    string script = $"document.querySelectorAll('video, audio').forEach(m => m.volume = {volume.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
                    await WebView.CoreWebView2.ExecuteScriptAsync(script);
                }
                catch { }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoBack == true)
            {
                WebView.CoreWebView2.GoBack();
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoForward == true)
            {
                WebView.CoreWebView2.GoForward();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            WebView.CoreWebView2?.Reload();
        }

        private void BookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            BookmarkRequested?.Invoke(this, CurrentUrl);
        }

        /// <summary>
        /// Update the bookmark icon to show filled or outline star
        /// </summary>
        public void SetBookmarked(bool isBookmarked)
        {
            BookmarkIcon.Kind = isBookmarked 
                ? MaterialDesignThemes.Wpf.PackIconKind.Star 
                : MaterialDesignThemes.Wpf.PackIconKind.StarOutline;
        }

        // ── Feature #3: Tab Group Color ─────────────────────────
        public string? GroupId { get; set; }

        /// <summary>
        /// Tint the card's top accent strip with the group color.
        /// </summary>
        public void SetGroupColor(string? hexColor)
        {
            if (string.IsNullOrEmpty(hexColor))
            {
                GroupColorStrip.Background = System.Windows.Media.Brushes.Transparent;
                CardBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
                    GroupColorStrip.Background = new System.Windows.Media.SolidColorBrush(color);
                    // Also tint the card border to match, at low opacity
                    var borderColor = System.Windows.Media.Color.FromArgb(0x55, color.R, color.G, color.B);
                    CardBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(borderColor);
                }
                catch { }
            }
        }

        public void ClearGroupColor() => SetGroupColor(null);

        public void DisposeResources()
        {
            try { WebView?.Dispose(); } catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Dispose WebView2 resources
            DisposeResources();
            CloseRequested?.Invoke(this, CardId);
        }

        // Browser-style 3-Line Menu Handlers
        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the Popup open/closed
            BrowserMenuPopup.IsOpen = !BrowserMenuPopup.IsOpen;
            
            if (BrowserMenuPopup.IsOpen)
            {
                // Sync state
                MenuZoomLabel.Text = $"{(int)(_pageZoomLevel * 100)}%";
                var kind = IsPinned ? MaterialDesignThemes.Wpf.PackIconKind.Pin : MaterialDesignThemes.Wpf.PackIconKind.PinOutline;
                if (MenuPinIcon != null) MenuPinIcon.Kind = kind;
                if (MenuPinText != null) MenuPinText.Text = IsPinned ? "Unpin from Viewport" : "Pin to Viewport";

                // Feature #3: Dynamic Tab Groups
                PopulateGroupsMenu();
            }
        }

        private void PopulateGroupsMenu()
        {
            if (MenuGroupsList == null) return;
            MenuGroupsList.Children.Clear();

            var args = new GroupRequestEventArgs();
            GroupListRequested?.Invoke(this, args);

            if (args.Groups == null || args.Groups.Count == 0)
            {
                MenuGroupsList.Children.Add(new TextBlock 
                { 
                    Text = "No groups created yet.", 
                    Foreground = System.Windows.Media.Brushes.Gray, 
                    FontSize = 11, Margin = new Thickness(10, 0, 0, 0) 
                });
                return;
            }

            foreach (var group in args.Groups)
            {
                var btn = new Button
                {
                    Content = group.Name,
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 32, Padding = new Thickness(10, 0, 10, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(group.Color)),
                    FontSize = 12, Tag = group.Id
                };
                btn.Click += (s, e) => 
                {
                    string? id = (s as Button)?.Tag as string;
                    GroupAssigned?.Invoke(this, id);
                    SetGroupColor(group.Color);
                    GroupId = id;
                    CloseMenu();
                };
                MenuGroupsList.Children.Add(btn);
            }
        }

        private void MenuClearGroup_Click(object sender, RoutedEventArgs e)
        {
            GroupId = null;
            ClearGroupColor();
            GroupAssigned?.Invoke(this, null);
            CloseMenu();
        }

        private void CloseMenu() => BrowserMenuPopup.IsOpen = false;

        private void MenuNewTab_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            // Spawn a new blank card to the right of this one
            LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, "https://www.google.com"));
        }

        private void MenuNewWindow_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            // Spawn a new card with a blank start page
            LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, "about:blank"));
        }

        private void MenuHistory_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            // Surface the main window sidebar to "history" tab
            var mainWindow = System.Windows.Application.Current.MainWindow as Sowser.MainWindow;
            mainWindow?.ShowSidebar("history");
        }

        private void MenuBookmarks_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            var mainWindow = System.Windows.Application.Current.MainWindow as Sowser.MainWindow;
            mainWindow?.ShowSidebar("bookmarks");
        }

        private void MenuDownloads_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            var mainWindow = System.Windows.Application.Current.MainWindow as Sowser.MainWindow;
            mainWindow?.ShowSidebar("downloads");
        }

        private void MenuPrint_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            WebView?.CoreWebView2?.ExecuteScriptAsync("window.print()");
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            var mainWindow = System.Windows.Application.Current.MainWindow as Sowser.MainWindow;
            mainWindow?.ShowSidebar("settings");
        }

        // --- New Menu Actions ---

        private void MenuZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _pageZoomLevel = Math.Min(5.0, _pageZoomLevel + 0.25);
            ApplyPageZoom();
        }

        private void MenuZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _pageZoomLevel = Math.Max(0.25, _pageZoomLevel - 0.25);
            ApplyPageZoom();
        }

        private void MenuZoomFull_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            FullscreenRequested?.Invoke(this, CardId);
        }

        private void ApplyPageZoom()
        {
            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                WebView.ZoomFactor = _pageZoomLevel;
            }
            MenuZoomLabel.Text = $"{(int)(_pageZoomLevel * 100)}%";
        }

        private void MenuFindOnPage_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            // Trigger browser's native find bar via Ctrl+F
            WebView?.CoreWebView2?.ExecuteScriptAsync(
                "document.dispatchEvent(new KeyboardEvent('keydown', {key:'f', ctrlKey:true, bubbles:true}));");
        }

        private void MenuReadLater_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            ReadLaterRequested?.Invoke(this, EventArgs.Empty);
        }

        private void MenuCaptureToCanvas_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            CapturePreviewToCanvasRequested?.Invoke(this, EventArgs.Empty);
        }

        private void MenuSavePage_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            // Use WebView2's built-in save dialog
            try { WebView?.CoreWebView2?.ExecuteScriptAsync("window.print()"); } catch { }
        }

        #endregion

        #region Performance & Tabs

        private bool _isSleeping;

        private void UpdateOpacity()
        {
            double op = 1.0;
            if (IsGhostMode) op *= 0.4;
            if (_isSleeping) op *= 0.5;
            Opacity = op;
        }

        public async void SetSleepState(bool sleep)
        {
            if (_isSleeping == sleep || WebView.CoreWebView2 == null) return;
            
            _isSleeping = sleep;
            try 
            {
                if (sleep) 
                {
                    await WebView.CoreWebView2.TrySuspendAsync();
                    UpdateOpacity();
                } 
                else 
                {
                    WebView.CoreWebView2.Resume();
                    UpdateOpacity();
                }
            }
            catch { }
        }

        public class CardTab
        {
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
        }

        private List<CardTab> _tabs = new();

        public void AddTab(string title, string url)
        {
            if (_tabs.Count == 0 && !string.IsNullOrEmpty(CurrentUrl) && CurrentUrl != "about:blank")
            {
                // Add current as first tab
                _tabs.Add(new CardTab { Title = CurrentTitle, Url = CurrentUrl });
            }
            _tabs.Add(new CardTab { Title = title, Url = url });
            RefreshTabsUI();
        }

        private void RefreshTabsUI()
        {
            TabsPanel.Children.Clear();
            foreach (var tab in _tabs)
            {
                var btn = new Button 
                { 
                    Content = string.IsNullOrEmpty(tab.Title) ? tab.Url : tab.Title, 
                    Margin = new Thickness(0,0,4,0),
                    Padding = new Thickness(12,4,12,4),
                    FontSize = 11,
                    Height = 24,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    ToolTip = tab.Url
                };
                btn.Click += (s, e) => Navigate(tab.Url);
                TabsPanel.Children.Add(btn);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Process URL input - detect if URL or search query
        /// </summary>
        public static string ProcessUrlInput(string input, string searchEngine = "https://www.google.com/search?q=")
        {
            if (string.IsNullOrWhiteSpace(input))
                return "about:blank";

            // Check if it's already a valid URL
            if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) && 
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                return input;
            }

            // Check if it looks like a domain (contains dot, no spaces)
            if (input.Contains('.') && !input.Contains(' '))
            {
                return "https://" + input;
            }

            // Treat as search query
            return searchEngine + Uri.EscapeDataString(input);
        }

        /// <summary>
        /// Get center point of this card on the canvas
        /// </summary>
        public Point GetCenterPoint()
        {
            double left = Canvas.GetLeft(this);
            double top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            
            return new Point(left + ActualWidth / 2, top + ActualHeight / 2);
        }

        #endregion
    }

    #region Event Args

    public class ConnectionPointEventArgs : EventArgs
    {
        public string CardId { get; }
        public string Edge { get; }
        public bool IsHovered { get; }

        public ConnectionPointEventArgs(string cardId, string edge, bool isHovered)
        {
            CardId = cardId;
            Edge = edge;
            IsHovered = isHovered;
        }
    }

    public class LinkClickedEventArgs : EventArgs
    {
        public string SourceCardId { get; }
        public string Url { get; }

        public LinkClickedEventArgs(string sourceCardId, string url)
        {
            SourceCardId = sourceCardId;
            Url = url;
        }
    }

    public class HistoryEventArgs : EventArgs
    {
        public string Title { get; }
        public string Url { get; }

        public HistoryEventArgs(string title, string url)
        {
            Title = title;
            Url = url;
        }
    }

    public class DownloadEventArgs : EventArgs
    {
        public string Url { get; }
        public string FilePath { get; }
        public long TotalBytes { get; }

        public DownloadEventArgs(string url, string filePath, long totalBytes)
        {
            Url = url;
            FilePath = filePath;
            TotalBytes = totalBytes;
        }
    }

    public class GroupRequestEventArgs : EventArgs
    {
        public System.Collections.Generic.List<Sowser.Models.CardGroup>? Groups { get; set; }
    }

    #endregion
}
