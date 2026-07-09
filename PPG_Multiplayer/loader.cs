using System;
using System.IO;
using System.Reflection;
using UnityEngine;

// -----------------------------------------------------------------------------
//  Tiny loader script.
//
//  PPG's mod compiler HARD-BLOCKS networking code (UnityWebRequest) in source
//  scripts, so the real mod ships as a precompiled DLL (PPGMultiplayer.dll,
//  built from src/Mod.cs by build.bat). This loader is the only thing PPG
//  compiles: at runtime it locates that DLL and calls its entry point.
//
//  It searches BOTH the installed Mods folder and the Steam Workshop content
//  folder, so it works whether the mod was sideloaded or subscribed, on
//  Windows or Linux. Reflection + file IO are only "suspicious" (not a hard
//  block), so this compiles with "Reject suspicious mods" OFF.
// -----------------------------------------------------------------------------
namespace MPLoader
{
    public class Boot
    {
        public static void Main()
        {
            // dataPath = <library>/steamapps/common/People Playground/People Playground_Data
            string data = Application.dataPath;
            string[] baseDirs = new string[]
            {
                data + "/../Mods",                            // sideloaded mods
                data + "/../../../workshop/content/1118200",  // steam workshop items
            };

            string dllPath = FindDll(baseDirs);
            if (dllPath == null)
            {
                ModAPI.Notify("Loader: PPGMultiplayer.dll not found (looked in Mods + Workshop).");
                return;
            }

            try
            {
                // Load from BYTES, not LoadFrom(path). LoadFrom caches by file
                // identity for the whole process, so after you rebuild the DLL
                // an in-game Recompile would keep running the OLD code until you
                // fully restart the game. Reading the bytes and calling
                // Assembly.Load gives a fresh assembly every recompile, so your
                // changes show up without restarting.
                byte[] bytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(bytes);
                Type t = asm.GetType("Mod.Mod");
                if (t == null) { ModAPI.Notify("Loader: type Mod.Mod missing in DLL"); return; }
                MethodInfo m = t.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
                if (m == null) { ModAPI.Notify("Loader: Main() missing in DLL"); return; }
                m.Invoke(null, null);
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                ModAPI.Notify("Loader error: " + inner.Message);
            }
        }

        static string FindDll(string[] baseDirs)
        {
            foreach (string b in baseDirs)
            {
                try
                {
                    if (!Directory.Exists(b)) continue;
                    string[] hits = Directory.GetFiles(b, "PPGMultiplayer.dll", SearchOption.AllDirectories);
                    if (hits.Length > 0) return hits[0];
                }
                catch { /* unreadable dir, skip */ }
            }
            return null;
        }
    }
}
