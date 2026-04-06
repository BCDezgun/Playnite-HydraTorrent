using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK;

namespace Playnite_HydraTorrent.Views
{
    public partial class DownloadProgressView : UserControl
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly dynamic plugin;
        private readonly DispatcherTimer updateTimer;
        private readonly DispatcherTimer particleTimer;
        private readonly Random random = new Random();
        private readonly List<ParticleInfo> particles = new List<ParticleInfo>();
        private bool isDownloading = false;

        private class ParticleInfo
        {
            public Ellipse Element { get; set; }
            public double StartX { get; set; }
            public double StartY { get; set; }
            public double Vx { get; set; }
            public double Vy { get; set; }
            public double Life { get; set; }
            public double Decay { get; set; }
        }

        public DownloadProgressView(dynamic plugin)
        {
            InitializeComponent();
            this.plugin = plugin;

            DataContextChanged += DownloadProgressView_DataContextChanged;
            Loaded += DownloadProgressView_Loaded;

            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            updateTimer.Tick += UpdateTimer_Tick;
            updateTimer.Start();

            particleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            particleTimer.Tick += ParticleTimer_Tick;
            particleTimer.Start();
        }

        private void DownloadProgressView_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateVisibility();
            UpdateTimer_Tick(null, null);
        }

        private void DownloadProgressView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdateVisibility();
            UpdateTimer_Tick(null, null);
        }

        private void ParticleTimer_Tick(object sender, EventArgs e)
        {
            if (!isDownloading) return;

            DownloadBtn.ApplyTemplate();
            var progressBorder = DownloadBtn.Template.FindName("ProgressBorder", DownloadBtn) as Border;
            if (progressBorder == null) return;

            var progressWidth = progressBorder.ActualWidth;
            if (progressWidth < 48) return;

            var offset = DownloadBtn.TranslatePoint(new Point(0, 0), ParticlesCanvas);
            var btnLeft = offset.X;
            var btnTop = offset.Y;
            var btnHeight = DownloadBtn.ActualHeight;
            if (btnHeight <= 0) btnHeight = 48;

            var radius = btnHeight / 2.0;

            // Reduced spawn rate
            int count = random.Next(1, 3);
            for (int i = 0; i < count; i++)
            {
                int segment = random.Next(3);
                double px, py, nx, ny;
                var dist = random.NextDouble() * 12 + 8;

                if (segment == 0)
                {
                    var angle = Math.PI * 0.5 + random.NextDouble() * Math.PI;
                    px = btnLeft + radius + radius * Math.Cos(angle);
                    py = btnTop + radius + radius * Math.Sin(angle);
                    nx = Math.Cos(angle);
                    ny = Math.Sin(angle);
                }
                else if (segment == 1)
                {
                    var t = random.NextDouble();
                    px = btnLeft + radius + t * Math.Max(0, progressWidth - radius * 2);
                    py = btnTop + 1;
                    nx = 0;
                    ny = -1;
                }
                else
                {
                    var t = random.NextDouble();
                    px = btnLeft + radius + t * Math.Max(0, progressWidth - radius * 2);
                    py = btnTop + btnHeight - 1;
                    nx = 0;
                    ny = 1;
                }

                SpawnParticle(px, py, nx * dist, ny * dist);
            }

            UpdateParticles();
        }

        private void SpawnParticle(double x, double y, double vx, double vy)
        {
            // Radial gradient instead of BlurEffect (much faster)
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 60, 240, 60), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 60, 240, 60), 1.0));

            var particle = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = brush,
                RenderTransform = new ScaleTransform(1, 1)
            };

            ParticlesCanvas.Children.Add(particle);
            Canvas.SetLeft(particle, x - 5);
            Canvas.SetTop(particle, y - 5);

            particles.Add(new ParticleInfo
            {
                Element = particle,
                StartX = x,
                StartY = y,
                Vx = vx,
                Vy = vy,
                Life = 1.0,
                Decay = random.NextDouble() * 0.02 + 0.015
            });
        }

        private void UpdateParticles()
        {
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                var p = particles[i];
                p.Life -= p.Decay;

                if (p.Life <= 0)
                {
                    ParticlesCanvas.Children.Remove(p.Element);
                    particles.RemoveAt(i);
                    continue;
                }

                var eased = (1.0 - p.Life) * (1.0 - p.Life);

                // Use RenderTransform for position + scale (GPU-accelerated, no layout pass)
                var scale = 0.15 + 0.85 * p.Life;
                p.Element.RenderTransform = new ScaleTransform(scale, scale);
                Canvas.SetLeft(p.Element, p.StartX + p.Vx * eased - 5);
                Canvas.SetTop(p.Element, p.StartY + p.Vy * eased - 5);

                // Fade only (no color recalculation)
                if (p.Element.Fill is RadialGradientBrush brush)
                {
                    var alpha = (byte)(p.Life * 200);
                    brush.GradientStops[0].Color = Color.FromArgb(alpha, 60, 240, 60);
                }
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            var game = DataContext as Playnite.SDK.Models.Game;
            if (game == null)
            {
                UpdateVisibility(false);
                return;
            }

            System.Collections.IEnumerable queue = plugin.DownloadQueue;
            object queueItem = null;

            if (queue != null)
            {
                foreach (var item in queue)
                {
                    var gidProp = item.GetType().GetProperty("GameId");
                    if (gidProp != null)
                    {
                        var gid = gidProp.GetValue(item) as Guid?;
                        if (gid == game.Id)
                        {
                            queueItem = item;
                            break;
                        }
                    }
                }
            }

            if (queueItem == null)
            {
                UpdateVisibility(false);
                return;
            }

            var statusProp = queueItem.GetType().GetProperty("QueueStatus");
            var status = statusProp?.GetValue(queueItem) as string;
            if (status != "Downloading" && status != "Paused" && status != "Queued")
            {
                UpdateVisibility(false);
                return;
            }

            UpdateVisibility(true);

            double progress = 0;
            string text = "";
            string color = "#4CAF50";

            try
            {
                var liveStatusField = plugin.GetType().GetField("LiveStatus",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                
                if (liveStatusField != null)
                {
                    var liveStatus = liveStatusField.GetValue(null) as System.Collections.IDictionary;
                    
                    if (liveStatus != null && liveStatus.Contains(game.Id))
                    {
                        var torrentStatus = liveStatus[game.Id];
                        var progressProp = torrentStatus.GetType().GetProperty("Progress");
                        if (progressProp != null)
                        {
                            progress = Convert.ToDouble(progressProp.GetValue(torrentStatus)) * 100.0;
                        }

                        if (status == "Downloading")
                        {
                            var speedProp = torrentStatus.GetType().GetProperty("DownloadSpeed");
                            if (speedProp != null)
                            {
                                var speed = Convert.ToInt64(speedProp.GetValue(torrentStatus)) / 1024.0 / 1024.0;
                                text = string.Format(
                                    Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_DownloadProgressText"),
                                    progress.ToString("F1"), speed.ToString("F1"));
                            }
                            else
                            {
                                text = string.Format(
                                    Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_DownloadProgressNoSpeed"),
                                    progress.ToString("F1"));
                            }
                        }
                    }
                }
            }
            catch { }

            switch (status)
            {
                case "Downloading":
                    color = "#4CAF50";
                    if (string.IsNullOrEmpty(text))
                        text = string.Format(
                            Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_DownloadProgressNoSpeed"),
                            progress.ToString("F1"));
                    break;

                case "Paused":
                    color = "#FF9800";
                    text = string.Format(
                        Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_PausedProgressText"),
                        progress.ToString("F1"));
                    break;

                case "Queued":
                    color = "#9E9E9E";
                    var pos = queueItem.GetType().GetProperty("QueuePosition")?.GetValue(queueItem)?.ToString() ?? "?";
                    text = string.Format(
                        Playnite.SDK.ResourceProvider.GetString("LOC_HydraTorrent_QueuedProgressText"),
                        pos);
                    break;
            }

            isDownloading = status == "Downloading";
            if (!isDownloading)
            {
                foreach (var p in particles)
                    ParticlesCanvas.Children.Remove(p.Element);
                particles.Clear();
            }
            UpdateButton(progress, text, color);
        }

        private void UpdateButton(double progress, string text, string colorHex)
        {
            DownloadBtn.ApplyTemplate();

            var btnWidth = DownloadBtn.ActualWidth > 0 ? DownloadBtn.ActualWidth : 200;
            // Start at 48px (circle), grow to full width at 100%
            var fillWidth = 48 + (btnWidth - 48) * (progress / 100.0);

            if (DownloadBtn.Template.FindName("ProgressBorder", DownloadBtn) is Border progressBorder)
            {
                progressBorder.Width = fillWidth;
            }

            if (DownloadBtn.Template.FindName("ProgressBrush", DownloadBtn) is SolidColorBrush brush)
            {
                if (ColorConverter.ConvertFromString(colorHex) is Color color)
                    brush.Color = color;
            }

            if (DownloadBtn.Template.FindName("StatusText", DownloadBtn) is TextBlock statusText)
            {
                statusText.Text = text;
            }
        }

        private void UpdateVisibility(bool visible = false)
        {
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                isDownloading = false;
                foreach (var p in particles)
                    ParticlesCanvas.Children.Remove(p.Element);
                particles.Clear();
            }
        }

        private void DownloadBtn_Click(object sender, RoutedEventArgs e)
        {
            var game = DataContext as Playnite.SDK.Models.Game;
            if (game == null) return;

            try
            {
                System.Collections.IEnumerable queue = plugin.DownloadQueue;
                if (queue == null) return;

                foreach (var item in queue)
                {
                    var gidProp = item.GetType().GetProperty("GameId");
                    if (gidProp != null)
                    {
                        var gid = gidProp.GetValue(item) as Guid?;
                        if (gid == game.Id)
                        {
                            var statusProp = item.GetType().GetProperty("QueueStatus");
                            var status = statusProp?.GetValue(item) as string;

                            if (status == "Downloading")
                                plugin.PauseDownload(game.Id);
                            else if (status == "Paused")
                                plugin.ResumeDownload(game.Id);

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[DownloadProgressView] Click error");
            }
        }
    }
}
