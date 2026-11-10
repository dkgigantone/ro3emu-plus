using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Diagnostic only (not for release): shows what the client's own Lua does when the Hunter's falcon should fly for
    /// Blitz Beat. Off unless the file &lt;client&gt;\ro3emu\custom\falcon-trace.txt exists. The client Lua is compiled
    /// and cannot be read, so this lists the functions of the falcon and bullet modules and wraps each one with a C# 
    /// delegate (the route LuaLogs uses) that logs the call, its first arguments and its first result to Player.log with
    /// the prefix "[Ro3Emu.FalconTrace]", then calls the original. A wrapped function only hands back its first result.
    /// Methods whose names contain Update or Tick are logged once and then only counted.
    /// </summary>
    internal static class FalconTrace
    {
        public delegate object TraceMethod(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12);

        /// <summary>Modules traced in full; required by name, whatever the file's case.</summary>
        private static readonly string[] Modules =
        {
            "lc_pendant_falcon", "lc_pendant_manager", "lc_bullet_helper", "lc_bullet_mgr", "lc_bulletemit_mgr", "lc_bullet", "lc_unit_bullet",
        };

        /// <summary>Modules traced only for functions whose name contains "ullet" (they are large and busy).</summary>
        private static readonly string[] BulletOnlyModules = { "lc_scene_sync", "lc_performeffect_agent", "lc_perform_effect_agent" };

        private const int MaxLines = 4000;
        private static int _lines;
        private static object _doneEnv;
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly List<Probe> Probes = new List<Probe>();

        private sealed class Probe
        {
            public string Module, Name;
            public Obf_R.Obf_Ef Original;
            public TraceMethod Wrapper;
            public int Calls;
            public bool Busy;

            public object Call(object a1, object a2, object a3, object a4, object a5, object a6, object a7, object a8, object a9, object a10, object a11, object a12)
            {
                int n = ++Calls;
                bool log = !Busy || n == 1;
                string args = log ? Show(a1) + ", " + Show(a2) + ", " + Show(a3) + ", " + Show(a4) + ", " + Show(a5) : null;
                object[] result;
                try { result = Original.Obf_HuA(a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12); }
                catch (Exception e)
                {
                    Log(Module + "." + Name + " #" + n + " (" + args + ") THREW " + e.GetType().Name + ": " + e.Message);
                    throw;
                }
                object first = result != null && result.Length > 0 ? result[0] : null;
                if (log) Log(Module + "." + Name + " #" + n + " (" + args + ") -> " + Show(first));
                return first;
            }
        }

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global, object env)
        {
            try
            {
                if (ReferenceEquals(env, _doneEnv)) return;
                var path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ro3emu", "custom", "falcon-trace.txt");
                if (!File.Exists(path)) return;
                _doneEnv = env;
                Probes.Clear();
                _lines = 0;
                var next = global.Obf_LuA<Obf_R.Obf_Ef>("next");
                if (next == null) { Log("Lua next not found; nothing traced"); return; }
                foreach (var name in Modules) Hook(manager, global, next, name, false);
                foreach (var name in BulletOnlyModules) Hook(manager, global, next, name, true);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.FalconTrace] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Hook(LA_LuaManager manager, Obf_R.Obf_ff global, Obf_R.Obf_Ef next, string module, bool bulletOnly)
        {
            var table = LuaPatches.Module(manager, global, module);
            if (table == null) { Log(module + ": module not found"); return; }
            var names = new List<string>();
            Collect(next, table, names);
            foreach (var holder in new[] { "vtbl", "__index" })
                if (table.Obf_RuA<object>(holder) is Obf_R.Obf_ff inner && !ReferenceEquals(inner, table)) Collect(next, inner, names);
            Log(module + ": functions = " + string.Join(", ", names.ToArray()));
            int wrapped = 0;
            foreach (var name in names)
            {
                if (bulletOnly && name.IndexOf("ullet", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var original = table.Obf_RuA<Obf_R.Obf_Ef>(name);
                if (original == null) continue;
                var probe = new Probe
                {
                    Module = module, Name = name, Original = original,
                    Busy = name.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) >= 0,
                };
                probe.Wrapper = probe.Call;
                Probes.Add(probe);
                table.Obf_luA<string, TraceMethod>(name, probe.Wrapper);
                wrapped++;
            }
            Log(module + ": " + wrapped + " function(s) wrapped");
        }

        /// <summary>The string keys of a table whose value is a function.</summary>
        private static void Collect(Obf_R.Obf_Ef next, Obf_R.Obf_ff table, List<string> names)
        {
            object key = null;
            for (int guard = 0; guard < 2000; guard++)
            {
                var pair = next.Obf_HuA(table, key);
                if (pair == null || pair.Length < 2 || pair[0] == null) return;
                key = pair[0];
                if (key is string text && pair[1] is Obf_R.Obf_Ef && !names.Contains(text)) names.Add(text);
            }
        }

        private static string Show(object value)
        {
            if (value == null) return "nil";
            if (value is string text) return "\"" + (text.Length > 40 ? text.Substring(0, 40) : text) + "\"";
            if (value is bool flag) return flag ? "true" : "false";
            if (value is long || value is int || value is double || value is float) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is Obf_R.Obf_ff) return "table";
            if (value is Obf_R.Obf_Ef) return "function";
            return value.GetType().Name;
        }

        private static void Log(string message)
        {
            if (++_lines > MaxLines) return;
            Debug.Log("[Ro3Emu.FalconTrace] " + message);
        }
    }
}
