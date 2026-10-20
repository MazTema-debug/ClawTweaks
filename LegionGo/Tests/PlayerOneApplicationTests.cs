// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void PlayerOneApplicationTests()
    {
        Test("Mode preparation permits the Steam client and its web helper", () => {
            PlayerOneApplications.Check(new[] {
                new ControllerApplication { Name = "steam", Executable = @"C:\Program Files (x86)\Steam\steam.exe" },
                new ControllerApplication { Name = "steamwebhelper", Executable = @"C:\Program Files (x86)\Steam\bin\cef\steamwebhelper.exe" }
            });
        });
        Test("Mode preparation rejects known games even when their image cannot be inspected", () => {
            foreach(string name in new[] { "Diablo IV", "DiabloIV.exe", "DIABLOIV", "Dungeons", "Minecraft.Windows.exe" })
                MustFail(() => PlayerOneApplications.Check(new[] { new ControllerApplication { Name = name } }));
        });
        Test("Lossless Scaling is recognized as a Steam-hosted desktop utility", () => {
            foreach(string path in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe", "D:/SteamLibrary/STEAMAPPS/common/Lossless Scaling/LosslessScaling.exe" })
                PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "LosslessScaling", Executable = path } });
        });
        Test("A scaling-like name or directory does not exempt another Steam-library process", () => {
            foreach(var app in new[] {
                new ControllerApplication { Name = "LosslessScaling", Executable = @"D:\Steam\steamapps\common\Other Game\LosslessScaling.exe" },
                new ControllerApplication { Name = "Other", Executable = @"D:\Steam\steamapps\common\Lossless Scaling\Other.exe" },
                new ControllerApplication { Name = "LosslessScaling", Executable = @"D:\Steam\steamapps\common\Lossless Scaling\subfolder\LosslessScaling.exe" }
            }) MustFail(() => PlayerOneApplications.Check(new[] { app }));
        });
        Test("Mode preparation rejects Steam-library applications regardless of name and path casing", () => {
            foreach(string path in new[] { @"D:\SteamLibrary\steamapps\common\Game\Game.exe", @"E:\Juegos\STEAMAPPS\common\Árbol\renamed.exe", "F:/SteamLibrary/steamapps/common/Game/game.exe", @"C:\Steam\steamapps\sourcemods\Mod\game.exe" })
                MustFail(() => PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "renamed", Executable = path } }));
        });
        Test("Similar directory names and ordinary desktop applications do not masquerade as Steam games", () => {
            foreach(string path in new[] { @"C:\Tools\steamapps-old\common\Tool.exe", @"C:\Tools\mysteamapps\Tool.exe", @"C:\Tools\steamapps.exe", @"C:\Program Files\OrcaSlicer\orca-slicer.exe" })
                PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "tool", Executable = path } });
        });
        Test("An absent application snapshot is refused before any preparation is authorized", () => {
            MustFail(() => PlayerOneApplications.Check(null));
            MustFail(() => PlayerOneApplications.Check(new ControllerApplication[] { null }));
        });
        Test("Protected unrelated process metadata is not classified as a game by default", () => {
            PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "System" }, new ControllerApplication { Name = "wininit" } });
        });
        Test("Steam alone permits the single durable Desktop startup attempt", () => {
            var f = new StartupFixture(true);
            PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "steam" } });
            var p = f.Create(); p.RunAtStartup(); p.RunAtStartup();
            Assert(f.Starts == 1 && f.Store.Attempt.State == "starting", "Steam client prevented or repeated Desktop startup");
        });
        Test("A detected game skips Desktop startup without controller or journal mutation", () => {
            var f = new StartupFixture(true);
            try { PlayerOneApplications.Check(new[] { new ControllerApplication { Name = "Diablo IV" } }); }
            catch(InvalidOperationException ex) { f.Skip = ex.Message; }
            var p = f.Create(); p.RunAtStartup();
            Assert(f.Starts == 0 && f.Store.AttemptWrites == 0 && p.GetStatus().State == "skipped" && p.GetStatus().Message.Contains("juego"), "game did not stop preparation before intent");
        });
    }
}
