// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace LegionTweaks
{
    [DataContract]
    public sealed class GameCompatibilityEntry
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Executable { get; set; }
        [DataMember] public bool DisableSdlRawInput { get; set; }
        public GameCompatibilityEntry Copy() => new GameCompatibilityEntry {
            Id = Id, Name = Name, Executable = Executable, DisableSdlRawInput = DisableSdlRawInput };
        public void Validate()
        {
            if (!Guid.TryParseExact(Id, "D", out var id) || id == Guid.Empty) throw new ArgumentException("Identidad de juego inválida.");
            if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || Name.Length > 80 || Name.Any(char.IsControl))
                throw new ArgumentException("Escribe un nombre de juego de 1–80 caracteres.");
            // Accept a literal local Windows executable, not shell input, a URI,
            // an environment-variable path, UNC/device path or a command line.
            // This validation is independent of the OS running the unit tests.
            if (string.IsNullOrEmpty(Executable) || Executable.Length > 512 || Executable.Length < 8
                || !IsDriveLetter(Executable[0]) || Executable[1] != ':' || Executable[2] != '\\'
                || !Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || Executable.Any(c => char.IsControl(c) || "\"<>|?*/".IndexOf(c) >= 0)
                || Executable.IndexOf(':', 2) >= 0)
                throw new ArgumentException("Selecciona un ejecutable .exe en una unidad local.");
            foreach (var segment in Executable.Substring(3).Split('\\'))
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(" ") || segment.EndsWith("."))
                    throw new ArgumentException("La ruta del juego no es válida.");
        }
        private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }

    [DataContract]
    public sealed class GameCompatibilityCatalog
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public List<GameCompatibilityEntry> Games { get; set; } = new List<GameCompatibilityEntry>();
        public GameCompatibilityCatalog Copy() => new GameCompatibilityCatalog { Version = Version, Games = Games.Select(x => x.Copy()).ToList() };
        public void Validate()
        {
            if (Version != 1 || Games == null || Games.Count > 8 || Games.Any(x => x == null))
                throw new ArgumentException("La lista de accesos de juegos no es válida (máximo 8).");
            foreach (var game in Games) game.Validate();
            if (Games.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Games.Count)
                throw new ArgumentException("Hay identidades de juego repetidas.");
        }
    }

    public static class GameCompatibilityAccess
    {
        // User-generated launch access, never executed by the elevated helper.
        // Batch '%' escaping plus disabled delayed expansion preserves literal
        // paths containing %, !, &, ^ or parentheses without shell evaluation.
        public static string Create(GameCompatibilityEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            game.Validate();
            string executable = game.Executable.Replace("%", "%%");
            string directory = executable.Substring(0, executable.LastIndexOf('\\') + 1);
            var text = new StringBuilder("@echo off\r\nchcp 65001 >nul\r\nsetlocal DisableDelayedExpansion\r\n");
            if (game.DisableSdlRawInput) text.Append("set \"SDL_JOYSTICK_RAWINPUT=0\"\r\n");
            // Do not put the display name or optional arguments into commands.
            text.Append("if not exist \"").Append(executable).Append("\" goto missing\r\n")
                .Append("start \"\" /D \"").Append(directory).Append("\" \"").Append(executable).Append("\"\r\n")
                .Append("if errorlevel 1 goto failed\r\nexit /b 0\r\n")
                .Append(":missing\r\necho No se encuentra el ejecutable. Genera de nuevo el acceso desde LegionTweaks.\r\npause\r\nexit /b 1\r\n")
                .Append(":failed\r\necho Windows no pudo abrir el juego.\r\npause\r\nexit /b 1\r\n");
            return text.ToString();
        }
        public static string SuggestedName(string name)
        {
            var safe = new string((name ?? "Juego").Where(c => !char.IsControl(c) && "\\/:*?\"<>|".IndexOf(c) < 0).ToArray()).Trim(' ', '.');
            return (string.IsNullOrEmpty(safe) ? "Juego" : safe) + " - LegionTweaks";
        }
    }
}
