using System;
using System.Drawing;
using System.IO;
using LGEngine;

namespace LGGpu
{
    // LGCpuRef.exe: draws the same test cards with the CPU engine (LGEngine.dll, the reference)
    // over the same scene, so the GPU shader's look can be set against it side by side.
    //   LGCpuRef.exe --out DIR [--case NAME] [--scene mixed|white|dark] [--edge]
    internal static class CpuRef
    {
        static int Main(string[] args)
        {
            string dir = null, only = null, scene = "mixed";
            bool edge = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) dir = args[++i];
                else if (args[i] == "--case" && i + 1 < args.Length) only = args[++i];
                else if (args[i] == "--scene" && i + 1 < args.Length) scene = args[++i];
                else if (args[i] == "--edge") edge = true;
            }
            if (dir == null) { Console.WriteLine("--out DIR needed"); return 2; }
            Directory.CreateDirectory(dir);
            int sceneW = 1400, sceneH = 900;
            int[] scenePx = TestArt.Scene(sceneW, sceneH, scene);
            object[][] cases = {
                new object[] { "toast-200", 792, 380, 2f, false, 50, -1f },
                new object[] { "toast-100", 396, 190, 1f, false, 50, -1f },
                new object[] { "osd-lens-200", 424, 122, 2f, true, 50, -1f },
                new object[] { "pill-150", 330, 72, 1.5f, true, 50, -1f },
                new object[] { "toast-200-light", 792, 380, 2f, false, 50, 1f },
                new object[] { "toast-200-elev100", 792, 380, 2f, false, 100, -1f },
                new object[] { "toast-200-elev0", 792, 380, 2f, false, 0, -1f },
                new object[] { "dock-200", 1100, 140, 2f, false, 70, -1f },
            };
            foreach (object[] cs in cases)
            {
                string name = (string)cs[0];
                if (only != null && name != only) continue;
                int cardW = (int)cs[1], cardH = (int)cs[2];
                float k = (float)cs[3];
                bool lens = (bool)cs[4];
                int elev = (int)cs[5];
                float appearance = (float)cs[6];
                Render(dir, name, scenePx, sceneW, sceneH, cardW, cardH, k, lens, elev, appearance, edge);
            }
            return 0;
        }

        static void Render(string dir, string name, int[] scenePx, int sceneW, int sceneH, int cardW, int cardH, float k, bool lens, int elev, float appearance, bool edge)
        {
            int cardX, cardY;
            Program.CardPlace(sceneW, sceneH, cardW, cardH, edge, out cardX, out cardY);
            GlassLook look = new GlassLook(30, 44, 84, 63, 36, 24, 100, elev);
            GlassShape shape = lens ? GlassShape.Lens : GlassShape.Card;
            GlassMap map = new GlassMap(cardW, cardH, k, look, shape);
            GlassBackdrop backdrop = new GlassBackdrop();
            int[] into = backdrop.Buffer(map);
            Rectangle area = GlassBackdrop.AreaFor(map, cardX, cardY);
            // the picture behind the card, as the live feed hands it over (step x step averages, edges repeated)
            int step = map.SharpFactor, n = step * step;
            for (int ty = 0; ty < map.SharpH; ty++)
                for (int tx = 0; tx < map.SharpW; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int v = 0; v < step; v++)
                    {
                        int y = Math.Max(0, Math.Min(sceneH - 1, area.Top + ty * step + v));
                        for (int u = 0; u < step; u++)
                        {
                            int x = Math.Max(0, Math.Min(sceneW - 1, area.Left + tx * step + u));
                            int p = scenePx[y * sceneW + x];
                            r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255;
                        }
                    }
                    into[ty * map.SharpW + tx] = ((r / n) << 16) | ((g / n) << 8) | (b / n);
                }
            backdrop.Accept(map, false);
            backdrop.Measure(map);
            // as GlassOverlay sets them: light or dark by what is behind, evened out by its spread,
            // the shadow deepened over bright or busy backdrops
            float ev = (backdrop.Spread - 24) / 46f;
            ev = ev <= 0 ? 0 : (ev >= 1 ? 1 : ev * ev * (3 - 2 * ev));
            float even = (float)Math.Round(ev * 16) / 16f;
            map.Even = even * even * (3 - 2 * even);
            map.AvgR = backdrop.AvgR; map.AvgG = backdrop.AvgG; map.AvgB = backdrop.AvgB;
            map.Appearance = appearance >= 0 ? appearance : (backdrop.MidLuma >= 165 ? 1 : (backdrop.MidLuma <= 110 ? 0 : (backdrop.MidLuma - 110) / 55f));
            float bright = Math.Max(0f, Math.Min(1f, (backdrop.MidLuma - 80) / 150f)), busy = Math.Max(0f, Math.Min(1f, (backdrop.Spread - 20) / 70f));
            map.ShadowGain = Math.Min(1.3f, 0.85f + 0.45f * bright + 0.25f * busy);
            int[] content = lens ? TestArt.LensContent(cardW, cardH, k) : TestArt.ToastContent(cardW, cardH, k);
            int[] dst = new int[map.W * map.H];
            map.Prime(dst);
            map.Shade(backdrop.Soft, backdrop.Edge, content, cardW, cardH, 0, 0, dst);
            // over the scene, the window's part of it
            int winX = cardX - map.Margin, winY = cardY - map.Margin;
            int[] outPx = new int[map.W * map.H];
            for (int y = 0; y < map.H; y++)
                for (int x = 0; x < map.W; x++)
                {
                    int sx = winX + x, sy = winY + y;
                    outPx[y * map.W + x] = sx >= 0 && sy >= 0 && sx < sceneW && sy < sceneH ? scenePx[sy * sceneW + sx] | unchecked((int)0xFF000000) : unchecked((int)0xFF808080);
                }
            TestArt.Over(outPx, map.W, map.H, dst, map.W, map.H, 0, 0);
            string path = Path.Combine(dir, name + "-cpu.png");
            TestArt.SavePng(outPx, map.W, map.H, path);
            Console.WriteLine("saved " + path + "  (window " + map.W + "x" + map.H + ", margin " + map.Margin + ", backdrop " + map.SharpW * map.SharpFactor + "x" + map.SharpH * map.SharpFactor + ", appearance " + map.Appearance + ", even " + map.Even + ")");
        }
    }
}
