using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sowser.Controls
{
    public partial class StickyNote : UserControl
    {
        public string NoteId { get; } = Guid.NewGuid().ToString();
        public event EventHandler<string>? CloseRequested;
        public event EventHandler? ContentChanged;
        
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        public new string Content
        {
            get => ContentBox.Text;
            set => ContentBox.Text = value;
        }

        public StickyNote()
        {
            InitializeComponent();
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
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                Canvas.SetLeft(this, Math.Max(0, _dragStartLeft + deltaX));
                Canvas.SetTop(this, Math.Max(0, _dragStartTop + deltaY));
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, NoteId);
        }

        private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
