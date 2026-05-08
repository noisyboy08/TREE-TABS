using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sowser.Controls;
using Sowser.Models;

namespace Sowser.Services
{
    public class GemmaOrganizeService
    {
        private readonly GemmaService _gemmaService;

        public GemmaOrganizeService(GemmaService gemmaService)
        {
            _gemmaService = gemmaService;
        }

        public void OrganizeCanvas(
            ObservableCollection<BrowserCardModel> cards,
            List<CardGroup> groups)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var matchedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                double startX = 40;

                foreach (var group in groups)
                {
                    var groupUrls = new HashSet<string>(group.Urls, StringComparer.OrdinalIgnoreCase);
                    var groupCards = cards.Where(c => groupUrls.Contains(c.Url)).ToList();
                    double x = startX;
                    double y = 40;
                    double maxWidth = 700;

                    foreach (var card in groupCards)
                    {
                        double cardWidth = card.Width > 0 ? card.Width : 700;
                        double cardHeight = card.Height > 0 ? card.Height : 500;
                        maxWidth = Math.Max(maxWidth, cardWidth);
                        card.GroupName = group.GroupName;
                        card.GroupColor = TryCreateBrush(group.Color);
                        card.X = x;
                        card.Y = y;
                        y += cardHeight + 40;
                        matchedUrls.Add(card.Url);
                    }

                    startX += maxWidth + 60;
                }

                foreach (var card in cards.Where(c => !matchedUrls.Contains(c.Url)))
                {
                    card.GroupName = null;
                    card.GroupColor = null;
                }
            });
        }

        public void OrganizeCanvas(IEnumerable<BrowserCard> cards, List<CardGroup> groups)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var cardList = cards.ToList();
                var matchedCards = new HashSet<BrowserCard>();
                double startX = 40;

                foreach (var group in groups)
                {
                    var groupUrls = new HashSet<string>(group.Urls, StringComparer.OrdinalIgnoreCase);
                    var groupCards = cardList.Where(c => groupUrls.Contains(c.CurrentUrl)).ToList();
                    double x = startX;
                    double y = 40;
                    double maxWidth = 700;

                    foreach (var card in groupCards)
                    {
                        double cardWidth = card.ActualWidth > 0 ? card.ActualWidth : card.Width;
                        double cardHeight = card.ActualHeight > 0 ? card.ActualHeight : card.Height;
                        if (double.IsNaN(cardWidth) || cardWidth <= 0) cardWidth = 700;
                        if (double.IsNaN(cardHeight) || cardHeight <= 0) cardHeight = 500;

                        maxWidth = Math.Max(maxWidth, cardWidth);
                        card.GroupId = group.Id;
                        card.SetGroupColor(group.Color);
                        Canvas.SetLeft(card, x);
                        Canvas.SetTop(card, y);
                        y += cardHeight + 40;
                        matchedCards.Add(card);
                    }

                    startX += maxWidth + 60;
                }

                foreach (var card in cardList.Where(c => !matchedCards.Contains(c)))
                {
                    card.GroupId = null;
                    card.ClearGroupColor();
                }
            });
        }

        private static SolidColorBrush? TryCreateBrush(string hexColor)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
            }
            catch
            {
                return null;
            }
        }
    }
}
