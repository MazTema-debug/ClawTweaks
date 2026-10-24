// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    private static GameCompatibilityEntry CompatibleGame(string path = @"D:\Games\Baldur's Gate 3\bin\bg3.exe") =>
        new GameCompatibilityEntry { Id = Guid.NewGuid().ToString("D"), Name = "BG3", Executable = path, DisableSdlRawInput = true };
    private static void GameCompatibilityTests()
    {
        Test("Game access catalog round trips per-game options without modifying the source", () => {
            var catalog = new GameCompatibilityCatalog(); catalog.Games.Add(CompatibleGame());
            var copy = Protocol.Decode<GameCompatibilityCatalog>(Protocol.Encode(catalog)); copy.Validate();
            Assert(copy.Games[0].Executable == catalog.Games[0].Executable && copy.Games[0].DisableSdlRawInput, "game settings were lost");
            var edited = copy.Copy(); edited.Games[0].Name = "Another game"; edited.Games[0].DisableSdlRawInput = false;
            Assert(copy.Games[0].Name == "BG3" && copy.Games[0].DisableSdlRawInput, "editing mutated the saved configuration");
        });
        Test("Game access rejects command lines, URI, UNC, control characters and ambiguous paths", () => {
            foreach (var path in new[] { "steam://run/123.exe", @"\\server\game.exe", @"\\?\C:\game.exe", @"C:game.exe",
                "C:\\game.exe\" & bad.exe", "C:\\game.exe\r\nbad.exe", @"C:\a\..\game.exe", @"C:\a\.\game.exe",
                @"C:\a\\game.exe", @"C:\game.exe:other.exe", @"C:\a.\game.exe", @"C:\a\*.exe" }) {
                MustFail(() => GameCompatibilityAccess.Create(CompatibleGame(path)));
            }
            var game = CompatibleGame(); game.Id = Guid.Empty.ToString("D"); MustFail(game.Validate);
            game = CompatibleGame(); game.Name = "bad\nname"; MustFail(game.Validate);
        });
        Test("Game access catalog rejects duplicate identities, unknown versions and excess entries", () => {
            var catalog = new GameCompatibilityCatalog(); var game = CompatibleGame(); catalog.Games.Add(game); catalog.Games.Add(game.Copy());
            MustFail(catalog.Validate); catalog.Games.RemoveAt(1); catalog.Version = 2; MustFail(catalog.Validate);
            catalog.Version = 1; for (int i = 0; i < 8; i++) catalog.Games.Add(CompatibleGame()); MustFail(catalog.Validate);
        });
        Test("Game access applies the SDL setting only inside a literal local launch access", () => {
            var game = CompatibleGame(@"D:\Games\A & B ! 100% ^ (ñ)\game.exe");
            game.Name = "Name & echo should never become a command";
            string access = GameCompatibilityAccess.Create(game);
            Assert(access.Contains("setlocal DisableDelayedExpansion\r\nset \"SDL_JOYSTICK_RAWINPUT=0\"")
                && access.Contains("100%%") && !access.Contains(game.Name) && !access.Contains("setx") && !access.Contains("runas"), "unsafe scope or shell input");
            game.DisableSdlRawInput = false;
            Assert(!GameCompatibilityAccess.Create(game).Contains("SDL_JOYSTICK_RAWINPUT"), "an unselected option changed the inherited environment");
            Assert(!GameCompatibilityAccess.SuggestedName("Bad:/\\?name").Any(c => ":/\\?".IndexOf(c) >= 0), "unsafe suggested filename");
        });
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            Test("Windows executes the generated access with literal special-character paths and scoped environment", () => WithRecordingDirectory(root => {
                string folder = Path.Combine(root, "game & ! 100% ^ (ñ)"); Directory.CreateDirectory(folder);
                string exe = Path.Combine(folder, "probe & ! 100% ^ (ñ).exe"), source = Path.Combine(root, "probe.cs"), marker = Path.Combine(root, "observed.txt");
                File.WriteAllText(source, "using System; using System.IO; class Probe { static void Main() { File.WriteAllText(Environment.GetEnvironmentVariable(\"LEGIONTWEAKS_TEST_ACCESS_MARKER\"), (Environment.GetEnvironmentVariable(\"SDL_JOYSTICK_RAWINPUT\") ?? \"missing\") + \"|\" + Environment.CurrentDirectory); } }", new UTF8Encoding(false));
                string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
                Assert(File.Exists(compiler), "the Windows launcher check requires the installed .NET Framework compiler");
                var compile = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string arg in new[] { "/nologo", "/target:winexe", "/out:" + exe, source }) compile.ArgumentList.Add(arg);
                using (var process = Process.Start(compile)) {
                    if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("probe compilation timed out"); }
                    Assert(process.ExitCode == 0 && File.Exists(exe), "probe could not be prepared");
                }
                string previous = Environment.GetEnvironmentVariable("SDL_JOYSTICK_RAWINPUT");
                try {
                    foreach (bool disabled in new[] { true, false }) {
                        if (File.Exists(marker)) File.Delete(marker);
                        var game = CompatibleGame(exe); game.DisableSdlRawInput = disabled;
                        string batch = Path.Combine(root, "test-access.cmd"); File.WriteAllText(batch, GameCompatibilityAccess.Create(game), new UTF8Encoding(false));
                        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = "/d /c \"\"" + batch + "\"\"", RedirectStandardInput = true };
                        start.Environment["LEGIONTWEAKS_TEST_ACCESS_MARKER"] = marker;
                        start.Environment["SDL_JOYSTICK_RAWINPUT"] = "inherited";
                        using (var process = Process.Start(start)) {
                            process.StandardInput.Close();
                            if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("launch access timed out"); }
                            Assert(process.ExitCode == 0, "launch access failed");
                        }
                        for (int i = 0; i < 100 && !File.Exists(marker); i++) Thread.Sleep(50);
                        Assert(File.Exists(marker) && File.ReadAllText(marker) == (disabled ? "0" : "inherited") + "|" + folder,
                            "the access changed a literal path, inherited environment or working directory");
                        Assert(Environment.GetEnvironmentVariable("SDL_JOYSTICK_RAWINPUT") == previous, "child settings escaped to the test process");
                    }
                } finally { /* All environment changes belong to ProcessStartInfo, not the test process. */ }
            }));
        }
    }
}
