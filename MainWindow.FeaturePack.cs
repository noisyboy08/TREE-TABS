using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Sowser.Controls;
using Sowser.Models;
using Sowser.Services;

namespace Sowser
{
    public partial class MainWindow
    {
        private readonly Stack<Action> _undoActions = new();
        private const int MaxUndo = 35;
        private readonly Dictionary<string, ImageClipCard> _imageClips = new();
        private bool _suppressUndoForClose;
        private bool _undoInProgress;
        private string? _focusedCardId;
        private bool _timeMachineSliderInternal;

        private void PushUndo(Action action)
        {
            _undoActions.Push(action);
            TrimUndoStack();
        }

        /// <summary>Stack.ToArray() is newest-first; keep newest <see cref="MaxUndo"/> entries.</summary>
        private void TrimUndoStack()
        {
            if (_undoActions.Count <= MaxUndo) return;
            var arr = _undoActions.ToArray();
            _undoActions.Clear();
            int n = Math.Min(MaxUndo, arr.Length);
            for (int i = n - 1; i >= 0; i--)
                _undoActions.Push(arr[i]);
        }

        private void UndoLast()
        {
            if (_undoActions.Count == 0)
            {
                ShowToast("Nothing to undo.");
                return;
            }
            try
            {
                _undoInProgress = true;
                var a = _undoActions.Pop();
                a();
            }
            finally
            {
                _undoInProgress = false;
            }
        }

        private static CardUndoSnapshot TakeCardSnapshot(BrowserCard card)
        {
            double lx = Canvas.GetLeft(card);
            double ty = Canvas.GetTop(card);
            if (double.IsNaN(lx)) lx = 0;
            if (double.IsNaN(ty)) ty = 0;
            return new CardUndoSnapshot
            {
                X = lx,
                Y = ty,
                Width = card.ActualWidth,
                Height = card.ActualHeight,
                Url = card.CurrentUrl,
                Title = card.CurrentTitle,
                GroupId = card.GroupId,
                BrowserProfile = card.BrowserProfileKey,
                IsPortal = card.IsPortal,
                PortalWorkspaceFullPath = card.PortalWorkspaceFullPath,
                PortalTargetFile = card.PortalTargetFile
            };
        }

        private void RestoreCardSnapshot(CardUndoSnapshot s)
        {
            var card = CreateCard(s.X, s.Y, string.IsNullOrEmpty(s.Url) ? null : s.Url, s.BrowserProfile);
            card.Width = s.Width > 0 ? s.Width : card.Width;
            card.Height = s.Height > 0 ? s.Height : card.Height;
            card.GroupId = s.GroupId;
            card.IsPortal = s.IsPortal;
            card.PortalWorkspaceFullPath = s.PortalWorkspaceFullPath;
            card.PortalTargetFile = s.PortalTargetFile;
            if (!string.IsNullOrEmpty(s.GroupId))
            {
                var g = _groups.FirstOrDefault(x => x.Id == s.GroupId);
                if (g != null) card.SetGroupColor(g.Color);
            }
            ShowToast("Restored closed card (undo).");
        }

        private void SafeCloseCardById(string cardId)
        {
            if (!_cards.ContainsKey(cardId)) return;
            _suppressUndoForClose = true;
            try { Card_CloseRequested(this, cardId); }
            finally { _suppressUndoForClose = false; }
        }

        private void OnCardInteracted(BrowserCard? card)
        {
            if (card == null) return;
            _focusedCardId = card.CardId;
            UpdateFocusRings();
        }

        private void UpdateFocusRings()
        {
            foreach (var c in _cards.Values)
                c.SetFocusIndicator(c.CardId == _focusedCardId);
        }

        private void CycleCardFocus(int delta)
        {
            if (_cards.Count == 0) return;
            var list = _cards.Values.ToList();
            int idx = string.IsNullOrEmpty(_focusedCardId) ? 0 : list.FindIndex(c => c.CardId == _focusedCardId);
            if (idx < 0) idx = 0;
            idx = (idx + delta + list.Count) % list.Count;
            _focusedCardId = list[idx].CardId;
            PanToCard(list[idx]);
            UpdateFocusRings();
        }

        private void AddCurrentToReadLater(BrowserCard? card)
        {
            if (card == null || string.IsNullOrEmpty(card.CurrentUrl)) return;
            _settings.ReadLater.RemoveAll(r => r.Url == card.CurrentUrl);
            _settings.ReadLater.Insert(0, new ReadLaterItem
            {
                Title = string.IsNullOrEmpty(card.CurrentTitle) ? card.CurrentUrl : card.CurrentTitle,
                Url = card.CurrentUrl
            });
            AppSettingsStore.Save(_settings);
            RefreshReadLaterList();
            ShowToast("Saved to read later.");
        }

        private async void CaptureCardToCanvasAsync(BrowserCard? card)
        {
            if (card == null) return;
            byte[]? png = await card.CapturePreviewPngAsync();
            if (png == null || png.Length == 0)
            {
                ShowToast("Screenshot failed.");
                return;
            }
            Point p = GetViewportCenter();
            var clip = new ImageClipCard();
            clip.SetImage(png);
            Canvas.SetLeft(clip, p.X - 180);
            Canvas.SetTop(clip, p.Y - 130);
            clip.CloseRequested += (s, id) =>
            {
                if (s is ImageClipCard ic)
                {
                    CardsCanvas.Children.Remove(ic);
                    _imageClips.Remove(ic.ClipId);
                }
            };
            CardsCanvas.Children.Add(clip);
            _imageClips[clip.ClipId] = clip;
            UpdateCanvasSize();
            ShowToast("Screenshot placed on canvas.");
        }

        private void RefreshReadLaterList()
        {
            if (ReadLaterList == null) return;
            ReadLaterList.Children.Clear();
            foreach (var item in _settings.ReadLater)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var openBtn = new Button
                {
                    Content = $"{item.Title}\n{item.Url}",
                    Style = (Style)FindResource("SidebarButtonStyle"),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                var url = item.Url;
                openBtn.Click += (s, e) =>
                {
                    Point c = GetViewportCenter();
                    CreateCard(c.X - 400, c.Y - 300, url);
                };
                Grid.SetColumn(openBtn, 0);
                var rm = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("MaterialDesignFlatButton"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                    Width = 28
                };
                var captured = item;
                rm.Click += (s, e) =>
                {
                    _settings.ReadLater.RemoveAll(x => x.Url == captured.Url);
                    AppSettingsStore.Save(_settings);
                    RefreshReadLaterList();
                };
                Grid.SetColumn(rm, 1);
                row.Children.Add(openBtn);
                row.Children.Add(rm);
                ReadLaterList.Children.Add(row);
            }
        }

        private void ToggleReadLater_Click(object sender, RoutedEventArgs e) => ShowSidebar("readlater");

        private void MenuMain_ReadLater_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            ShowSidebar("readlater");
        }

        private void MenuMain_GlobalFind_Click(object sender, RoutedEventArgs e)
        {
            CloseMainMenu();
            OpenGlobalFind();
        }

        private void OpenGlobalFind()
        {
            GlobalFindOverlay.Visibility = Visibility.Visible;
            GlobalFindInput.Text = "";
            GlobalFindInput.Focus();
            RunGlobalFindFilter("");
        }

        private void CloseGlobalFind()
        {
            GlobalFindOverlay.Visibility = Visibility.Collapsed;
        }

        private void GlobalFindBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseGlobalFind();

        private void GlobalFindInner_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void GlobalFindInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            RunGlobalFindFilter(GlobalFindInput.Text);
        }

        private void GlobalFindInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseGlobalFind(); e.Handled = true; return; }
            if (e.Key == Key.Enter && GlobalFindList.SelectedItem is ListBoxItem lbi && lbi.Tag is string id && _cards.TryGetValue(id, out var c))
            {
                PanToCard(c);
                CloseGlobalFind();
                e.Handled = true;
            }
        }

        private void GlobalFindList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GlobalFindList.SelectedItem is ListBoxItem lbi && lbi.Tag is string id && _cards.TryGetValue(id, out var c))
            {
                PanToCard(c);
                CloseGlobalFind();
            }
        }

        private void RunGlobalFindFilter(string q)
        {
            GlobalFindList.Items.Clear();
            string query = q.Trim().ToLowerInvariant();
            foreach (var card in _cards.Values)
            {
                string t = (card.CurrentTitle ?? "").ToLowerInvariant();
                string u = (card.CurrentUrl ?? "").ToLowerInvariant();
                if (string.IsNullOrEmpty(query) || t.Contains(query) || u.Contains(query))
                {
                    var item = new ListBoxItem
                    {
                        Content = $"{card.CurrentTitle}\n{card.CurrentUrl}",
                        Tag = card.CardId,
                        Foreground = new SolidColorBrush(Colors.White),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    GlobalFindList.Items.Add(item);
                }
            }
        }

        private void TryRestoreLastSession()
        {
            try
            {
                string autoSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sowser", "autosave.json");
                if (!File.Exists(autoSavePath)) return;

                var mode = _settings.SessionRestoreMode ?? "Prompt";
                if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return;

                var fi = new FileInfo(autoSavePath);
                if ((DateTime.Now - fi.LastWriteTime).TotalDays > 7)
                {
                    ShowToast("Last session was too old — skipped restore.");
                    return;
                }

                if (string.Equals(mode, "Silent", StringComparison.OrdinalIgnoreCase))
                {
                    LoadWorkspaceFromFile(autoSavePath, showSuccessDialog: false, showErrorDialog: false);
                    ShowToast("Session restored (silent).");
                    return;
                }

                var result = MessageBox.Show("Restore your last session?", "Sowser", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                    LoadWorkspaceFromFile(autoSavePath, showSuccessDialog: false, showErrorDialog: true);
            }
            catch { /* ignore */ }
        }

        private WorkspaceState CloneWorkspaceStateForTimeMachine()
        {
            string json = JsonSerializer.Serialize(BuildWorkspaceState(), new JsonSerializerOptions { WriteIndented = false });
            return JsonSerializer.Deserialize<WorkspaceState>(json) ?? new WorkspaceState();
        }

        private void PushTimeMachineSnapshot()
        {
            if (!_settings.TimeMachineSnapshotsEnabled) return;
            try
            {
                _timeMachineHistory.Add(CloneWorkspaceStateForTimeMachine());
                while (_timeMachineHistory.Count > 30) _timeMachineHistory.RemoveAt(0);
                Dispatcher.BeginInvoke(new Action(RefreshTimeMachineUi), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { /* ignore */ }
        }

        private void RefreshTimeMachineUi()
        {
            if (TimeMachineSlider == null || TimeMachineLabel == null) return;
            int n = _timeMachineHistory.Count;
            _timeMachineSliderInternal = true;
            TimeMachineSlider.Maximum = Math.Max(0, n - 1);
            TimeMachineSlider.Value = n > 0 ? n - 1 : 0;
            _timeMachineSliderInternal = false;
            TimeMachineLabel.Text = n == 0 ? "No snapshots yet" : $"Snapshot {(int)TimeMachineSlider.Value + 1} of {n}";
        }

        private void TimeMachineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_timeMachineSliderInternal || TimeMachineLabel == null) return;
            int n = _timeMachineHistory.Count;
            if (n == 0) { TimeMachineLabel.Text = "No snapshots yet"; return; }
            int idx = (int)Math.Round(TimeMachineSlider.Value);
            idx = Math.Clamp(idx, 0, n - 1);
            TimeMachineLabel.Text = $"Snapshot {idx + 1} of {n} (click Restore to apply)";
        }

        private void RestoreTimeMachineSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_timeMachineHistory.Count == 0) { ShowToast("No snapshots."); return; }
            int idx = (int)Math.Round(TimeMachineSlider.Value);
            idx = Math.Clamp(idx, 0, _timeMachineHistory.Count - 1);
            var st = _timeMachineHistory[idx];
            ApplyWorkspaceState(st, showSuccessDialog: false, showErrorDialog: true);
            ShowToast("Time Machine snapshot applied.");
        }

        private void ApplyLoadedIntegrationSettings()
        {
            AppServices.BlockTrackers = _settings.BlockTrackers;
            if (BlockTrackersToggle != null) BlockTrackersToggle.IsChecked = _settings.BlockTrackers;
            if (SuspendOffscreenToggle != null) SuspendOffscreenToggle.IsChecked = _settings.SuspendOffscreenCards;
            if (TimeMachineToggle != null) TimeMachineToggle.IsChecked = _settings.TimeMachineSnapshotsEnabled;

            string mode = _settings.SessionRestoreMode ?? "Prompt";
            int si = mode.Equals("Silent", StringComparison.OrdinalIgnoreCase) ? 1 : mode.Equals("Off", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            if (SessionRestoreCombo != null && SessionRestoreCombo.Items.Count > si)
                SessionRestoreCombo.SelectedIndex = si;

            string prof = _settings.DefaultBrowserProfile ?? "default";
            int pi = prof == "work" ? 1 : prof == "personal" ? 2 : 0;
            if (DefaultProfileCombo != null && DefaultProfileCombo.Items.Count > pi)
                DefaultProfileCombo.SelectedIndex = pi;

            RefreshTimeMachineUi();
        }

        private void SessionRestoreCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || SessionRestoreCombo?.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string tag) return;
            _settings.SessionRestoreMode = tag;
            AppSettingsStore.Save(_settings);
        }

        private void DefaultProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || DefaultProfileCombo?.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string tag) return;
            _settings.DefaultBrowserProfile = tag;
            AppSettingsStore.Save(_settings);
        }

        private void IntegrationToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            _settings.BlockTrackers = BlockTrackersToggle?.IsChecked == true;
            _settings.SuspendOffscreenCards = SuspendOffscreenToggle?.IsChecked == true;
            _settings.TimeMachineSnapshotsEnabled = TimeMachineToggle?.IsChecked == true;
            AppServices.BlockTrackers = _settings.BlockTrackers;
            AppSettingsStore.Save(_settings);
        }

        private void ImportBookmarksHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "HTML bookmarks|*.html;*.htm|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var imported = BookmarkHtmlIO.ImportNetscapeHtml(dlg.FileName);
                foreach (var b in imported)
                {
                    if (!_bookmarks.Any(x => x.Url == b.Url)) _bookmarks.Add(b);
                }
                RefreshBookmarksList();
                ShowToast($"Imported {imported.Count} bookmarks.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Import failed"); }
        }

        private void ExportBookmarksHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "HTML|*.html", FileName = "sowser-bookmarks.html" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                BookmarkHtmlIO.ExportNetscapeHtml(dlg.FileName, _bookmarks);
                ShowToast("Bookmarks exported.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed"); }
        }

        private void ExportShareLayout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "sowser-share.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                SaveWorkspaceToFile(dlg.FileName);
                ShowToast("Shareable layout exported.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed"); }
        }

        private void CreatePortalCard_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Sowser / JSON workspace|*.sowser;*.json|All|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            Point c = GetViewportCenter();
            var card = CreateCard(c.X - 350, c.Y - 250, "about:blank", _settings.DefaultBrowserProfile);
            card.IsPortal = true;
            card.PortalWorkspaceFullPath = dlg.FileName;
            card.PortalTargetFile = Path.GetFileNameWithoutExtension(dlg.FileName);
            ShowToast("Portal card: double-click title to open workspace.");
        }

        private void AddCustomQuickLink_Click(object sender, RoutedEventArgs e)
        {
            string title = QuickLinkTitleInput?.Text?.Trim() ?? "";
            string url = QuickLinkUrlInput?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(url)) { ShowToast("Enter a URL."); return; }
            if (string.IsNullOrEmpty(title)) title = url;
            _settings.CustomQuickLinks.Add(new QuickLinkItem { Title = title, Url = url });
            QuickLinkTitleInput!.Text = "";
            QuickLinkUrlInput!.Text = "";
            AppSettingsStore.Save(_settings);
            RefreshBookmarksList();
            ShowToast("Quick link added.");
        }
    }
}
