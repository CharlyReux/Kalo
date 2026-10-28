using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Bungie;
using Bungie.Game;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// Can Kalo read and write Forge map variants? Reads the .mvar files MCC ships (read-only),
    /// loads each with ManagedBlam's MapVariant, writes it back to bytes and compares, then tries
    /// the XML export (the readable form a Forge editor would work on). Everything written goes to
    /// spike-results\forge\, never to MCC.
    /// </summary>
    internal static class ForgeProbe
    {
        private const string ForgeWorldScenario = @"levels\multi\forge_halo\forge_halo";

        public static void Run(Options o, Hrek hrek, Report report)
        {
            report.Section("Forge map variants");
            string mcc = hrek.GuessMccRoot();
            string dir = mcc == null ? null : Path.Combine(mcc, "haloreach", "map_variants");
            if (dir == null || !Directory.Exists(dir))
            {
                report.Fail("MCC map_variants folder not found next to HREK.");
                return;
            }
            report.Metric("Source (read-only)", dir);

            string outDir = Path.Combine(o.ResultsDir, "forge");
            Guard.EnsureWritable(outDir, "Forge exports");
            Directory.CreateDirectory(outDir);

            BlamSession.Start(o.WorkspaceDir, InitializationType.TagsOnly, report);
            try
            {
                report.Metric("Engine map variant version", Safe(() => MapVariant.GetCurrentMapVariantVersion().ToString()));

                int total = 0, loaded = 0, identical = 0, xmlOk = 0, xmlBack = 0;
                foreach (string file in Directory.GetFiles(dir, "*.mvar").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    total++;
                    string name = Path.GetFileNameWithoutExtension(file);
                    report.Line("");
                    report.Line("### " + name);

                    byte[] data = File.ReadAllBytes(file);
                    Blf.Chunk chunk = Blf.Find(data, "mvar");
                    if (chunk == null)
                    {
                        report.Metric("mvar chunk", "not found");
                        continue;
                    }
                    report.Metric("mvar chunk", "version " + chunk.Major + "." + chunk.Minor + ", " + chunk.Payload.Length + " bytes");

                    using (var variant = new MapVariant())
                    {
                        bool ok;
                        try { ok = variant.LoadMapVariantFromBLF(chunk.Payload); }
                        catch (Exception ex) { report.Metric("Load failed", ex.GetType().Name + ": " + ex.Message); continue; }
                        report.Metric("Loaded", ok.ToString());
                        if (!ok) continue;
                        loaded++;

                        int mapId = variant.GetMapId();
                        report.Metric("Map id", mapId.ToString());

                        byte[] back = variant.WriteMapVariantToBLFPayload();
                        string diff = Compare(chunk.Payload, back);
                        report.Metric("Write back to bytes", diff);
                        if (diff == "byte-identical") identical++;

                        XmlDocument doc = TryExportXml(variant, mapId, report);
                        if (doc == null) continue;
                        string xmlPath = Path.Combine(outDir, name + ".xml");
                        doc.Save(xmlPath);
                        xmlOk++;
                        report.Metric("XML export", "forge\\" + name + ".xml (" + new FileInfo(xmlPath).Length.ToString("N0") + " bytes)");
                        report.Metric("Scenario named in XML", Safe(() => MapVariant.GetScenarioTagPathFromXML(doc)));

                        // XML -> MapVariant -> bytes: the path a Forge editor would save through.
                        try
                        {
                            using (var reloaded = new MapVariant())
                            {
                                reloaded.LoadMapVariantFromXML(doc, ScenarioInfo(mapId, ForgeWorldScenario));
                                string d2 = Compare(chunk.Payload, reloaded.WriteMapVariantToBLFPayload());
                                report.Metric("XML back to bytes", d2);
                                if (d2 == "byte-identical") xmlBack++;
                            }
                        }
                        catch (Exception ex)
                        {
                            report.Metric("XML back to bytes failed", ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }

                report.Section("Forge summary");
                report.Metric("Map variants found", total.ToString());
                report.Check("All map variants load", total > 0 && loaded == total);
                report.Metric("Byte-identical write-back", identical + " of " + loaded);
                report.Metric("Exported to XML", xmlOk + " of " + loaded);
                report.Metric("Byte-identical after XML round trip", xmlBack + " of " + xmlOk);
                report.Check("At least one map variant exported to XML", xmlOk > 0);
            }
            finally
            {
                BlamSession.Stop(report);
            }
        }

        /// <summary>
        /// The XML export wants scenario information (object palette lookups). We do not know yet how
        /// to fill the palettes, so try the simplest forms and report which one works.
        /// </summary>
        private static XmlDocument TryExportXml(MapVariant variant, int mapId, Report report)
        {
            var attempts = new List<KeyValuePair<string, MapVariantScenarioInfo>>
            {
                new KeyValuePair<string, MapVariantScenarioInfo>("map id only", ScenarioInfo(mapId, null)),
                new KeyValuePair<string, MapVariantScenarioInfo>("map id + Forge World scenario", ScenarioInfo(mapId, ForgeWorldScenario))
            };
            foreach (var attempt in attempts)
            {
                try
                {
                    XmlDocument doc = variant.WriteMapVariantToXML(attempt.Value, true);
                    if (doc != null && doc.DocumentElement != null)
                    {
                        report.Metric("XML export worked with", attempt.Key);
                        return doc;
                    }
                    report.Metric("XML export (" + attempt.Key + ")", "returned nothing");
                }
                catch (Exception ex)
                {
                    report.Metric("XML export (" + attempt.Key + ") failed", ex.GetType().Name + ": " + ex.Message);
                }
            }
            return null;
        }

        private static MapVariantScenarioInfo ScenarioInfo(int mapId, string scenario)
        {
            var info = new MapVariantScenarioInfo();
            info.mapID = mapId;
            info.scenarioTagPath = scenario;
            // Empty palette lookups, created by reflection so this compiles whatever their constructors are.
            try { info.pathToPalette = (TagPathToPaletteDict)Activator.CreateInstance(typeof(TagPathToPaletteDict)); } catch (Exception) { }
            try { info.paletteToPath = (PaletteEntryToTagPathDict)Activator.CreateInstance(typeof(PaletteEntryToTagPathDict)); } catch (Exception) { }
            return info;
        }

        private static string Compare(byte[] a, byte[] b)
        {
            if (b == null) return "nothing written";
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                    return "differs at byte " + i + " (original " + a.Length + " bytes, written " + b.Length + ")";
            }
            if (a.Length == b.Length) return "byte-identical";
            return "same first " + n + " bytes, length differs (original " + a.Length + ", written " + b.Length + ")";
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { return "<" + ex.GetType().Name + ": " + ex.Message + ">"; }
        }
    }

    /// <summary>Minimal reader for Bungie's BLF container (big-endian chunk headers: signature, size, major, minor).</summary>
    internal static class Blf
    {
        internal sealed class Chunk
        {
            public string Signature;
            public int Offset;
            public int Major;
            public int Minor;
            public byte[] Payload;
        }

        public static Chunk Find(byte[] data, string signature)
        {
            int i = 0;
            while (i + 12 <= data.Length)
            {
                string sig = System.Text.Encoding.ASCII.GetString(data, i, 4);
                int size = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
                if (size < 12 || i + size > data.Length) return null;
                if (sig == signature)
                {
                    var c = new Chunk
                    {
                        Signature = sig,
                        Offset = i,
                        Major = (data[i + 8] << 8) | data[i + 9],
                        Minor = (data[i + 10] << 8) | data[i + 11],
                        Payload = new byte[size - 12]
                    };
                    Buffer.BlockCopy(data, i + 12, c.Payload, 0, size - 12);
                    return c;
                }
                i += size;
            }
            return null;
        }
    }
}
