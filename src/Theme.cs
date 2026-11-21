using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RogueCoreNexus
{
    internal static class Theme
    {
        internal const string Title = "Rogue Core Nexus Manager";
        internal static readonly Color Background = Color.FromArgb(13, 15, 18);
        internal static readonly Color Surface = Color.FromArgb(22, 25, 29);
        internal static readonly Color Field = Color.FromArgb(31, 35, 40);
        internal static readonly Color Line = Color.FromArgb(52, 58, 64);
        internal static readonly Color Text = Color.FromArgb(238, 232, 220);
        internal static readonly Color Dim = Color.FromArgb(152, 154, 150);
        internal static readonly Color Accent = Color.FromArgb(255, 166, 36);
        internal static readonly Color AccentHot = Color.FromArgb(255, 198, 96);
        internal static readonly Color AccentDeep = Color.FromArgb(92, 60, 18);
        internal static readonly Color AccentText = Color.FromArgb(214, 160, 76);
        internal static readonly Color Crystal = Color.FromArgb(84, 210, 234);
        internal static readonly Color Selection = Color.FromArgb(54, 41, 21);
        internal static readonly Color Error = Color.FromArgb(255, 118, 96);

        internal static readonly Image Banner = Resource("banner.jpg");
        internal static readonly Image Emblem = Resource("emblem.png");
        internal static readonly Image Crystals = Resource("crystals.png");
        internal static readonly Icon AppIcon = ResourceIcon("app.ico");

        private static readonly string DisplayFamily = FindFamily("Bahnschrift SemiBold Condensed", "Bahnschrift Condensed",
            "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI");
        private static readonly string IconFamily = FindFamily("Segoe Fluent Icons", "Segoe MDL2 Assets");
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        internal static void Style(Form form)
        {
            form.Font = new Font("Microsoft YaHei UI", 10f);
            form.AutoScaleMode = AutoScaleMode.Font;
            form.BackColor = Background;
            form.ForeColor = Text;
            if (AppIcon != null) form.Icon = AppIcon;
            form.HandleCreated += (sender, e) => DarkFrame(form.Handle);
        }

        internal static Button Button(string text, bool primary = false, int width = 112, string glyph = null)
        {
            return new HudButton(primary, glyph) { Text = text, Width = width, Height = 36, Margin = new Padding(4, 0, 0, 0) };
        }

        internal static Label Label(string text, bool muted = false)
        {
            return new Label {
                Text = text, AutoSize = true, ForeColor = muted ? Dim : Text, UseMnemonic = false,
                Margin = new Padding(0, 4, 0, 4)
            };
        }

        internal static TextBox TextBox()
        {
            return new TextBox { BackColor = Field, ForeColor = Text, BorderStyle = BorderStyle.None };
        }

        internal static Control Framed(TextBox box, string tag = null)
        {
            return new FieldFrame(box, tag);
        }

        internal static void Cue(TextBox box, string hint)
        {
            box.HandleCreated += (sender, e) => SendMessage(box.Handle, 0x1501, new IntPtr(1), hint);
            if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, new IntPtr(1), hint);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static FlowLayoutPanel ButtonRow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        }

        internal static Font Display(float size) { return Cached(DisplayFamily, size, FontStyle.Regular); }

        internal static Font Strong(float size) { return Cached("Microsoft YaHei UI", size, FontStyle.Bold); }

        internal static Font Icons(float size) { return IconFamily == null ? null : Cached(IconFamily, size, FontStyle.Regular); }

        internal static float Scale(Graphics g) { return g.DpiX / 96f; }

        // Rectangle with the top-left and bottom-right corners cut off.
        internal static GraphicsPath Chamfer(RectangleF r, float cut)
        {
            var path = new GraphicsPath();
            path.AddPolygon(new[] {
                new PointF(r.Left + cut, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom - cut),
                new PointF(r.Right - cut, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Top + cut) });
            return path;
        }

        internal static void Hazard(Graphics g, Rectangle r, float offset, Color stripe, Color gap)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            float k = Scale(g), width = 7 * k, period = 14 * k;
            GraphicsState saved = g.Save();
            g.SetClip(r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(gap)) g.FillRectangle(back, r);
            using (var brush = new SolidBrush(stripe))
            {
                for (float x = r.Left - r.Height - period + offset % period; x < r.Right + period; x += period)
                    g.FillPolygon(brush, new[] { new PointF(x, r.Bottom), new PointF(x + r.Height, r.Top),
                        new PointF(x + r.Height + width, r.Top), new PointF(x + width, r.Bottom) });
            }
            g.Restore(saved);
        }

        internal static void DrawImage(Graphics g, Image image, RectangleF target, float opacity = 1f)
        {
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                if (opacity < 1f) attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, new[] { target.Location, new PointF(target.Right, target.Top), new PointF(target.Left, target.Bottom) },
                    new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
            }
        }

        private static Font Cached(string family, float size, FontStyle style)
        {
            string key = family + "|" + size + "|" + style;
            Font font;
            if (!fonts.TryGetValue(key, out font)) fonts[key] = font = new Font(family ?? "Microsoft YaHei UI", size, style);
            return font;
        }

        private static string FindFamily(params string[] names)
        {
            try
            {
                using (var installed = new InstalledFontCollection())
                {
                    var available = new HashSet<string>(installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    return names.FirstOrDefault(available.Contains);
                }
            }
            catch (ArgumentException) { return null; }
        }

        private static Image Resource(string name)
        {
            using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("RogueCoreNexus." + name))
            {
                if (stream == null) return null;
                using (Image image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }

        private static Icon ResourceIcon(string name)
        {
            using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("RogueCoreNexus." + name))
                return stream == null ? null : new Icon(stream);
        }

        // Dark caption and an amber window border on Windows 10 20H1 and later; older systems ignore the attributes.
        private static void DarkFrame(IntPtr handle)
        {
            try
            {
                int dark = 1, caption = ColorRef(Background), border = ColorRef(AccentDeep), text = ColorRef(Text);
                DwmSetWindowAttribute(handle, 20, ref dark, 4);
                DwmSetWindowAttribute(handle, 34, ref border, 4);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 36, ref text, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static int ColorRef(Color color) { return color.R | color.G << 8 | color.B << 16; }
    }

    // Segoe MDL2 Assets / Segoe Fluent Icons code points.
    internal static class Glyph
    {
        internal const string Settings = "\uE713";
        internal const string Sync = "\uE895";
        internal const string Play = "\uE768";
        internal const string Download = "\uE896";
        internal const string Folder = "\uE838";
        internal const string Add = "\uE710";
        internal const string Copy = "\uE8C8";
        internal const string Link = "\uE71B";
        internal const string Switch = "\uE8AB";
        internal const string Puzzle = "\uEA86";
        internal const string Delete = "\uE74D";
        internal const string Cancel = "\uE711";
        internal const string Accept = "\uE8FB";
        internal const string Search = "\uE721";
        internal const string Globe = "\uE774";
    }

    internal sealed class HudButton : Button
    {
        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter;
        private readonly bool primary;
        private readonly string glyph;
        private bool hover;
        private bool pressed;

        internal HudButton(bool primary, string glyph)
        {
            this.primary = primary;
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = primary ? Theme.Background : Theme.Text;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            bool focus = Focused && ShowFocusCues;
            Color fill, edge, ink;
            if (!Enabled)
            {
                fill = primary ? Color.FromArgb(66, 52, 32) : Color.FromArgb(25, 28, 32);
                edge = primary ? fill : Color.FromArgb(40, 45, 50);
                ink = primary ? Color.FromArgb(150, 126, 92) : Color.FromArgb(98, 101, 100);
            }
            else if (primary)
            {
                fill = pressed ? Color.FromArgb(222, 136, 20) : hover ? Theme.AccentHot : Theme.Accent;
                edge = focus ? Theme.Text : fill;
                ink = Theme.Background;
            }
            else
            {
                fill = pressed ? Theme.AccentDeep : hover ? Color.FromArgb(46, 42, 36) : Theme.Field;
                edge = hover || focus ? Theme.Accent : Theme.Line;
                ink = Theme.Text;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cut = 7 * k;
            using (GraphicsPath shape = Theme.Chamfer(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), cut))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(edge, 1))
            {
                g.FillPath(brush, shape);
                g.DrawPath(pen, shape);
            }
            if (Enabled && !primary && (hover || focus))
            {
                using (var tick = new SolidBrush(Theme.Accent))
                    g.FillPolygon(tick, new[] { new PointF(Width - 1, Height - 1 - cut), new PointF(Width - 1, Height - 1),
                        new PointF(Width - 1 - cut, Height - 1) });
            }
            g.SmoothingMode = SmoothingMode.Default;

            Font font = primary ? Theme.Strong(Font.SizeInPoints) : Font;
            Font icons = glyph == null ? null : Theme.Icons(Font.SizeInPoints);
            Size text = TextRenderer.MeasureText(g, Text, font, Size.Empty, Flags);
            int iconWidth = icons == null ? 0 : (int)(16 * k), gap = (int)(7 * k);
            if (text.Width + iconWidth + gap > Width - 16 * k) icons = null;
            int total = text.Width + (icons == null ? 0 : iconWidth + gap);
            int x = (Width - total) / 2;
            if (icons != null)
            {
                TextRenderer.DrawText(g, glyph, icons, new Rectangle(x, 0, iconWidth, Height - 1), ink, Flags | TextFormatFlags.HorizontalCenter);
                x += iconWidth + gap;
            }
            TextRenderer.DrawText(g, Text, font, new Rectangle(x, 0, text.Width + 2, Height - 1), ink, Flags);
        }
    }

    // Surface panel with cut corners and amber corner brackets.
    internal sealed class HudPanel : Panel
    {
        internal Image Art;

        internal HudPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Padding = new Padding(10);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g), cut = 10 * k, arm = 16 * k;
            using (var back = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Background)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = Theme.Chamfer(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), cut))
            {
                using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, shape);
                if (Art != null) PaintArt(g, k);
                using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, shape);
            }
            using (var pen = new Pen(Theme.Accent, 2 * k))
            {
                float r = Width - k, b = Height - k;
                g.DrawLines(pen, new[] { new PointF(r - arm, k), new PointF(r, k), new PointF(r, arm) });
                g.DrawLines(pen, new[] { new PointF(k, b - arm), new PointF(k, b), new PointF(arm, b) });
                g.DrawLine(pen, 3 * k, cut + 3 * k, cut + 3 * k, 3 * k);
            }
        }

        // Fills the free space under the content with the artwork, anchored to the bottom edge.
        private void PaintArt(Graphics g, float k)
        {
            int top = Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom).DefaultIfEmpty(0).Max();
            float width = Math.Min(Width - 2, Art.Width / 2f * k), height = width * Art.Height / Art.Width;
            float room = Height - 2 - top;
            if (height > room)
            {
                height = room;
                width = height * Art.Width / Art.Height;
            }
            if (height < 60 * k) return;
            Theme.DrawImage(g, Art, new RectangleF((Width - width) / 2, Height - 1 - height, width, height), 0.9f);
        }
    }

    // Borderless text box inside an outlined field with an optional amber tag on the left.
    internal sealed class FieldFrame : Panel
    {
        private readonly TextBox box;
        private readonly string tag;

        internal FieldFrame(TextBox box, string tag)
        {
            this.box = box;
            this.tag = tag;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Field;
            Height = 36;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            Cursor = Cursors.IBeam;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.Field;
            box.Dock = DockStyle.None;
            box.GotFocus += (sender, e) => Invalidate();
            box.LostFocus += (sender, e) => Invalidate();
            Controls.Add(box);
        }

        private int TagWidth
        {
            get { return tag == null ? 0 : TextRenderer.MeasureText(tag + " ›", Theme.Display(10.5f)).Width + 4; }
        }

        protected override void OnMouseDown(MouseEventArgs e) { box.Focus(); base.OnMouseDown(e); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int pad = box.PreferredHeight / 2, left = pad + TagWidth;
            box.SetBounds(left, (ClientSize.Height - box.Height) / 2, Math.Max(0, ClientSize.Width - left - pad), box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            bool focused = box.Focused;
            using (var pen = new Pen(focused ? Theme.Accent : Theme.Line)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (focused)
                using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, 0, 0, 3 * k, Height);
            if (tag != null)
                TextRenderer.DrawText(g, tag + " ›", Theme.Display(10.5f), new Rectangle(box.PreferredHeight / 2, 0, TagWidth, Height),
                    focused ? Theme.Accent : Theme.AccentText, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding);
        }
    }

    // Header strip: cave artwork, a left fade for legibility and a hazard stripe along the bottom.
    internal sealed class BannerPanel : Panel
    {
        private Bitmap cache;

        internal BannerPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            DropCache();
            base.OnResize(eventargs);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (cache == null || cache.Size != ClientSize) Render(Theme.Scale(e.Graphics));
            if (cache != null) e.Graphics.DrawImageUnscaled(cache, 0, 0);
        }

        private void Render(float k)
        {
            DropCache();
            Size size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            cache = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(cache))
            {
                g.Clear(Theme.Background);
                int stripe = (int)Math.Round(5 * k);
                var art = new Rectangle(0, 0, size.Width, size.Height - stripe);
                Image image = Theme.Banner;
                if (image != null)
                {
                    float scale = Math.Max(art.Width / (float)image.Width, art.Height / (float)image.Height);
                    float w = image.Width * scale, h = image.Height * scale;
                    Theme.DrawImage(g, image, new RectangleF(art.Right - w, art.Top + (art.Height - h) / 2, w, h));
                }
                using (var fade = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, art.Width * 6 / 10), art.Height),
                    Color.FromArgb(200, Theme.Background), Color.FromArgb(0, Theme.Background), LinearGradientMode.Horizontal))
                    g.FillRectangle(fade, 0, 0, art.Width * 6 / 10, art.Height);
                using (var scan = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                    for (int y = 0; y < art.Height; y += 3) g.FillRectangle(scan, 0, y, art.Width, 1);
                using (var edge = new SolidBrush(Theme.AccentDeep)) g.FillRectangle(edge, 0, art.Bottom - 1, art.Width, 1);
                Theme.Hazard(g, new Rectangle(0, art.Bottom, size.Width, stripe), 0, Theme.Accent, Theme.Background);
            }
        }

        private void DropCache()
        {
            if (cache != null) cache.Dispose();
            cache = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropCache();
            base.Dispose(disposing);
        }
    }

    // Large condensed title with an amber glow, followed by a small tag plate.
    internal sealed class TitleMark : Control
    {
        private readonly string tag;
        private readonly float size;

        internal TitleMark(string title, string tag, float size)
        {
            this.tag = tag;
            this.size = size;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = title;
            AccessibleName = title + " " + tag;
            AutoSize = true;
        }

        private Size TitleSize { get { return TextRenderer.MeasureText(Text, Theme.Display(size), Size.Empty, TextFormatFlags.NoPadding); } }
        private Size TagSize { get { return TextRenderer.MeasureText(tag, Theme.Strong(size * 0.4f), Size.Empty, TextFormatFlags.NoPadding); } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size title = TitleSize, plate = TagSize;
            return new Size(title.Width + plate.Width + plate.Height * 2 + 8, title.Height + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            Font font = Theme.Display(size);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float em = font.SizeInPoints * g.DpiY / 72f;
            RectangleF bounds;
            using (var path = new GraphicsPath())
            using (StringFormat format = StringFormat.GenericTypographic)
            {
                path.AddString(Text, font.FontFamily, (int)font.Style, em, new PointF(0, 0), format);
                bounds = path.GetBounds();
                using (var shift = new Matrix())
                {
                    shift.Translate(2 * k - bounds.Left, (Height - bounds.Height) / 2 - bounds.Top);
                    path.Transform(shift);
                }
                bounds = path.GetBounds();
                foreach (float width in new[] { 9f, 5f, 2.5f })
                    using (var glow = new Pen(Color.FromArgb(width > 5 ? 22 : width > 3 ? 40 : 70, Theme.Accent), width * k) { LineJoin = LineJoin.Round })
                        g.DrawPath(glow, path);
                using (var fill = new LinearGradientBrush(bounds, Theme.AccentHot, Theme.Accent, LinearGradientMode.Vertical))
                    g.FillPath(fill, path);
            }

            Size plate = TagSize;
            float pad = plate.Height * 0.55f;
            var tagBox = new RectangleF(bounds.Right + 12 * k, bounds.Bottom - plate.Height - pad * 0.6f, plate.Width + pad * 2, plate.Height + pad * 0.6f);
            using (GraphicsPath shape = Theme.Chamfer(tagBox, 5 * k))
            using (var pen = new Pen(Theme.Accent, 1))
            using (var fill = new SolidBrush(Color.FromArgb(170, Theme.Background)))
            {
                g.FillPath(fill, shape);
                g.DrawPath(pen, shape);
            }
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, tag, Theme.Strong(size * 0.4f), Rectangle.Round(tagBox), Theme.Text, TextFormatFlags.SingleLine |
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // Section caption: amber bar, bold text and a small Latin tag.
    internal sealed class Heading : Control
    {
        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter;
        private readonly string tag;

        internal Heading(string text, string tag)
        {
            this.tag = tag;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = text;
            AutoSize = true;
            Margin = new Padding(0, 4, 0, 4);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Theme.Strong(10f), Size.Empty, Flags);
            Size label = tag == null ? Size.Empty : TextRenderer.MeasureText(tag, Theme.Display(9.5f), Size.Empty, Flags);
            return new Size(12 + text.Width + (tag == null ? 0 : 10 + label.Width), Math.Max(text.Height, label.Height) + 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, 0, Height * 0.2f, 3 * k, Height * 0.6f);
            Size text = TextRenderer.MeasureText(g, Text, Theme.Strong(10f), Size.Empty, Flags);
            int x = (int)(10 * k);
            TextRenderer.DrawText(g, Text, Theme.Strong(10f), new Rectangle(x, 0, text.Width + 2, Height), Theme.Text, Flags);
            if (tag != null)
                TextRenderer.DrawText(g, tag, Theme.Display(9.5f), new Rectangle(x + text.Width + (int)(9 * k), 1, Width, Height),
                    Theme.AccentText, Flags);
        }
    }

    internal sealed class ArtBox : Control
    {
        private readonly Image image;

        internal ArtBox(Image image)
        {
            this.image = image;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (image == null) return;
            float side = Math.Min(Width, Height);
            Theme.DrawImage(e.Graphics, image, new RectangleF((Width - side) / 2, (Height - side) / 2, side, side));
        }
    }

    // Progress strip with hazard stripes; the stripes travel while the length is unknown.
    internal sealed class HazardProgress : Control
    {
        private readonly Timer timer = new Timer { Interval = 40 };
        private ProgressBarStyle style = ProgressBarStyle.Continuous;
        private int value;
        private float offset;

        internal HazardProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            timer.Tick += (sender, e) => { offset += 1.5f; Invalidate(); };
        }

        internal ProgressBarStyle Style
        {
            get { return style; }
            set { style = value; Animate(); Invalidate(); }
        }

        internal int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            Animate();
            base.OnVisibleChanged(e);
        }

        private void Animate() { timer.Enabled = Visible && style == ProgressBarStyle.Marquee; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g);
            using (var back = new SolidBrush(Theme.Field)) g.FillRectangle(back, ClientRectangle);
            var bar = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (style != ProgressBarStyle.Marquee) bar.Width = bar.Width * value / 100;
            Theme.Hazard(g, bar, style == ProgressBarStyle.Marquee ? offset * k : 0, Theme.Accent, Theme.AccentDeep);
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // Diamond indicator: cyan when ready, amber while working, red after an error.
    internal sealed class StatusLamp : Control
    {
        private Color light = Theme.Crystal;

        internal StatusLamp()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(18, 18);
        }

        internal Color Light
        {
            get { return light; }
            set { if (light != value) { light = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float k = Theme.Scale(g), cx = Width / 2f, cy = Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (float radius in new[] { 8f, 4f })
            {
                float r = radius * k;
                using (var brush = new SolidBrush(radius > 5 ? Color.FromArgb(60, light) : light))
                    g.FillPolygon(brush, new[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) });
            }
        }
    }
}
