using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Sowser.Helpers
{
    /// <summary>
    /// Reusable animation helpers for modern UI effects
    /// </summary>
    public static class AnimationHelpers
    {
        // Easing functions
        public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
        public static readonly IEasingFunction Bounce = new BounceEase { Bounces = 2, Bounciness = 2 };

        /// <summary>
        /// Animate card spawn (scale from 0 to 1)
        /// </summary>
        public static void AnimateCardSpawn(FrameworkElement element, double durationMs = 300)
        {
            var scaleTransform = new ScaleTransform(0, 0);
            element.RenderTransform = scaleTransform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);

            var scaleXAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut
            };
            var scaleYAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut
            };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        }

        /// <summary>
        /// Animate card lift on drag start
        /// </summary>
        public static void AnimateCardLift(FrameworkElement element, bool lift)
        {
            var translateY = lift ? -4 : 0;
            var scale = lift ? 1.02 : 1.0;

            var transform = element.RenderTransform as TransformGroup;
            if (transform == null)
            {
                transform = new TransformGroup();
                transform.Children.Add(new ScaleTransform(1, 1));
                transform.Children.Add(new TranslateTransform(0, 0));
                element.RenderTransform = transform;
                element.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            var scaleT = transform.Children[0] as ScaleTransform;
            var translateT = transform.Children[1] as TranslateTransform;

            if (scaleT != null)
            {
                scaleT.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(scale, TimeSpan.FromMilliseconds(150)) { EasingFunction = EaseOut });
                scaleT.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(scale, TimeSpan.FromMilliseconds(150)) { EasingFunction = EaseOut });
            }

            if (translateT != null)
            {
                translateT.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(translateY, TimeSpan.FromMilliseconds(150)) { EasingFunction = EaseOut });
            }
        }

        /// <summary>
        /// Animate connection line drawing
        /// </summary>
        public static void AnimateLineDrawing(Shape line, double durationMs = 400)
        {
            if (line is Path path && path.Data is PathGeometry geometry)
            {
                double length = GetPathLength(geometry);
                line.StrokeDashArray = new DoubleCollection { length, length };
                line.StrokeDashOffset = length;

                var animation = new DoubleAnimation(length, 0, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = EaseOut
                };
                line.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
            }
        }

        /// <summary>
        /// Smooth zoom animation
        /// </summary>
        public static void AnimateZoom(ScaleTransform transform, double targetScale, double durationMs = 200)
        {
            var animX = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut
            };
            var animY = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut
            };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }

        /// <summary>
        /// Fade in animation
        /// </summary>
        public static void FadeIn(UIElement element, double durationMs = 200)
        {
            element.Opacity = 0;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private static double GetPathLength(PathGeometry geometry)
        {
            // Approximate path length
            geometry.GetPointAtFractionLength(0, out _, out _);
            return 1000; // Default estimate
        }
    }
}
