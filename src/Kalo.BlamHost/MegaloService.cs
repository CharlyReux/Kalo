extern alias megalo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Kalo.Protocol;
using M = megalo::Bungie.Megalo;

namespace Kalo.BlamHost
{
    /// <summary>
    /// Gametype scripts (Megalo) through HREK's ManagedMegalo: the same compiler MegaloEdit uses.
    /// Started on first use, in the same process as ManagedBlam.
    /// </summary>
    internal static class MegaloService
    {
        private static string _objectLists;

        private static void Ensure(string objectLists)
        {
            if (string.IsNullOrEmpty(objectLists) || !Directory.Exists(objectLists))
                throw new DirectoryNotFoundException("Megalo object lists not found: " + objectLists);
            if (!M.ManagedMegaloSystem.IsInitialized)
            {
                var sw = Stopwatch.StartNew();
                M.ManagedMegaloSystem.Initialize();
                Server.Log("ManagedMegalo started in " + sw.ElapsedMilliseconds + " ms (" + M.ManagedMegaloSystem.BuildString + ")");
            }
            if (!string.Equals(_objectLists, objectLists, StringComparison.OrdinalIgnoreCase))
            {
                M.MegaloCompiler.Initialize(objectLists.TrimEnd('\\') + "\\", "Kalo");
                _objectLists = objectLists;
            }
        }

        public static MegaloCatalog Catalog(string objectLists)
        {
            Ensure(objectLists);
            return new MegaloCatalog
            {
                Actions = Safe(() => M.MegaloCompiler.GetActionList()).Select(a => new MegaloEntry { Name = a, Description = SafeText(() => M.MegaloCompiler.DescribeAction(a)) }).ToList(),
                Conditions = Safe(() => M.MegaloCompiler.GetConditionList()).Select(c => new MegaloEntry { Name = c, Description = SafeText(() => M.MegaloCompiler.DescribeCondition(c)) }).ToList(),
                Lists = new Dictionary<string, List<string>>
                {
                    { "weapons", Safe(() => M.MegaloCompiler.GetWeaponList()) },
                    { "vehicles", Safe(() => M.MegaloCompiler.GetVehicleList()) },
                    { "equipment", Safe(() => M.MegaloCompiler.GetEquipmentList()) },
                    { "grenades", Safe(() => M.MegaloCompiler.GetGrenadeList()) },
                    { "objectTypes", Safe(() => M.MegaloCompiler.GetObjectTypeList()) },
                    { "weaponSets", Safe(() => M.MegaloCompiler.GetWeaponSetList()) },
                    { "vehicleSets", Safe(() => M.MegaloCompiler.GetVehicleSetList()) },
                    { "sounds", Safe(() => M.MegaloCompiler.GetSoundList()) },
                    { "incidents", Safe(() => M.MegaloCompiler.GetIncidentList()) },
                    { "loadouts", Safe(() => M.MegaloCompiler.GetLoadoutList()) },
                    { "loadoutPalettes", Safe(() => M.MegaloCompiler.GetLoadoutPaletteList()) },
                    { "hudWidgetIcons", Safe(() => M.MegaloCompiler.GetHudWidgetIconList()) },
                    { "gameOptions", Safe(() => M.MegaloCompiler.GetOverridableGameOptionsList()) },
                },
                DeployDirectory = SafeText(() => M.MegaloCompiler.GetMegaloDeployDirectory()),
                EncodingVersion = M.MegaloCompiler.GetCurrentEncodingVersion(),
                EngineBuild = SafeText(() => M.ManagedMegaloSystem.BuildString)
            };
        }

        public static MegaloCompileResult Compile(MegaloCompileParams p)
        {
            Ensure(p.ObjectLists);
            if (string.IsNullOrEmpty(p.IncludeFolder) || !Directory.Exists(p.IncludeFolder))
                throw new DirectoryNotFoundException("Include folder not found: " + p.IncludeFolder);
            string include = p.IncludeFolder.TrimEnd('\\') + "\\";
            string output = p.OutputFolder;
            bool write = !string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(p.MgloFile) || !string.IsNullOrEmpty(p.BinFile);
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetTempPath(), "kalo-megalo-scratch");
            Directory.CreateDirectory(output);
            DateTime before = DateTime.UtcNow.AddSeconds(-1);

            var sw = Stopwatch.StartNew();
            M.ParsingResult r;
            using (var reader = new StringReader(p.Text ?? ""))
                r = M.MegaloCompiler.Compile(reader, include, output.TrimEnd('\\') + "\\", write, false);
            long ms = sw.ElapsedMilliseconds;

            var result = new MegaloCompileResult
            {
                Succeeded = r.Succeeded,
                Ms = ms,
                Messages = (r.Messages ?? Enumerable.Empty<M.ParsingMessage>()).Select(m => new MegaloMessage
                {
                    Line = m.Token == null ? 0 : m.Token.LineIndex + 1,
                    Start = m.Token == null ? 0 : m.Token.StartPosition,
                    Length = m.Token == null ? 0 : m.Token.Length,
                    Token = m.Token == null ? null : m.Token.Text,
                    Severity = m.Severity.ToString().Replace("eParsingMessageSeverity_", ""),
                    Message = m.Message
                }).ToList(),
                Stats = new Dictionary<string, long>(),
                Variables = new Dictionary<string, List<string>>(),
                OutputFiles = write ? Directory.GetFiles(output).Where(f => File.GetLastWriteTimeUtc(f) >= before).ToList() : new List<string>()
            };
            if (r.Succeeded && !string.IsNullOrEmpty(p.MgloFile) && r.SerializedEngine != null && r.SerializedEngine.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p.MgloFile));
                File.WriteAllBytes(p.MgloFile, r.SerializedEngine);
                result.OutputFiles.Add(p.MgloFile);
            }
            if (r.Succeeded && !string.IsNullOrEmpty(p.BinFile) && r.SerializedEngineBin != null && r.SerializedEngineBin.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p.BinFile));
                File.WriteAllBytes(p.BinFile, r.SerializedEngineBin);
                result.OutputFiles.Add(p.BinFile);
            }
            M.EngineStats s = r.Stats;
            foreach (var f in typeof(M.EngineStats).GetFields())
                result.Stats[f.Name] = Convert.ToInt64(f.GetValue(s));
            if (r.SerializedEngineBin != null) result.Stats["SerializedBytes"] = r.SerializedEngineBin.Length;
            AddVariables(result.Variables, "global", r.GlobalVariables);
            AddVariables(result.Variables, "player", r.PlayerVariables);
            AddVariables(result.Variables, "team", r.TeamVariables);
            AddVariables(result.Variables, "object", r.ObjectVariables);
            if (r.GameOptionNames != null) result.Variables["options"] = r.GameOptionNames.Select(t => t.Text).ToList();
            if (r.HudWidgetNames != null) result.Variables["widgets"] = r.HudWidgetNames.Select(t => t.Text).ToList();
            if (r.GameStatisticNames != null) result.Variables["stats"] = r.GameStatisticNames.Select(t => t.Text).ToList();
            return result;
        }

        private static void AddVariables(Dictionary<string, List<string>> into, string scope, M.VariableNameBlock block)
        {
            Action<string, List<M.Token>> add = (kind, list) =>
            {
                if (list != null && list.Count > 0) into[scope + "." + kind] = list.Where(t => t != null).Select(t => t.Text).ToList();
            };
            add("number", block.NumericVariableNames);
            add("object", block.ObjectVariableNames);
            add("player", block.PlayerVariableNames);
            add("team", block.TeamVariableNames);
            add("timer", block.CustomTimerNames);
        }

        private static List<string> Safe(Func<IEnumerable<string>> get)
        {
            try { return (get() ?? Enumerable.Empty<string>()).ToList(); } catch (Exception) { return new List<string>(); }
        }

        private static string SafeText(Func<string> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }
    }
}
