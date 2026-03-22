using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Sowser.Controls
{
    public partial class ImageClipCard : UserControl
    {
        public string ClipId { get; } = Guid.NewGuid().ToString();
        public event EventHandler<string>? CloseRequested;

        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        public ImageClipCard()
        {
            InitializeComponent();
        }

        public void SetImage(byte[] pngBytes)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(pngBytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage.Source = bmp;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(Parent as Canvas);
            _dragStartLeft = Canvas.GetLeft(this);
            _dragStartTop = Canvas.GetTop(this);
            if (double.IsNaN(_dragStartLeft)) _dragStartLeft = 0;
            if (double.IsNaN(_dragStartTop)) _dragStartTop = 0;
            ((UIElement)sender).CaptureMouse();
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && Parent is Canvas canvas)
            {
                Point currentPoint = e.GetPosition(canvas);
                Canvas.SetLeft(this, Math.Max(0, _dragStartLeft + (currentPoint.X - _dragStartPoint.X)));
                Canvas.SetTop(this, Math.Max(0, _dragStartTop + (currentPoint.Y - _dragStartPoint.Y)));
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ((UIElement)sender).ReleaseMouseCapture();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, ClipId);
        }
    }
}
