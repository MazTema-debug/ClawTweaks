// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LegionTweaks
{
    internal interface ILosslessScalingProbe
    {
        string[] InstallationDirectories();
        bool ExecutableExists(string path);
        string FileVersion(string path);
        string[] RunningImages(); // null item means a matching process could not be inspected.
    }

    internal sealed class LosslessScalingDetector : ILosslessScalingStatusProvider
    {
        private readonly ILosslessScalingProbe probe;
        public bool Supported { get; }
        internal LosslessScalingDetector(bool supported, ILosslessScalingProbe probe = null)
        { Supported = supported; this.probe = probe ?? new WindowsLosslessScalingProbe(); }

        public LosslessScalingStatus Inspect()
        {
            var result = new LosslessScalingStatus { Supported = Supported, CapturedAtUtc = DateTime.UtcNow.ToString("o") };
            if (!Supported) { result.Message = "Integración no disponible en este equipo."; return result; }
            try {
                var directories = probe.InstallationDirectories();
                if (directories == null || directories.Length > 8) throw new InvalidDataException();
                var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories) {
                    if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || !Path.IsPathRooted(directory)
                        || directory.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException();
                    string executable = Path.GetFullPath(Path.Combine(directory, "LosslessScaling.exe"));
                    if (probe.ExecutableExists(executable)) executables.Add(executable);
                }
                result.Installed = executables.Count != 0;
                if (!result.Installed) { result.Message = "No se encontró una instalación de Lossless Scaling registrada por Steam."; return result; }
                string selected = null;
                foreach (string executable in executables) selected = executable;
                bool uncertain = false, running = false;
                var runningExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try {
                    string[] images = probe.RunningImages();
                    if (images == null || images.Length > 64) uncertain = true;
                    else foreach (string image in images) {
                        if (image == null) { uncertain = true; continue; }
                        if (executables.Contains(image)) { selected = image; running = true; runningExecutables.Add(image); }
                    }
                }
                catch (Exception) { uncertain = true; }
                result.Running = running ? true : uncertain ? (bool?)null : false;
                // Multiple installations without a confirmed running image have
                // no single version. Never choose a registry entry arbitrarily.
                if (executables.Count == 1 || runningExecutables.Count == 1) {
                    try {
                        string version = probe.FileVersion(selected);
                        if (!string.IsNullOrWhiteSpace(version) && version.Length <= 64) {
                            bool valid = true;
                            foreach (char c in version) if (!(char.IsDigit(c) || c == '.' || c == ',' || c == ' ' || c == '-')) valid = false;
                            if (valid) result.FileVersion = version.Trim();
                        }
                    }
                    catch (Exception) { /* Installation/running evidence is independent of version metadata. */ }
                }
                result.Message = result.Running == true ? "Lossless Scaling está en ejecución."
                    : result.Running == false ? "Lossless Scaling está instalado y cerrado."
                    : "Lossless Scaling está instalado; no se pudo confirmar si está abierto.";
            }
            catch (Exception) { result.Installed = false; result.Running = null; result.FileVersion = null;
                result.Message = "No se pudo completar la detección de Lossless Scaling. Vuelve a consultar."; }
            return result;
        }
    }

    internal sealed class WindowsLosslessScalingProbe : ILosslessScalingProbe
    {
        public string[] InstallationDirectories()
        {
            const string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 993090";
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(keyName, false)) {
                        if (key == null) continue;
                        if (!string.Equals(key.GetValue("DisplayName") as string, "Lossless Scaling", StringComparison.OrdinalIgnoreCase)) continue;
                        string directory = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(directory)) result.Add(directory);
                    }
            var values = new string[result.Count]; result.CopyTo(values); return values;
        }

        public bool ExecutableExists(string path)
        {
            // Inspect a fixed registered file, never scan disks or follow links.
            try {
                var file = new FileInfo(path);
                if (!file.Exists) return false;
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidDataException();
                for (var directory = file.Directory; directory != null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        public string FileVersion(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion;
        public string[] RunningImages()
        {
            var values = new List<string>();
            var processes = Process.GetProcessesByName("LosslessScaling");
            try {
                foreach (var process in processes) {
                    if (values.Count >= 64) throw new InvalidDataException();
                    try { values.Add(process.MainModule?.FileName); }
                    catch (Win32Exception) { values.Add(null); }
                    catch (InvalidOperationException) { /* Exited while being inspected. */ }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return values.ToArray();
        }
    }
}
