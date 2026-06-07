// ============================================================================
//  SpriteIcon.cs  -  Mapping for Icons_Desaturated.png
// ----------------------------------------------------------------------------
//  Atlas : 1024 x 768 px, 32-bit RGBA (premultiplied-safe), transparent bg.
//  Grid  : 8 columns x 6 rows = 48 cells, each 128 x 128 px.
//  Icons are DESATURATED (grayscale) with baked 3D shading so they tint
//  correctly under a multiply blend. Pass any RGBA tint to ImGui.Image /
//  AddImage and the white highlights become the tint colour while the dark
//  shading is preserved.
//
//  Usage (ExileCore / ImGui.NET):
//      var uv = SpriteAtlas.GetUV(SpriteIcon.Hexagon);
//      ImGui.Image(textureId, new Vector2(24, 24), uv.Uv0, uv.Uv1, tintRGBA);
//
//  Or with a draw list:
//      var (uv0, uv1) = SpriteAtlas.GetUVPair(SpriteIcon.Star5);
//      drawList.AddImage(textureId, pMin, pMax, uv0, uv1,
//                        ImGui.ColorConvertFloat4ToU32(tint));
// ============================================================================

using System.Numerics;

namespace ExileMaps.Classes
{
    /// <summary>Index of each icon in Icons_Desaturated.png (row-major, 0-based).</summary>
    public enum SpriteIcon
    {
        // ---- Row 0 : filled solid shapes ----
        Circle          = 0,
        Square          = 1,
        TriangleUp      = 2,
        TriangleDown    = 3,
        Diamond         = 4,
        Hexagon         = 5,
        Pentagon        = 6,
        Octagon         = 7,

        // ---- Row 1 : filled stars / specials ----
        Star5           = 8,
        Star6           = 9,
        Star4           = 10,   // 4-point sparkle
        Star8           = 11,   // 8-point burst
        Crescent        = 12,
        Teardrop        = 13,
        Kite            = 14,
        Shield          = 15,

        // ---- Row 2 : marks / abstract ----
        Heart           = 16,
        Plus            = 17,   // thick cross / plus
        Cross           = 18,   // X mark
        Chevron         = 19,   // up chevron
        Arrow           = 20,   // up arrow
        Dot             = 21,   // small filled circle
        Flower4         = 22,
        Flower6         = 23,

        // ---- Row 3 : abstract / geometric ----
        Pinwheel        = 24,
        Trapezoid       = 25,
        Dome            = 26,   // half circle
        Pill            = 27,   // horizontal stadium
        DiamondCluster  = 28,   // 4 small diamonds
        Target          = 29,   // concentric rings
        Burst12         = 30,   // 12-point sunburst
        Exclamation     = 31,

        // ---- Row 4 : outline shapes ----
        CircleOutline   = 32,   // ring
        SquareOutline   = 33,
        TriangleOutline = 34,
        DiamondOutline  = 35,
        HexagonOutline  = 36,
        PentagonOutline = 37,
        OctagonOutline  = 38,
        Star5Outline    = 39,

        // ---- Row 5 : outline specials ----
        Star6Outline    = 40,
        Star4Outline    = 41,
        CrescentOutline = 42,
        TeardropOutline = 43,
        KiteOutline     = 44,
        ShieldOutline   = 45,
        HeartOutline    = 46,
        RingThin        = 47,   // thin outline circle
    }

    /// <summary>UV / source-rect helpers for the desaturated icon atlas.</summary>
    public static class SpriteAtlas
    {
        public const int AtlasWidth  = 1024;
        public const int AtlasHeight = 768;
        public const int CellSize    = 128;
        public const int Columns     = 8;
        public const int Rows        = 6;
        public const int Count       = Columns * Rows; // 48

        public const string FileName = "Icons_Desaturated.png";

        /// <summary>Top-left pixel of the icon's cell.</summary>
        public static (int X, int Y) GetCell(SpriteIcon icon)
        {
            int i = (int)icon;
            return ((i % Columns) * CellSize, (i / Columns) * CellSize);
        }

        /// <summary>Pixel source rectangle (x, y, w, h) inside the atlas.</summary>
        public static (int X, int Y, int W, int H) GetSourceRect(SpriteIcon icon)
        {
            var (x, y) = GetCell(icon);
            return (x, y, CellSize, CellSize);
        }

        /// <summary>Normalised UVs as (Uv0, Uv1) corner pair for ImGui.</summary>
        public static (Vector2 Uv0, Vector2 Uv1) GetUVPair(SpriteIcon icon)
        {
            var (x, y) = GetCell(icon);
            var uv0 = new Vector2((float)x / AtlasWidth,
                                  (float)y / AtlasHeight);
            var uv1 = new Vector2((float)(x + CellSize) / AtlasWidth,
                                  (float)(y + CellSize) / AtlasHeight);
            return (uv0, uv1);
        }

        /// <summary>Convenience struct accessor for GetUVPair.</summary>
        public static UvRect GetUV(SpriteIcon icon)
        {
            var (uv0, uv1) = GetUVPair(icon);
            return new UvRect { Uv0 = uv0, Uv1 = uv1 };
        }
    }

    public struct UvRect
    {
        public Vector2 Uv0;
        public Vector2 Uv1;
    }
}
