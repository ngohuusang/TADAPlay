using System.Drawing;
using System.Windows.Forms;
using AntdUI;

namespace TadaPlay.Controls
{
    /// <summary>
    /// The app's shared look, built out of AntdUI rather than raw WinForms.
    ///
    /// Started life inside <see cref="Setting"/> and moved out because the two answer different questions. Setting.cs
    /// is about what a setting DOES - which folder, which launcher, what happens on save - and
    /// that reads badly with colours and control chrome threaded through it. Nothing in this file
    /// knows what any setting means.
    ///
    /// Everything here is AntdUI: the app already ships it (it is what draws the modals, the
    /// notifications and the online list), so using it here costs nothing and gets rounded
    /// corners, shadows, hover states, focus rings and a coherent palette for free - all of which
    /// would otherwise be hand-painted, and would drift from the rest of the app the first time
    /// antd's own colours changed.
    /// </summary>
    internal static class UiTheme
    {
        public static readonly Color PageBg = Color.FromArgb(0xF3, 0xF6, 0xFA);
        public static readonly Color CardBorder = Color.FromArgb(0xE8, 0xED, 0xF4);
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
        public static readonly Color Ink = Color.FromArgb(0x1F, 0x2A, 0x37);

        /// <summary>A bound key reads as a keycap: darker border than an empty one.</summary>
        public static readonly Color KeyBorder = Color.FromArgb(0xB4, 0xBE, 0xCC);

        /// <summary>Counts on a list row. Antd badges default to error red, which turns every
        /// count into an alarm; this is a plain slate that reads as information.</summary>
        public static readonly Color BadgeBack = Color.FromArgb(0x8C, 0x96, 0xA8);

        /// <summary>Alternate-row band in a list. Faint on purpose - a guide, not a thing to
        /// look at.</summary>
        public static readonly Color RowBand = Color.FromArgb(0xF7, 0xF9, 0xFC);

        // While a key button is waiting for a keypress. Amber, because it is a state the player
        // has to get out of - by pressing something, or Esc.
        public static readonly Color CapturingBack = Color.FromArgb(0xFF, 0xF7, 0xE0);
        public static readonly Color CapturingBorder = Color.FromArgb(0xFA, 0xAD, 0x14);

        // Section accents, in antd's own hues so they sit beside the greens and golds the online
        // list already uses.
        public static readonly Color AccentDisplay = Color.FromArgb(0x15, 0x77, 0xD4); // blue-6
        public static readonly Color AccentFolder = Color.FromArgb(0x38, 0x9E, 0x0D);  // green-6
        public static readonly Color AccentAccount = Color.FromArgb(0xD4, 0x88, 0x06); // gold-7

        /// <summary>
        /// Gold, dark enough to carry white text. gold-7 is right for an icon or a tinted band
        /// but only reaches about 2.6:1 against white, so a button filled with it has unreadable
        /// text; gold-8 clears 4.5:1 and still reads as the same colour.
        /// </summary>
        public static readonly Color AccentAccountStrong = Color.FromArgb(0xAD, 0x68, 0x00);

        // Inline antd outline icons. AntdUI takes raw SVG for any *Svg property and recolours it
        // to match the control, so these need no assets and follow the accent they are given.
        public const string IconDisplay =
            "<svg viewBox='0 0 1024 1024'><path d='M928 140H96c-17.7 0-32 14.3-32 32v496c0 17.7 14.3 32 32 32h380v112H304c-8.8 0-16 7.2-16 16v48c0 4.4 3.6 8 8 8h432c4.4 0 8-3.6 8-8v-48c0-8.8-7.2-16-16-16H548V700h380c17.7 0 32-14.3 32-32V172c0-17.7-14.3-32-32-32z m-40 488H136V212h752v416z'/></svg>";
        public const string IconFolder =
            "<svg viewBox='0 0 1024 1024'><path d='M880 298.4H521L403.7 186.2a8.15 8.15 0 0 0-5.5-2.2H144c-17.7 0-32 14.3-32 32v592c0 17.7 14.3 32 32 32h736c17.7 0 32-14.3 32-32V330.4c0-17.7-14.3-32-32-32zM840 768H184V256h188.5l119.6 114.4H840V768z'/></svg>";
        public const string IconAccount =
            "<svg viewBox='0 0 1024 1024'><path d='M858.5 763.6a374 374 0 0 0-80.6-119.5 375.63 375.63 0 0 0-119.5-80.6c-.4-.2-.8-.3-1.2-.5C719.5 518 760 444.7 760 362c0-137-111-248-248-248S264 225 264 362c0 82.7 40.5 156 102.8 201.1-.4.2-.8.3-1.2.5-44.8 18.9-85 46-119.5 80.6a375.63 375.63 0 0 0-80.6 119.5A371.7 371.7 0 0 0 136 901.8a8 8 0 0 0 8 8.2h60c4.4 0 7.9-3.5 8-7.8 2-77.2 33-149.5 87.8-204.3 56.7-56.7 132-87.9 212.2-87.9s155.5 31.2 212.2 87.9C779 752.7 810 825 812 902.2c.1 4.4 3.6 7.8 8 7.8h60a8 8 0 0 0 8-8.2c-1-47.8-10.9-94.3-29.5-138.2zM512 534c-45.9 0-89.1-17.9-121.6-50.4S340 407.9 340 362c0-45.9 17.9-89.1 50.4-121.6S466.1 190 512 190s89.1 17.9 121.6 50.4S684 316.1 684 362c0 45.9-17.9 89.1-50.4 121.6S557.9 534 512 534z'/></svg>";

        /// <summary>The accent at roughly a tenth strength, for tinted title bands.</summary>
        public static Color Tint(Color accent, int percent = 16)
        {
            int Mix(int c) => c + (255 - c) * (100 - percent) / 100;
            return Color.FromArgb(Mix(accent.R), Mix(accent.G), Mix(accent.B));
        }

        /// <summary>
        /// One settings section as an antd card: rounded, softly shadowed, white on the page's
        /// grey. <paramref name="content"/> is where the section's own controls go.
        ///
        /// The header is a tinted band with the accent icon and title, so the three sections are
        /// told apart at a glance by colour rather than by reading their headings.
        /// </summary>
        public static AntdUI.Panel Card(string iconSvg, string title, string subtitle,
                                        Color accent, int height, out System.Windows.Forms.Panel content)
        {
            var card = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = height,
                Radius = 10,
                Shadow = 8,
                ShadowOpacity = 0.10F,
                ShadowOffsetY = 2,
                Back = Color.White,
                BorderWidth = 1,
                BorderColor = CardBorder,
                Padding = new Padding(0),
                Margin = new Padding(0, 0, 0, 14),
            };

            var band = new AntdUI.Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                Back = Tint(accent),
                Radius = 10,
                RadiusAlign = TAlignRound.Top,   // square at the bottom, where the body meets it
                Padding = new Padding(14, 0, 14, 0),
            };

            // Two labels, not one with a Suffix: as a suffix the subtitle sits immediately
            // after the title and pushes it around, and a long one squeezed the title until it
            // wrapped mid-word. Docked Right, the subtitle is the part that gets clipped.
            var subtitleLabel = new AntdUI.Label
            {
                Dock = DockStyle.Right,
                Width = 190,
                Text = subtitle,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };
            var heading = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                PrefixSvg = iconSvg,
                PrefixColor = accent,
                ForeColor = Dark(accent),
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                IconGap = 10,
                BackColor = Color.Transparent,
            };
            band.Controls.Add(heading);
            band.Controls.Add(subtitleLabel);

            content = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(16, 12, 16, 12),
            };

            // Fill first, then the edge - the order the rest of this app's layouts use.
            card.Controls.Add(content);
            card.Controls.Add(band);
            return card;
        }

        private static Color Dark(Color c) =>
            Color.FromArgb(c.R * 72 / 100, c.G * 72 / 100, c.B * 72 / 100);

        /// <summary>
        /// The one action a card is really for: a filled, rounded antd button.
        ///
        /// Takes the card's own accent rather than antd's default blue, so the button belongs to
        /// the section it sits in - a blue Save inside the gold account card reads as though it
        /// came from somewhere else.
        /// </summary>
        public static AntdUI.Button Primary(string text, Color accent, string iconSvg = null,
                                            int height = 44)
        {
            return new AntdUI.Button
            {
                Text = text,
                IconSvg = iconSvg,
                Dock = DockStyle.Top,
                Height = height,
                Type = TTypeMini.Primary,
                Shape = TShape.Round,
                BackColor = accent,
                BackHover = Lighten(accent, 10),
                BackActive = Darken(accent, 12),
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
        }

        private static Color Lighten(Color c, int percent)
        {
            int Mix(int v) => v + (255 - v) * percent / 100;
            return Color.FromArgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }

        private static Color Darken(Color c, int percent) =>
            Color.FromArgb(c.R * (100 - percent) / 100,
                           c.G * (100 - percent) / 100,
                           c.B * (100 - percent) / 100);

        /// <summary>A supporting action: outlined, same shape, quieter.</summary>
        public static AntdUI.Button Quiet(string text, string iconSvg = null, int height = 40)
        {
            // BorderWidth explicitly: antd's Default button is borderless, which on a white card
            // leaves nothing to show it is a button at all - it reads as a line of text.
            return new AntdUI.Button
            {
                Text = text,
                IconSvg = iconSvg,
                Dock = DockStyle.Top,
                Height = height,
                Type = TTypeMini.Default,
                Shape = TShape.Round,
                BorderWidth = 1,
                DefaultBorderColor = Color.FromArgb(0xD0, 0xD7, 0xE2),
                DefaultBack = Color.White,
                Font = new Font("Segoe UI", 10F),
                Cursor = Cursors.Hand,
            };
        }

        /// <summary>An antd text field. Placeholder and prefix icon are optional.</summary>
        public static AntdUI.Input Field(string placeholder = null, string prefixSvg = null,
                                         bool password = false, int height = 40)
        {
            var input = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                Height = height,
                Radius = 8,
                PlaceholderText = placeholder,
                PrefixSvg = prefixSvg,
                Font = new Font("Segoe UI", 10.5F),
            };
            if (password) input.UseSystemPasswordChar = true;
            return input;
        }

        /// <summary>The caption above a field.</summary>
        public static AntdUI.Label Caption(string text)
        {
            return new AntdUI.Label
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9.5F),
                TextAlign = ContentAlignment.BottomLeft,
            };
        }

        public const string IconSearch =
            "<svg viewBox='0 0 1024 1024'><path d='M909.6 854.5L649.9 594.8C690.2 542.7 712 479 712 412c0-80.2-31.3-155.4-87.9-212.1-56.6-56.7-132-87.9-212.1-87.9s-155.5 31.3-212.1 87.9C143.2 256.5 112 331.8 112 412c0 80.1 31.3 155.5 87.9 212.1C256.5 680.8 331.8 712 412 712c67 0 130.6-21.8 182.7-62l259.7 259.6a8.2 8.2 0 0 0 11.6 0l43.6-43.5a8.2 8.2 0 0 0 0-11.6zM570.4 570.4C528 612.7 471.8 636 412 636s-116-23.3-158.4-65.6C211.3 528 188 471.8 188 412s23.3-116.1 65.6-158.4C296 211.3 352.2 188 412 188s116.1 23.2 158.4 65.6S636 352.2 636 412s-23.3 116.1-65.6 158.4z'/></svg>";

        public const string IconTrophy =
            "<svg viewBox='0 0 1024 1024'><path d='M868 160h-92v-40c0-4.4-3.6-8-8-8H256c-4.4 0-8 3.6-8 8v40h-92c-24.3 0-44 19.7-44 44v148c0 81.7 60 149.6 138.2 162C265.6 541 359 632 480 644v128h-88c-30.9 0-56 25.1-56 56v92c0 4.4 3.6 8 8 8h336c4.4 0 8-3.6 8-8v-92c0-30.9-25.1-56-56-56h-88V644c121-12 214.4-103 225.8-230C848 401.6 908 333.7 908 252V204c0-24.3-19.7-44-40-44zM184 352V232h64v207.6a91.99 91.99 0 0 1-64-87.6zm520 128c0 49.1-19.1 95.4-53.9 130.1C615.3 645 569 664 520 664h-16c-49.1 0-95.4-19.1-130.1-53.9C339 575.3 320 529 320 480V184h384v296zm136-128c0 41-27 75.8-64 87.6V232h64v120z'/></svg>";

        /// <summary>
        /// An undo arrow: a shaft with a solid head. Deliberately not the circular "reload" ring -
        /// at this size that renders as an open arc indistinguishable from a letter C, which sat
        /// in the hotkey list one column from a key button showing the letter C.
        /// </summary>
        public const string IconReset =
            "<svg viewBox='0 0 1024 1024'><path d='M511.4 124C290.5 124.3 112 302.9 112 523.8c0 110.1 44.4 209.9 116.3 282.3l-58.1 58.1a8 8 0 0 0 5.6 13.6H392c4.4 0 8-3.6 8-8V653.8c0-7.1-8.6-10.7-13.6-5.7l-59.4 59.4a303.5 303.5 0 0 1-88.1-215.2c0-79.9 30.8-155.1 86.7-211.6 55.9-56.5 130.7-88 210.6-88.4 79.9-0.4 155.6 30.4 212.6 86.4 57 56 88.6 131.2 88.6 211.1 0 79.9-31.1 155.1-87.7 211.6-33.3 33.2-72.9 57.5-116 71.5a8 8 0 0 0-5.1 10.2l19.4 56.6a8 8 0 0 0 10.1 5c55.1-17.9 105.6-49 147.9-91.3 71.9-71.9 111.5-167.6 111.5-269.4 0-101.8-39.6-197.5-111.5-269.4C733.2 163.8 637.5 124.3 535.7 124h-24.3z'/></svg>";

        /// <summary>A small toolbar button - outlined, fixed position, for a top row of actions.</summary>
        public static AntdUI.Button Toolbar(string text, Point location, int width, int height = 36)
        {
            return new AntdUI.Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, height),
                Type = TTypeMini.Default,
                Shape = TShape.Round,
                BorderWidth = 1,
                DefaultBorderColor = Color.FromArgb(0xD0, 0xD7, 0xE2),
                DefaultBack = Color.White,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
            };
        }

        /// <summary>A gap between docked controls, on whichever edge they are stacking against.</summary>
        public static System.Windows.Forms.Panel Spacer(DockStyle dock, int size) =>
            new System.Windows.Forms.Panel
            {
                Dock = dock,
                Width = size,
                Height = size,
                BackColor = Color.Transparent,
            };

        /// <summary>Vertical breathing room between docked controls.</summary>
        public static System.Windows.Forms.Panel Gap(int height) =>
            new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Top,
                Height = height,
                BackColor = Color.Transparent,
            };
    }
}
